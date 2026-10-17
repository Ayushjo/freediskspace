import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Developer (redesign board 06): tool tiles, chips and the inspector's words.
@Suite("Developer page")
struct DeveloperPageTests {
    private func item(_ name: String, _ eco: DeveloperEcosystem, _ bytes: Int64, _ r: DeveloperReclaimability = .reclaimable,
                      cost: RebuildCost = .cheap, lockfile: String? = nil) -> DeveloperItem {
        DeveloperItem(id: name + eco.rawValue, nodeID: 1, displayName: name, absolutePath: "/p/" + name, displayPath: "~/p/" + name,
                      bytes: bytes, category: .dependencies, ecosystem: eco,
                      safety: SafetyAssessment(level: .safe, reason: "r", title: "t"), reclaimability: r, whyLarge: "w",
                      projectKey: nil, projectName: nil, modifiedDay: 1, isToolRoot: false, rebuildCost: cost,
                      lockfile: lockfile, projectManifest: nil)
    }

    @Test func tilesAreBiggestFirstWithWhatRebuilds() {
        let tiles = DeveloperStorageView.tiles([item("a", .node, 10), item("b", .node, 30, .keep), item("c", .xcode, 50, .reviewFirst)], limit: 9)
        #expect(tiles.map(\.title) == [DeveloperEcosystem.xcode.title, DeveloperEcosystem.node.title])
        #expect(tiles[1].bytes == 40)
        #expect(tiles[1].rebuildBytes == 10, "only what rebuilds by itself is hatched")
        #expect(tiles[0].rebuildBytes == 0)
    }

    @Test func extraToolsFoldIntoOneTile() {
        let items: [DeveloperItem] = [item("a", .node, 50), item("b", .xcode, 40), item("c", .python, 30), item("d", .rust, 20)]
        let tiles = DeveloperStorageView.tiles(items, limit: 3)
        #expect(tiles.count == 3)
        #expect(tiles.last?.ecosystem == nil)
        #expect(tiles.last?.title == "2 more")
        #expect(tiles.last?.bytes == 50)
        #expect(tiles.reduce(0) { $0 + $1.bytes } == 140, "folding loses nothing")
    }

    @Test func chipSizesAreShort() {
        #expect(DevChip.short(4_200_000_000) == "4.2")
        #expect(DevChip.short(350_000_000) == "350M")
        #expect(DevChip.short(12_000) == "12K")
    }

    @Test func reinstallLineNamesTheLockfiles() {
        let ok = DeveloperStorageView.reinstallLine([item("node_modules", .node, 1, cost: .networked, lockfile: "package-lock.json"),
                                                     item("Pods", .xcode, 1, cost: .networked, lockfile: "Podfile.lock")])
        #expect(ok.ok)
        #expect(ok.detail == "Podfile.lock and package-lock.json are here, so the same versions come back.")
        let risky = DeveloperStorageView.reinstallLine([item("node_modules", .node, 1, cost: .networkedUnpinned)])
        #expect(!risky.ok)
        #expect(risky.detail.hasPrefix("1 folder has no lockfile"))
        #expect(DeveloperStorageView.reinstallLine([item(".build", .xcode, 1)]).ok, "build output always comes back")
    }

    @Test func itemNoteOffersTheToolsOwnWayWhenTrashIsWrong() {
        var sim = item("CoreSimulator", .xcode, 1, .reviewFirst)
        sim.recipe = CleanupRecipe(id: "sim", title: "Simulators", command: "`xcrun simctl delete unavailable`", why: "w", trashIsUnsafe: true, names: [], pathSuffixes: [], pathContains: [])
        #expect(DeveloperStorageView.itemNote(sim) == "Use xcrun simctl delete unavailable instead.")
        #expect(DeveloperStorageView.itemNote(item("target", .rust, 1)) == "\(DeveloperEcosystem.rust.title) · Offline build")
    }

    @Test func staleNoteDependsOnAge() {
        #expect(DeveloperStorageView.folderNote(stale: true).title.contains("six months"))
        #expect(DeveloperStorageView.folderNote(stale: false).title == "You worked on this recently")
    }
}
