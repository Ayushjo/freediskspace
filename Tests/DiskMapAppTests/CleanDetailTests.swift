import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// The Clean up detail pages: rows say the same as Clean up, protected things never get Add.
@Suite("Clean up detail pages")
struct CleanDetailTests {
    @Test func cacheRowsMatchCleanUp() {
        let t = ReviewableTarget(id: "spotify", category: .caches, displayName: "Spotify", detail: "d", bytes: 6_000,
                                 primaryPath: "/u/Library/Caches/spotify", paths: ["/u/Library/Caches/spotify"], nodeIDs: [],
                                 safety: SafetyAssessment(level: .safe, reason: "r", title: "t"),
                                 consequence: "Some songs may need to download again.", symbolName: "folder", bundleHint: nil)
        let rows = CachesReviewView.rows([t], totals: [], rootPath: "/u")
        #expect(rows.count == 1)
        #expect(rows[0].place == "Library/Caches", "relative to the scan, like Clean up")
        #expect(rows[0].kind == "Rebuilds")
        #expect(rows[0].accent)
        #expect(rows[0].requests.map(\.url.path) == ["/u/Library/Caches/spotify"])
    }

    @Test func protectedDownloadsCannotBeAdded() {
        func candidate(_ level: SafetyLevel) -> OldDownloadsCandidate {
            OldDownloadsCandidate(nodeID: 3, name: "a.dmg", absolutePath: "/u/Downloads/a.dmg", displayPath: "~/Downloads/a.dmg",
                                  bytes: 10, modifiedDay: 0, ageDays: 100, kind: .diskImage, status: .likelyDisposable,
                                  safety: SafetyAssessment(level: level, reason: "r", title: "t"), whyHere: "why", recommendation: "", score: 1)
        }
        #expect(OldDownloadsView.row(candidate(.protected), rootPath: "/u").requests.isEmpty)
        let ok = OldDownloadsView.row(candidate(.review), rootPath: "/u")
        #expect(ok.requests.count == 1)
        #expect(ok.place == "Downloads")
        #expect(ok.kind == "Likely done with")
        #expect(ok.changed == "—", "no date is shown as unknown")
    }
}

@Suite("Folders sort")
struct FoldersSortTests {
    @Test func everyOrderTheFileBrowserHad() {
        var tree = FileTree()
        let root = tree.addNode(name: "r", parent: -1, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 1)
        let b = tree.addNode(name: "b.zip", parent: root, isDirectory: false, logicalSize: 5, allocatedSize: 5, modifiedDaysSinceEpoch: 30)
        let a = tree.addNode(name: "A", parent: root, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 10)
        let c = tree.addNode(name: "c.mov", parent: root, isDirectory: false, logicalSize: 9, allocatedSize: 9, modifiedDaysSinceEpoch: 20)
        let rows: [(id: Int32, size: Int64)] = [(b, 5), (a, 2), (c, 9)]
        let ids = { (m: BiggestFoldersView.SortMode) in BiggestFoldersView.sorted(rows, by: m, tree: tree).map(\.id) }
        #expect(ids(.size) == [c, b, a])
        #expect(ids(.name) == [a, b, c])
        #expect(ids(.changed) == [b, c, a], "newest first")
        #expect(ids(.kind).first == a, "folders first")
    }
}
