import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Applications (redesign board 09): what Uninstall ticks and frees, and the words.
@Suite("Applications page")
struct ApplicationsPageTests {
    @Test func onlyWhatRebuildsIsTickedForYou() {
        #expect(AppsView.tickedByDefault(.caches))
        #expect(AppsView.tickedByDefault(.preferences))
        #expect(AppsView.tickedByDefault(.derivedData))
        for kind in [ApplicationRelatedKind.applicationSupport, .containers, .simulators, .archives, .other] {
            #expect(!AppsView.tickedByDefault(kind), "\(kind) can hold your own files")
        }
    }

    @Test func uninstallFreesTheAppAndTheTickedLeftovers() {
        let related = [ApplicationRelatedItem(kind: .caches, path: "/c", displayName: "c", bytes: 100),
                       ApplicationRelatedItem(kind: .containers, path: "/k", displayName: "k", bytes: 5_000)]
        let app = ApplicationEntry(name: "A", publisher: nil, version: nil, bundleID: "a", bundlePath: "/Applications/A.app",
                                   bundleBytes: 1_000, relatedBytes: 5_100, source: .other, status: .keep, lastUsed: nil,
                                   installed: nil, related: related, blurb: "", whyLarge: "", removalGuidance: "", sizePending: false)
        #expect(AppsView.uninstallBytes(app) { AppsView.tickedByDefault($0.kind) } == 1_100)
        #expect(AppsView.uninstallBytes(app) { _ in true } == 6_100)
        #expect(AppsView.uninstallBytes(app) { _ in false } == 1_000, "the app itself always counts")
    }

    @Test func lastOpenedWords() {
        let now = Date(timeIntervalSince1970: 1_792_000_000)
        let day: TimeInterval = 86_400
        #expect(AppsView.lastOpened(nil, now: now) == "—", "no Spotlight date is unknown, not never")
        #expect(AppsView.lastOpened(now, now: now) == "Today")
        #expect(AppsView.lastOpened(now.addingTimeInterval(-day), now: now) == "Yesterday")
        #expect(AppsView.lastOpened(now.addingTimeInterval(-10 * day), now: now) == "Last week")
        #expect(AppsView.lastOpened(now.addingTimeInterval(-125 * day), now: now) == "4 months ago")
        #expect(AppsView.lastOpened(now.addingTimeInterval(-400 * day), now: now) == "1 year ago")
    }

    @Test func lastOpenedSortsTheLongestUnusedFirst() {
        #expect(AppsView.SortKey.lastOpened.catalogSort == .lastUsedAsc)
        #expect(AppsView.SortKey.size.catalogSort == .sizeDesc)
    }
}
