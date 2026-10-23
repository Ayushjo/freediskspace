import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Clean up (redesign board 02): what goes in which group, what is ticked for
/// you, and that protected items never appear.
@Suite("Clean up")
struct CleanUpTests {
    private func target(_ id: String, _ level: SafetyLevel, _ bytes: Int64, paths: [String]? = nil) -> ReviewableTarget {
        let p = paths ?? ["/Users/me/Library/Caches/\(id)"]
        return ReviewableTarget(id: id, category: .caches, displayName: id.capitalized, detail: "detail", bytes: bytes,
                                primaryPath: p[0], paths: p, nodeIDs: [], safety: SafetyAssessment(level: level, reason: "r", title: "t"),
                                consequence: "\(id) rebuilds", symbolName: "folder", bundleHint: nil)
    }

    private func download(_ id: Int32, _ name: String, _ bytes: Int64, age: Int32 = 200) -> OldDownloadsCandidate {
        OldDownloadsCandidate(nodeID: id, name: name, absolutePath: "/Users/me/Downloads/misc/\(name)", displayPath: "~/Downloads/misc/\(name)",
                              bytes: bytes, modifiedDay: 0, ageDays: age, kind: .archive, status: .reviewFirst,
                              safety: SafetyAssessment(level: .review, reason: "r", title: "t"), whyHere: "", recommendation: "", score: 1)
    }

    private func media(_ id: Int32, _ name: String, _ bytes: Int64, level: SafetyLevel = .review) -> MediaCandidate {
        MediaCandidate(nodeID: id, name: name, absolutePath: "/Users/me/Movies/\(name)", displayPath: "~/Movies/\(name)", bytes: bytes,
                       modifiedDay: 0, ageDays: 400, kind: .video, location: .movies, status: .reviewFirst,
                       safety: SafetyAssessment(level: level, reason: "r", title: "t"), whyHere: "", recommendation: "",
                       isDirectory: false, score: 1)
    }

    @Test func groupsFollowSafetyAndOnlySafeItemsAreTicked() {
        let items = CleanUpView.buildItems(
            reviewables: [target("spotify", .safe, 6_000), target("simulators", .review, 30_000), target("keychain", .protected, 9_000)],
            downloads: [download(10, "old.zip", 4_000)],
            media: [media(20, "Holiday.mov", 7_000)],
            totals: [], rootPath: "/Users/me")
        #expect(items.map(\.title) == ["Simulators", "Holiday.mov", "Spotify", "old.zip"])
        #expect(items.first { $0.title == "Spotify" }?.group == .rebuilds)
        #expect(items.first { $0.title == "Simulators" }?.group == .reviewFirst)
        #expect(items.first { $0.title == "old.zip" }?.group == .downloads)
        #expect(items.first { $0.title == "Holiday.mov" }?.group == .media)
        #expect(!items.contains { $0.title == "Keychain" }, "protected items never appear")
        let ticks = CleanUpView.defaultTicks(items) { _ in false }
        #expect(ticks == ["r-spotify"], "only things that rebuild by themselves are ticked for you")
    }

    @Test func stagedItemsAreNotTickedAgain() {
        let items = CleanUpView.buildItems(reviewables: [target("spotify", .safe, 6_000), target("chrome", .safe, 5_000)],
                                           downloads: [], media: [], totals: [], rootPath: "/Users/me")
        let ticks = CleanUpView.defaultTicks(items) { $0.lastPathComponent == "spotify" }
        #expect(ticks == ["r-chrome"])
    }

    @Test func aFileThatIsBothAnOldDownloadAndLargeMediaIsListedOnce() {
        let items = CleanUpView.buildItems(reviewables: [], downloads: [download(5, "talk.mov", 9_000)],
                                           media: [media(5, "talk.mov", 9_000), media(6, "other.mov", 8_000, level: .protected)],
                                           totals: [], rootPath: "/Users/me")
        #expect(items.map(\.id) == ["d-5"])
    }

    @Test func everyPathOfATargetIsStagedWithItsOwnSize() {
        let t = target("node", .review, 3_000, paths: ["/Users/me/a/node_modules", "/Users/me/b/node_modules"])
        var withIDs = t
        withIDs.nodeIDs = [1, 2]
        let items = CleanUpView.buildItems(reviewables: [withIDs], downloads: [], media: [], totals: [0, 1_200, 1_800], rootPath: "/Users/me")
        #expect(items.first?.requests.map(\.size) == [1_200, 1_800])
        #expect(items.first?.why.hasPrefix("2 places") == true)
    }

    @Test func placeIsRelativeToTheScan() {
        #expect(CleanUpView.place("/Users/me/Downloads/misc/a.zip", rootPath: "/Users/me") == "Downloads/misc")
        #expect(CleanUpView.place("/Users/me/a.zip", rootPath: "/Users/me") == "me")
        #expect(CleanUpView.place("/Users/meow/a.zip", rootPath: "/Users/me") != "ow", "a sibling folder with the same prefix is not inside the scan")
    }

    @Test func receiptAgeWords() {
        #expect(CleanUpView.age(0) == "today")
        #expect(CleanUpView.age(10) == "10 days old")
        #expect(CleanUpView.age(45) == "6 weeks old")
        #expect(CleanUpView.age(330) == "11 months old")
        #expect(CleanUpView.age(800) == "2 years old")
    }

    @Test func heroFiguresUseThreeSignificantDigits() {
        #expect(ByteFormat.hero(45_530_000_000) == "45.5 GB")
        #expect(ByteFormat.hero(120_000_000_000) == "120 GB")
        #expect(ByteFormat.hero(8_412_000_000) == "8.41 GB")
        #expect(ByteFormat.hero(1_400_000_000_000) == "1.40 TB")
    }

    @Test func relativeScanTime() {
        let now = Date()
        #expect(RelativeTime.string(since: now.addingTimeInterval(-10), now: now) == "just now")
        #expect(RelativeTime.string(since: now.addingTimeInterval(-120), now: now) == "2 minutes ago")
        #expect(RelativeTime.string(since: now.addingTimeInterval(-3 * 3_600), now: now) == "3 hours ago")
        #expect(RelativeTime.string(since: now.addingTimeInterval(-26 * 3_600), now: now) == "yesterday")
    }

    @Test func sidebarFiguresAreCompact() {
        #expect(AppShellView.compactBytes(31_600_000_000) == "31.6G")
        #expect(AppShellView.compactBytes(212_000_000_000) == "212G")
        #expect(AppShellView.compactBytes(420_000_000) == "420M")
    }

    @Test func foldedPagesLightUpTheirParent() {
        #expect(AppDestination.cleanCaches.navParent == .cleanSafe)
        #expect(AppDestination.biggestFiles.navParent == .visualize)
        #expect(AppDestination.find.navParent == .visualize)
        #expect(AppDestination.duplicates.navParent == .duplicates)
        #expect(AppDestination.primaryNav.count == 6)
    }
}

@Suite("Clean up preference")
struct CleanUpPreferenceTests {
    @Test func nothingIsTickedWhenThePersonChoosesThemselves() {
        let t = ReviewableTarget(id: "spotify", category: .caches, displayName: "Spotify", detail: "d", bytes: 10,
                                 primaryPath: "/u/Library/Caches/spotify", paths: ["/u/Library/Caches/spotify"], nodeIDs: [],
                                 safety: SafetyAssessment(level: .safe, reason: "r", title: "t"),
                                 consequence: "c", symbolName: "folder", bundleHint: nil)
        let items = CleanUpView.buildItems(reviewables: [t], downloads: [], media: [], totals: [], rootPath: "/u")
        #expect(CleanUpView.defaultTicks(items, isStaged: { _ in false }, tickRebuilds: true) == ["r-spotify"])
        #expect(CleanUpView.defaultTicks(items, isStaged: { _ in false }, tickRebuilds: false).isEmpty)
    }
}

extension CleanUpTests {
    @Test func oldDownloadsGroupOnlyHoldsFilesOlderThanThirtyDays() {
        func dl(_ id: Int32, _ name: String, age: Int32) -> OldDownloadsCandidate {
            OldDownloadsCandidate(nodeID: id, name: name, absolutePath: "/Users/me/Downloads/\(name)", displayPath: "~/Downloads/\(name)",
                                  bytes: 5_000, modifiedDay: 0, ageDays: age, kind: .video, status: .reviewFirst,
                                  safety: SafetyAssessment(level: .review, reason: "r", title: "t"), whyHere: "", recommendation: "", score: 1)
        }
        let items = CleanUpView.buildItems(reviewables: [], downloads: [dl(1, "new.mp4", age: 10), dl(2, "old.mp4", age: 90)],
                                           media: [], totals: [], rootPath: "/Users/me")
        #expect(items.filter { $0.group == .downloads }.map(\.title) == ["old.mp4"])
    }

    @MainActor @Test func overviewNamesUntickedBuildFilesInsteadOfSayingNothingRebuilds() {
        let model = ScanModel()
        model.cachedReviewables = [target("deps", .review, 5_000_000_000)]
        #expect(model.safeReclaimableBytes == 0)
        #expect(model.nothingTickedLine.contains("5 GB"))
        model.cachedReviewables = []
        #expect(model.nothingTickedLine.hasPrefix("Nothing here rebuilds by itself"))
    }
}
