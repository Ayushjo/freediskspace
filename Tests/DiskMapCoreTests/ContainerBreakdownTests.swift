import Foundation
import Testing
@testable import DiskMapCore

/// statfs gives every APFS volume the container's blocks and free space, so
/// "used" was the whole container. These split it per volume (B1).
@Suite("APFS container breakdown")
struct ContainerBreakdownTests {
    private let gb: Int64 = 1_000_000_000

    @Test func deviceNodesMapToTheirContainer() {
        #expect(ContainerBreakdown.containerID(device: "/dev/disk3s1s1") == "disk3")
        #expect(ContainerBreakdown.containerID(device: "/dev/disk3s5") == "disk3")
        #expect(ContainerBreakdown.containerID(device: "/dev/disk12s2") == "disk12")
        #expect(ContainerBreakdown.containerID(device: "/dev/disk3") == nil)
        #expect(ContainerBreakdown.containerID(device: "map auto_home") == nil)
        #expect(ContainerBreakdown.containerID(device: "//server/share") == nil)
    }

    /// The real layout of the maintainer's Mac (macOS 26.5), figures from
    /// diskutil: only disk3's volumes count; disk2 (firmware) and the
    /// network share don't.
    @Test func bootContainerSplitsIntoNamedVolumes() throws {
        let mounts: [ContainerBreakdown.Mount] = [
            .init(mountPoint: "/", device: "/dev/disk3s1s1", fsType: "apfs", usedBytes: 12_570_000_000),
            .init(mountPoint: "/System/Volumes/Data", device: "/dev/disk3s5", fsType: "apfs", usedBytes: 346_440_000_000),
            .init(mountPoint: "/System/Volumes/VM", device: "/dev/disk3s6", fsType: "apfs", usedBytes: 23_620_000_000),
            .init(mountPoint: "/System/Volumes/Preboot", device: "/dev/disk3s2", fsType: "apfs", usedBytes: 10_750_000_000),
            .init(mountPoint: "/System/Volumes/Update", device: "/dev/disk3s4", fsType: "apfs", usedBytes: 4_000_000),
            .init(mountPoint: "/System/Volumes/xarts", device: "/dev/disk2s2", fsType: "apfs", usedBytes: 6_000_000),
            .init(mountPoint: "/Volumes/Share", device: "//me@nas/share", fsType: "smbfs", usedBytes: nil),
        ]
        let breakdown = try #require(ContainerBreakdown.build(
            rootDevice: "/dev/disk3s5", rootFsType: "apfs",
            containerTotal: 494_380_000_000, containerFree: 98_310_000_000, mounts: mounts, swapInUse: 20_600_000_000))
        #expect(breakdown.volumes.count == 5)
        #expect(breakdown.volumes.first?.role == .data)
        #expect(breakdown.dataUsedBytes == 346_440_000_000)
        #expect(breakdown.volumes.contains { $0.role == .swap && $0.usedBytes == 23_620_000_000 })
        #expect(breakdown.containerUsedBytes == 396_070_000_000)
        // Recovery + metadata is what no mounted volume accounts for.
        #expect(breakdown.unmountedAndMetadataBytes == 396_070_000_000 - 393_384_000_000)
        #expect(breakdown.mountedUsedBytes + breakdown.unmountedAndMetadataBytes == breakdown.containerUsedBytes)
    }

    @Test func notAPFSOrUnknownDeviceHasNoBreakdown() {
        #expect(ContainerBreakdown.build(rootDevice: "/dev/disk4s1", rootFsType: "exfat", containerTotal: 10 * gb,
                                         containerFree: gb, mounts: [], swapInUse: nil) == nil)
        #expect(ContainerBreakdown.build(rootDevice: "//nas/share", rootFsType: "apfs", containerTotal: 10 * gb,
                                         containerFree: gb, mounts: [], swapInUse: nil) == nil)
    }

    /// An external APFS disk: one data volume, nothing named "macOS".
    @Test func externalDiskIsJustItsVolume() throws {
        let mounts: [ContainerBreakdown.Mount] = [
            .init(mountPoint: "/Volumes/Archive", device: "/dev/disk6s1", fsType: "apfs", usedBytes: 700 * gb),
        ]
        let breakdown = try #require(ContainerBreakdown.build(
            rootDevice: "/dev/disk6s1", rootFsType: "apfs", containerTotal: 1_000 * gb, containerFree: 299 * gb,
            mounts: mounts, swapInUse: nil))
        #expect(breakdown.volumes.map(\.role) == [.data])
        #expect(breakdown.unmountedAndMetadataBytes == gb)
    }

    /// More used on volumes than the container says (figures taken a moment
    /// apart): the remainder clamps to 0 rather than going negative.
    @Test func remainderNeverGoesNegative() throws {
        let mounts: [ContainerBreakdown.Mount] = [
            .init(mountPoint: "/System/Volumes/Data", device: "/dev/disk3s5", fsType: "apfs", usedBytes: 101 * gb),
        ]
        let breakdown = try #require(ContainerBreakdown.build(
            rootDevice: "/dev/disk3s5", rootFsType: "apfs", containerTotal: 200 * gb, containerFree: 100 * gb,
            mounts: mounts, swapInUse: nil))
        #expect(breakdown.unmountedAndMetadataBytes == 0)
    }

    /// Live, on whatever Mac runs the tests: the boot container splits, the
    /// Data volume is found and nothing exceeds the container.
    @Test func readsThisMacsBootContainer() throws {
        let breakdown = try #require(ContainerBreakdown.read(forPath: NSHomeDirectory()))
        let data = try #require(breakdown.dataUsedBytes)
        #expect(data > 0)
        #expect(data <= breakdown.containerUsedBytes)
        #expect(breakdown.volumes.contains { $0.role == .system })
        #expect(breakdown.mountedUsedBytes <= breakdown.containerUsedBytes + 1_000_000_000)
    }
}
