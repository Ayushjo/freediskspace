import CoreGraphics
import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Explore (redesign board 03): what the map, rings and table stripe as
/// "can be freed", and the table's words.
@Suite("Explore")
struct ExploreTests {
    /// r(100) ─ Library(70) ─ Caches(60) ─ spotify(60)
    ///                       └ prefs(10)
    ///        └ Pictures(30)
    private struct Fixture {
        var tree = FileTree()
        var totals: [Int64] = []
        var library: Int32 = 0, caches: Int32 = 0, spotify: Int32 = 0, prefs: Int32 = 0, pictures: Int32 = 0
        init() {
            let root = tree.addNode(name: "r", parent: -1, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 1)
            library = tree.addNode(name: "Library", parent: root, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 1)
            caches = tree.addNode(name: "Caches", parent: library, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 1)
            spotify = tree.addNode(name: "spotify", parent: caches, isDirectory: false, logicalSize: 60, allocatedSize: 60, modifiedDaysSinceEpoch: 1)
            prefs = tree.addNode(name: "prefs", parent: library, isDirectory: false, logicalSize: 10, allocatedSize: 10, modifiedDaysSinceEpoch: 1)
            pictures = tree.addNode(name: "Pictures", parent: root, isDirectory: false, logicalSize: 30, allocatedSize: 30, modifiedDaysSinceEpoch: 1)
            totals = [100, 70, 60, 60, 10, 30]
        }
    }

    @Test func freeableCountsTargetsAndEverythingInsideThem() {
        let f = Fixture()
        let index = FreeableIndex(targets: [f.caches: 60])
        #expect(index.contains(f.spotify, tree: f.tree), "inside a target")
        #expect(!index.contains(f.library, tree: f.tree), "a parent is not itself a target")
        #expect(index.bytes(under: 0, tree: f.tree, totals: f.totals) == 60)
        #expect(index.bytes(under: f.library, tree: f.tree, totals: f.totals) == 60)
        #expect(index.bytes(under: f.pictures, tree: f.tree, totals: f.totals) == 0)
    }

    @Test func mostlyFreeableNeedsSixtyPercent() {
        let f = Fixture()
        let index = FreeableIndex(targets: [f.caches: 60])
        #expect(index.isMostly(f.caches, tree: f.tree, totals: f.totals))
        #expect(index.isMostly(f.library, tree: f.tree, totals: f.totals), "60 of 70 is mostly freeable")
        #expect(index.isMostly(0, tree: f.tree, totals: f.totals), "60 of 100 is exactly at the line and counts")
        #expect(!index.isMostly(f.pictures, tree: f.tree, totals: f.totals))
        #expect(!FreeableIndex.empty.isMostly(f.caches, tree: f.tree, totals: f.totals))
        #expect(!index.isMostly(99, tree: f.tree, totals: f.totals), "an id outside the tree is never striped")
    }

    @Test func nestedLayoutShowsChildrenInsideBigFolders() {
        let f = Fixture()
        let rects = [TreemapRect(id: f.library, rect: CGRect(x: 0, y: 0, width: 400, height: 300)),
                     TreemapRect(id: f.pictures, rect: CGRect(x: 400, y: 0, width: 100, height: 300))]
        let (kids, more) = ExploreTreemapView.nestedLayout(rects, tree: f.tree, totals: f.totals)
        #expect(Set(kids[f.library]?.map(\.id) ?? []) == [f.caches, f.prefs])
        #expect(kids[f.pictures] == nil, "files have no inside")
        #expect(more.isEmpty, "two children are always both shown")
        for r in kids[f.library] ?? [] {
            #expect(CGRect(x: 0, y: 0, width: 400, height: 300).contains(r.rect), "children stay inside their folder")
            #expect(r.rect.minY >= ExploreTreemapView.headerHeight, "below the folder's name")
        }
    }

    @Test func tableWordsForWhatItIs() {
        let other = StorageClass(id: "other", title: "Other", colorHex: "#000", blurb: "")
        let media = StorageClass(id: "media", title: "Photos, videos & music", colorHex: "#000", blurb: "")
        #expect(VisualizeView.whatItIs(rebuilds: true, storageClass: other) == "Rebuilds by itself")
        #expect(VisualizeView.whatItIs(rebuilds: false, storageClass: other) == "—", "Other says nothing, so the table says nothing")
        #expect(VisualizeView.whatItIs(rebuilds: false, storageClass: media) == "Photos, videos & music")
    }

    @Test func lastUsedWords() {
        #expect(VisualizeView.lastUsed(0, today: 100) == "—")
        #expect(VisualizeView.lastUsed(100, today: 100) == "Today")
        #expect(VisualizeView.lastUsed(99, today: 100) == "Yesterday")
        #expect(VisualizeView.lastUsed(97, today: 100) == "3 days ago")
        #expect(VisualizeView.lastUsed(86, today: 100) == "2 weeks ago")
        #expect(VisualizeView.lastUsed(10, today: 100) == "3 months ago")
        #expect(VisualizeView.lastUsed(1, today: 1_000) == "2 years ago")
    }
}
