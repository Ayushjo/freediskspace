import Darwin
import Foundation

/// How the space an APFS container reports as used splits across its
/// volumes. statfs gives the same container-wide blocks and free space for
/// every volume in a container, so "used" was the whole container: on a
/// real Mac ~49 GB of macOS, swap, Preboot and Recovery read as unexplained
/// "snapshots or purgeable". `getattrlist(ATTR_VOL_SPACEUSED)` gives each
/// mounted volume's own figure without root (matches `diskutil apfs list`
/// "Capacity Consumed"). Research: docs/STORAGE-RESEARCH-PLAN.md §1, B1.
public struct ContainerBreakdown: Sendable, Equatable {
    public enum Role: String, Sendable, Equatable {
        case data, system, swap, preboot, update, other

        /// What a person calls it.
        public var title: String {
            switch self {
            case .data: return "Your files and apps"
            case .system: return "macOS"
            case .swap: return "Swap (virtual memory)"
            case .preboot: return "Startup files (Preboot)"
            case .update: return "macOS update staging"
            case .other: return "Other volume"
            }
        }
    }

    public struct Volume: Sendable, Equatable {
        public var mountPoint: String
        public var role: Role
        public var usedBytes: Int64
        public init(mountPoint: String, role: Role, usedBytes: Int64) {
            self.mountPoint = mountPoint
            self.role = role
            self.usedBytes = usedBytes
        }
    }

    /// One mounted volume as the OS reports it; the input to `build`.
    public struct Mount: Sendable, Equatable {
        public var mountPoint: String
        /// `f_mntfromname`, e.g. "/dev/disk3s5" or "/dev/disk3s1s1".
        public var device: String
        public var fsType: String
        public var usedBytes: Int64?
        public init(mountPoint: String, device: String, fsType: String, usedBytes: Int64?) {
            self.mountPoint = mountPoint
            self.device = device
            self.fsType = fsType
            self.usedBytes = usedBytes
        }
    }

    public var containerTotalBytes: Int64
    public var containerFreeBytes: Int64
    public var volumes: [Volume]
    /// `vm.swapusage` used, when known — swap in use right now, which can be
    /// less than what the VM volume holds.
    public var swapInUseBytes: Int64?

    public var containerUsedBytes: Int64 { max(0, containerTotalBytes - containerFreeBytes) }
    public var mountedUsedBytes: Int64 { volumes.reduce(0) { $0 + $1.usedBytes } }
    /// Used but on no mounted volume: Recovery (not mounted while macOS
    /// runs) plus APFS metadata. Never negative.
    public var unmountedAndMetadataBytes: Int64 { max(0, containerUsedBytes - mountedUsedBytes) }
    public var dataUsedBytes: Int64? { volumes.first { $0.role == .data }?.usedBytes }

    public init(containerTotalBytes: Int64, containerFreeBytes: Int64, volumes: [Volume], swapInUseBytes: Int64? = nil) {
        self.containerTotalBytes = containerTotalBytes
        self.containerFreeBytes = containerFreeBytes
        self.volumes = volumes
        self.swapInUseBytes = swapInUseBytes
    }

    /// The APFS container a device node belongs to: "/dev/disk3s1s1" → "disk3".
    static func containerID(device: String) -> String? {
        guard device.hasPrefix("/dev/disk") else { return nil }
        let rest = device.dropFirst("/dev/disk".count)
        let digits = rest.prefix { $0.isNumber }
        guard !digits.isEmpty, rest.dropFirst(digits.count).first == "s" else { return nil }
        return "disk" + digits
    }

    static func role(mountPoint: String, isRootContainerVolume: Bool) -> Role {
        switch mountPoint {
        case "/": return .system
        case "/System/Volumes/Data": return .data
        case "/System/Volumes/VM": return .swap
        case "/System/Volumes/Preboot": return .preboot
        case "/System/Volumes/Update": return .update
        default: return isRootContainerVolume ? .other : .data
        }
    }

    /// Pure: the volumes sharing the scan root's container, with their own
    /// used figures. Nil when the root isn't on APFS or its container can't
    /// be told apart.
    static func build(rootDevice: String, rootFsType: String, containerTotal: Int64, containerFree: Int64,
                      mounts: [Mount], swapInUse: Int64?) -> ContainerBreakdown? {
        guard rootFsType == "apfs", let container = containerID(device: rootDevice) else { return nil }
        let siblings = mounts.filter { $0.fsType == "apfs" && containerID(device: $0.device) == container }
        guard !siblings.isEmpty else { return nil }
        let isBootContainer = siblings.contains { $0.mountPoint == "/" }
        var volumes: [Volume] = []
        var seen = Set<String>()
        for mount in siblings {
            guard let used = mount.usedBytes, used >= 0, seen.insert(mount.mountPoint).inserted else { continue }
            volumes.append(Volume(mountPoint: mount.mountPoint,
                                  role: role(mountPoint: mount.mountPoint, isRootContainerVolume: isBootContainer),
                                  usedBytes: used))
        }
        guard !volumes.isEmpty else { return nil }
        volumes.sort { $0.usedBytes > $1.usedBytes }
        return ContainerBreakdown(containerTotalBytes: containerTotal, containerFreeBytes: containerFree,
                                  volumes: volumes, swapInUseBytes: swapInUse)
    }

    /// Live: reads statfs for every mount, `ATTR_VOL_SPACEUSED` per APFS
    /// volume and `vm.swapusage`. Local only; no root.
    public static func read(forPath path: String) -> ContainerBreakdown? {
        var root = statfs()
        guard path.withCString({ statfs($0, &root) }) == 0 else { return nil }
        let block = Int64(root.f_bsize)
        var list: UnsafeMutablePointer<statfs>?
        let count = getmntinfo(&list, MNT_NOWAIT)
        guard count > 0, let list else { return nil }
        var mounts: [Mount] = []
        for i in 0..<Int(count) {
            var fs = list[i]
            let mountPoint = cString(&fs.f_mntonname)
            let device = cString(&fs.f_mntfromname)
            let type = cString(&fs.f_fstypename)
            mounts.append(Mount(mountPoint: mountPoint, device: device, fsType: type,
                                usedBytes: type == "apfs" ? spaceUsed(mountPoint) : nil))
        }
        return build(rootDevice: cString(&root.f_mntfromname), rootFsType: cString(&root.f_fstypename),
                     containerTotal: Int64(root.f_blocks) * block, containerFree: Int64(root.f_bavail) * block,
                     mounts: mounts, swapInUse: swapInUse())
    }

    private static func cString<T>(_ field: inout T) -> String {
        withUnsafePointer(to: &field) { ptr in
            ptr.withMemoryRebound(to: CChar.self, capacity: MemoryLayout<T>.size) { String(cString: $0) }
        }
    }

    /// `getattrlist(ATTR_VOL_INFO | ATTR_VOL_SPACEUSED)`: the volume's own
    /// used bytes. Constants checked against the SDK's sys/attr.h
    /// (ATTR_VOL_SPACEUSED 0x00800000, ATTR_VOL_INFO 0x80000000).
    static func spaceUsed(_ mountPoint: String) -> Int64? {
        var request = attrlist()
        request.bitmapcount = u_short(ATTR_BIT_MAP_COUNT)
        request.volattr = attrgroup_t(UInt32(ATTR_VOL_INFO) | UInt32(ATTR_VOL_SPACEUSED))
        // u_int32_t length, then off_t.
        var buffer = [UInt8](repeating: 0, count: 64)
        let rc = buffer.withUnsafeMutableBytes { raw in
            mountPoint.withCString { getattrlist($0, &request, raw.baseAddress, raw.count, 0) }
        }
        guard rc == 0 else { return nil }
        let length = buffer.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 0, as: UInt32.self) }
        guard length >= 12 else { return nil }
        let used = buffer.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 4, as: Int64.self) }
        return used >= 0 ? used : nil
    }

    /// `vm.swapusage` "used", in bytes.
    static func swapInUse() -> Int64? {
        var usage = xsw_usage()
        var size = MemoryLayout<xsw_usage>.size
        guard sysctlbyname("vm.swapusage", &usage, &size, nil, 0) == 0 else { return nil }
        return Int64(usage.xsu_used)
    }
}
