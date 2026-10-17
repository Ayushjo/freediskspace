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
