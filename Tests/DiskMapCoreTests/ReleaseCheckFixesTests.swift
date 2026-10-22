import Foundation
import Testing
@testable import DiskMapCore

/// Fixes from the pre-release walk through every screen.
@Suite("Release check fixes")
struct ReleaseCheckFixesTests {
    @Test func buildOutputKeepsItsOwnConsequenceNotTheGenericReviewLine() {
        var tree = FileTree()
        _ = tree.addNode(name: "Users", parent: -1, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        let me = tree.addNode(name: "me", parent: 0, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        let app = tree.addNode(name: "app", parent: me, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        _ = tree.addNode(name: "package.json", parent: app, isDirectory: false, logicalSize: 10, allocatedSize: 10, modifiedDaysSinceEpoch: 10)
        let nm = tree.addNode(name: "node_modules", parent: app, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        _ = tree.addNode(name: "x.js", parent: nm, isDirectory: false, logicalSize: 500_000_000, allocatedSize: 500_000_000, modifiedDaysSinceEpoch: 10)
        let root = URL(fileURLWithPath: "/Users", isDirectory: true)
        let both = tree.rollUpBoth()
        let built = ReviewableCatalog.build(tree: tree, root: root, totals: both.allocated,
                                            quickWins: QuickWins.find(in: tree, root: root, patterns: QuickWins.bundledPatterns()))
        let generic = SafetyAssessment(level: .review, reason: "r", title: "t").consequences
        let deps = built.targets.filter { $0.category == .buildArtifacts }
        #expect(!deps.isEmpty)
        #expect(deps.allSatisfy { $0.consequence != generic })
    }

    @Test func specificConsequencesAreTellableFromTheDefault() {
        #expect(!SafetyAssessment(level: .review, reason: "r", title: "t").hasSpecificConsequences)
        #expect(SafetyAssessment(level: .review, reason: "r", title: "t", consequences: "Pull images again.").hasSpecificConsequences)
    }

    @Test func duplicateCopiesOwnedByToolsAreNotPreTicked() {
        #expect(DuplicatePreTick.isToolOwned(path: "/Users/me/site/node_modules/next/swc.node"))
        #expect(DuplicatePreTick.isToolOwned(path: "/Users/me/repo/.git/objects/pack/pack-1.pack"))
        #expect(DuplicatePreTick.isToolOwned(path: "/Applications/Thing.app/Contents/Resources/a.png"))
        #expect(!DuplicatePreTick.isToolOwned(path: "/Users/me/Downloads/holiday.mp4"))
        // The file's own name never counts, only the folders above it.
        #expect(!DuplicatePreTick.isToolOwned(path: "/Users/me/Documents/build"))
    }

    @Test func untouchedSkipsFilesInsideAppBundles() {
        var tree = FileTree()
        _ = tree.addNode(name: "root", parent: -1, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        let bundle = tree.addNode(name: "Old.app", parent: 0, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 1)
        let contents = tree.addNode(name: "Contents", parent: bundle, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 1)
        _ = tree.addNode(name: "inside.bin", parent: contents, isDirectory: false, logicalSize: 9_000, allocatedSize: 9_000, modifiedDaysSinceEpoch: 1)
        let loose = tree.addNode(name: "old.pdf", parent: 0, isDirectory: false, logicalSize: 1_000, allocatedSize: 1_000, modifiedDaysSinceEpoch: 1)
        let totals = tree.rollUpBoth().allocated
        #expect(AgeMap.untouched(in: tree, totals: totals, today: 2_000, limit: 10) == [loose])
    }
}

extension ReleaseCheckFixesTests {
    @Test func swiftBuildFolderIsBuildOutputWithTheRebuildNote() {
        var tree = FileTree()
        _ = tree.addNode(name: "Users", parent: -1, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        let pkg = tree.addNode(name: "pkg", parent: 0, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        _ = tree.addNode(name: "Package.swift", parent: pkg, isDirectory: false, logicalSize: 10, allocatedSize: 10, modifiedDaysSinceEpoch: 10)
        let build = tree.addNode(name: ".build", parent: pkg, isDirectory: true, logicalSize: 0, allocatedSize: 0, modifiedDaysSinceEpoch: 0)
        _ = tree.addNode(name: "o.o", parent: build, isDirectory: false, logicalSize: 900_000_000, allocatedSize: 900_000_000, modifiedDaysSinceEpoch: 10)
        let root = URL(fileURLWithPath: "/Users", isDirectory: true)
        let both = tree.rollUpBoth()
        let built = ReviewableCatalog.build(tree: tree, root: root, totals: both.allocated,
                                            quickWins: QuickWins.find(in: tree, root: root, patterns: QuickWins.bundledPatterns()))
        let swift = built.targets.first { $0.primaryPath.hasSuffix("/.build") }
        #expect(swift?.category == .buildArtifacts)
        #expect(swift?.displayName == "Swift build")
        #expect(swift?.consequence == "The project may need to rebuild. The next build can take longer.")
    }
}
