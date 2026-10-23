import Foundation
import Testing
@testable import DiskMapCore

@Suite("Xcode & simulator storage")
struct DeveloperStorageCatalogTests {
    let entries = SystemStorageCatalog.loadEntries(resource: "developer-storage")

    @Test func bundledFileLoadsCompletely() throws {
        let raw = try #require(DiskMapResources.url(forResource: "developer-storage", withExtension: "json"))
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: raw)) as? [String: Any]
        #expect(entries.count == (json?["entries"] as? [Any])?.count)
        #expect(Set(entries.map(\.id)).count == entries.count)
        // Only things Xcode rebuilds by itself may reach Cleanup; devices,
        // previews, runtimes and archives are steps or commands only.
        #expect(Set(entries.filter { $0.action == .trash }.map(\.id)) == ["device-support", "simulator-caches", "swiftpm-cache"])
        for entry in entries where entry.action == .guide {
            #expect(entry.command != nil || entry.steps.contains { $0.contains("Xcode") }, "\(entry.id)")
        }
    }

    @Test func readsVersionsFromDeviceSupportNames() {
        #expect(SystemStorageCatalog.version(of: "17.4 (21E219)") == [17, 4])
        #expect(SystemStorageCatalog.version(of: "iPhone15,2 17.4.1 (21E236)") == [17, 4, 1])
        #expect(SystemStorageCatalog.version(of: "10.2 (21S364) arm64e") == [10, 2])
        #expect(SystemStorageCatalog.version(of: "Unknown") == nil)
        #expect(SystemStorageCatalog.version(of: "") == nil)
        #expect(SystemStorageCatalog.version(of: "17..4") == nil)
        #expect(SystemStorageCatalog.version(of: "１７.4") == nil)  // full-width digits
    }

    /// A fake home with Xcode's real layout: every DeviceSupport version is a
    /// row, the newest per platform is kept, a folder without versions keeps
    /// everything, hidden files are ignored, and missing parts are left out.
    @Test func measuresAFakeXcodeHome() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("xh-\(UUID().uuidString)").path
        defer { try? FileManager.default.trashItem(at: URL(fileURLWithPath: home), resultingItemURL: nil) }
        let fm = FileManager.default
        func folder(_ path: String, bytes: Int = 4096) throws {
            try fm.createDirectory(atPath: home + "/" + path, withIntermediateDirectories: true)
            try Data(repeating: 1, count: bytes).write(to: URL(fileURLWithPath: home + "/" + path + "/f.bin"))
        }
        let ios = "Library/Developer/Xcode/iOS DeviceSupport"
        try folder(ios + "/16.7 (20H19)", bytes: 100_000)
        try folder(ios + "/iPhone15,2 17.4.1 (21E236)", bytes: 200_000)
        try folder(ios + "/17.10 (21X1)", bytes: 50_000)          // 17.10 > 17.4.1
        try "x".write(toFile: home + "/" + ios + "/.DS_Store", atomically: true, encoding: .utf8)
        let watch = "Library/Developer/Xcode/watchOS DeviceSupport"
        try folder(watch + "/Watch6,1 10.2 (21S364)", bytes: 30_000)
        try folder("Library/Developer/Xcode/tvOS DeviceSupport/Something", bytes: 10_000)
        try folder("Library/Developer/CoreSimulator/Caches/dyld", bytes: 70_000)
        try folder("Library/Developer/CoreSimulator/Devices/ABC", bytes: 40_000)
        try folder("Library/Caches/org.swift.swiftpm/repositories", bytes: 20_000)

        let items = SystemStorageCatalog.measure(entries: entries, home: home)
        let byID = Dictionary(uniqueKeysWithValues: items.map { ($0.id, $0) })
        let support = try #require(byID["device-support"])
        let names = support.found.map { ($0.path as NSString).lastPathComponent }
        #expect(Set(names) == ["16.7 (20H19)", "iPhone15,2 17.4.1 (21E236)", "17.10 (21X1)", "Watch6,1 10.2 (21S364)", "Something"])
        let kept = Set(support.keep.map { ($0 as NSString).lastPathComponent })
        #expect(kept == ["17.10 (21X1)", "Watch6,1 10.2 (21S364)", "Something"])
        #expect(support.suggestedBytes > 0)
        #expect(support.suggestedBytes < support.bytes)
        #expect(byID["simulator-caches"] != nil)
        #expect(byID["simulator-devices"]?.entry.command == "xcrun simctl delete unavailable")
        #expect(byID["swiftpm-cache"] != nil)
        #expect(byID["swiftui-previews"] == nil)      // not on this fake Mac
        #expect(byID["xcode-archives"] == nil)
    }

    /// Opt-in (DISKMAP_LIVE_TESTS=1): on this Mac, everything the Xcode tab
    /// would offer for Cleanup passes the queue's checks, and a kept version
    /// isn't among it. Staging only; nothing moves.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["DISKMAP_LIVE_TESTS"] == "1"))
    func liveOfferedItemsPassStaging() async {
        let queue = CleanupQueue()
        for item in SystemStorageCatalog.measure(entries: entries) where item.entry.action == .trash {
            for found in item.found where !item.keep.contains(found.path) {
                #expect(await queue.stage(URL(fileURLWithPath: found.path), size: found.bytes, reason: "test"), "\(found.path)")
            }
        }
        for staged in await queue.allItems() { await queue.unstage(id: staged.id) }
        #expect(await queue.allItems().isEmpty)
    }
}
