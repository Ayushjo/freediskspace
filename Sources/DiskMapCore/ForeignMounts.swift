import Foundation

/// Mount points inside a scan root that belong to another volume.
///
/// The walk decides "another volume?" from `ATTR_CMN_DEVID`, but
/// `getattrlistbulk` reports a mount point with the device of the folder it
/// is mounted on, not the volume mounted there. So a "/" scan walked into
/// /System/Volumes/VM (swap), Preboot and every drive under /Volumes and
/// counted them as this disk. Reading the mount table once at the start
/// gives the exact list to stop at. Verified 2026-10-22: `stat` on
/// /System/Volumes/VM and /Volumes/<NTFS drive> returns their own device,
/// while "/" and /System/Volumes/Data share one (firmlinks).
enum ForeignMounts {
    struct Mount: Equatable {
        var path: String
        var device: Int32
    }

    /// Pure: mounts strictly under `root` whose device isn't the root's.
    static func paths(under root: String, rootDevice: Int32, mounts: [Mount]) -> Set<String> {
        let base = root == "/" ? "/" : (root.hasSuffix("/") ? String(root.dropLast()) : root)
        let prefix = base == "/" ? "/" : base + "/"
        var out = Set<String>()
        for mount in mounts where mount.device != rootDevice {
            let path = mount.path.count > 1 && mount.path.hasSuffix("/") ? String(mount.path.dropLast()) : mount.path
            if path != base, path.hasPrefix(prefix) { out.insert(path) }
        }
        return out
    }

    /// File systems that are never this disk and must not be touched while
    /// building the list: an `lstat` on a stale network share can hang.
    static let remoteTypes: Set<String> = ["nfs", "smbfs", "afpfs", "webdav", "autofs", "ftp", "cifs", "macfuse", "osxfuse"]

    /// Live: the current mount table. A local mount's device comes from
    /// `lstat`, the same source as the root's: `f_fsid` differs from st_dev
    /// for "/" (the sealed system snapshot vs the Data volume its firmlinks
    /// show), measured 2026-10-22. Remote and automount points get -1, which
    /// never equals a root device, so they are always stopped at.
    static func current() -> [Mount] {
        var list: UnsafeMutablePointer<statfs>?
        let count = getmntinfo(&list, MNT_NOWAIT)
        guard count > 0, let list else { return [] }
        var mounts: [Mount] = []
        for i in 0..<Int(count) {
            var fs = list[i]
            let path = cString(&fs.f_mntonname)
            let type = cString(&fs.f_fstypename)
            var device: Int32 = -1
            if !remoteTypes.contains(type) {
                var info = stat()
                if lstat(path, &info) == 0 { device = Int32(info.st_dev) }
            }
            mounts.append(Mount(path: path, device: device))
        }
        return mounts
    }

    private static func cString<T>(_ field: inout T) -> String {
        withUnsafePointer(to: &field) { ptr in
            ptr.withMemoryRebound(to: CChar.self, capacity: MemoryLayout<T>.size) { String(cString: $0) }
        }
    }
}
