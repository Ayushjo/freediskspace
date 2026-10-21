import Foundation
import Testing
@testable import DiskMapCore

@Suite("Mount points of other volumes")
struct ForeignMountsTests {
    let mounts: [ForeignMounts.Mount] = [
        .init(path: "/", device: 10),
        .init(path: "/System/Volumes/Data", device: 10),
        .init(path: "/System/Volumes/VM", device: 11),
        .init(path: "/System/Volumes/Preboot", device: 12),
        .init(path: "/Volumes/Untitled", device: 20),
        .init(path: "/Users/me/mnt/share", device: 30),
        .init(path: "/Users/meow", device: 31),
    ]

    @Test func wholeDiskStopsAtEveryOtherVolume() {
        let paths = ForeignMounts.paths(under: "/", rootDevice: 10, mounts: mounts)
        #expect(paths == ["/System/Volumes/VM", "/System/Volumes/Preboot", "/Volumes/Untitled",
                          "/Users/me/mnt/share", "/Users/meow"])
    }

    /// Same-device mounts (the Data volume behind firmlinks) are walked.
    @Test func sameDeviceIsNotForeign() {
        #expect(!ForeignMounts.paths(under: "/", rootDevice: 10, mounts: mounts).contains("/System/Volumes/Data"))
    }

    /// Only mounts strictly inside the root count, and a sibling sharing a
    /// name prefix ("/Users/meow" vs "/Users/me") is not inside.
    @Test func onlyMountsInsideTheRoot() {
        #expect(ForeignMounts.paths(under: "/Users/me", rootDevice: 10, mounts: mounts) == ["/Users/me/mnt/share"])
        #expect(ForeignMounts.paths(under: "/Users/me/", rootDevice: 10, mounts: mounts) == ["/Users/me/mnt/share"])
        #expect(ForeignMounts.paths(under: "/Volumes/Untitled", rootDevice: 20, mounts: mounts).isEmpty)
    }

    /// Live: the boot disk's own volumes behave as measured — "/" and the
    /// Data volume are the same device, swap and Preboot are not.
    @Test func liveBootDiskVolumes() throws {
        var root = stat()
        try #require(lstat("/", &root) == 0)
        let foreign = ForeignMounts.paths(under: "/", rootDevice: Int32(root.st_dev), mounts: ForeignMounts.current())
        #expect(!foreign.contains("/System/Volumes/Data"))
        #expect(foreign.contains("/System/Volumes/VM"))
        #expect(foreign.contains("/System/Volumes/Preboot"))
    }

    /// The walk records a foreign mount point as a folder but never enters it.
    @Test func walkStopsAtAForeignMountPoint() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("fm-\(UUID().uuidString)")
        let mount = root.appendingPathComponent("drive")
        try FileManager.default.createDirectory(at: mount.appendingPathComponent("inner"), withIntermediateDirectories: true)
        try Data(repeating: 1, count: 300_000).write(to: mount.appendingPathComponent("inner/big.bin"))
        try Data(repeating: 2, count: 50_000).write(to: root.appendingPathComponent("mine.bin"))
        defer { try? FileManager.default.trashItem(at: root, resultingItemURL: nil) }
        let realRoot = URL(fileURLWithPath: FSEventHistory.realPath(root.path))
        let stopped = BulkScan.walk(root: realRoot, progress: nil,
                                    foreignMounts: [realRoot.appendingPathComponent("drive").path])
        let walked = BulkScan.walk(root: realRoot, progress: nil, foreignMounts: [])
        let names = { (tree: FileTree) in (0..<tree.count).map { tree.name(of: Int32($0)) } }
        #expect(names(stopped.tree).contains("drive"))
        #expect(!names(stopped.tree).contains("big.bin"))
        #expect(stopped.crossMountSkipCount == 1)
        #expect(names(walked.tree).contains("big.bin"))
    }

    /// A quick rescan copying an old tree drops what was walked inside a
    /// mount point (a cache from before the fix) and doesn't re-enter it.
    @Test func rebuildDropsAnOldTreesForeignMountContents() throws {
        var old = FileTree()
        let r = old.addNode(name: "root", parent: -1, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        let d = old.addNode(name: "drive", parent: r, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        _ = old.addNode(name: "big.bin", parent: d, isDirectory: false, logicalSize: 300, allocatedSize: 300, modifiedDaysSinceEpoch: 0)
        _ = old.addNode(name: "mine.bin", parent: r, isDirectory: false, logicalSize: 50, allocatedSize: 50, modifiedDaysSinceEpoch: 0)
        var rebuild = IncrementalScan.Rebuild(old: old, rootPath: "/tmp/nowhere-root", rootDevID: 1,
                                              foreignMounts: ["/tmp/nowhere-root/drive"])
        rebuild.run(rootModifiedDay: 0)
        let names = (0..<rebuild.new.count).map { rebuild.new.name(of: Int32($0)) }
        #expect(names.contains("drive"))
        #expect(names.contains("mine.bin"))
        #expect(!names.contains("big.bin"))
    }
}
