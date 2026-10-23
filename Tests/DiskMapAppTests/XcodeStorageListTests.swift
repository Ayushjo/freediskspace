import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

@MainActor
@Suite("Developer › Xcode actions")
struct XcodeStorageListTests {
    let entries = Dictionary(uniqueKeysWithValues: SystemStorageCatalog.loadEntries(resource: "developer-storage").map { ($0.id, $0) })

    @Test func offersOlderVersionsButNeverTheKeptOne() throws {
        let e = try #require(entries["device-support"])
        let item = SystemStorageCatalog.Item(entry: e, found: [("/d/17.10", 4), ("/d/17.4", 3), ("/d/16.7", 2)],
                                             isPartial: false, keep: ["/d/17.10"])
        let requests = XcodeStorageList.stageRequests(item)
        #expect(requests.map(\.url.path) == ["/d/17.4", "/d/16.7"])
        #expect(XcodeStorageList.stageTitle(item, count: requests.count).hasPrefix("Add 2 older versions to Cleanup"))
    }

    @Test func guideEntriesOfferNothingToStage() throws {
        for id in ["simulator-devices", "swiftui-previews", "simulator-runtimes", "xcode-archives"] {
            let e = try #require(entries[id])
            let item = SystemStorageCatalog.Item(entry: e, found: [(e.paths[0], 1_000)], isPartial: false)
            #expect(XcodeStorageList.stageRequests(item).isEmpty, "\(id)")
        }
    }

    @Test func allVersionsKeptMeansNothingOffered() throws {
        let e = try #require(entries["device-support"])
        let item = SystemStorageCatalog.Item(entry: e, found: [("/d/Something", 4)], isPartial: false, keep: ["/d/Something"])
        #expect(XcodeStorageList.stageRequests(item).isEmpty)
    }
}
