import Foundation
import Testing
@testable import DiskMapApp

@MainActor
@Suite("Time Machine snapshot wording")
struct SnapshotDeletionTextTests {
    @Test func resultsSayExactlyWhatHappened() {
        #expect(SpaceAccountSheet.resultText(deleted: 1, failed: []) == "Deleted 1 snapshot.")
        #expect(SpaceAccountSheet.resultText(deleted: 3, failed: []) == "Deleted 3 snapshots.")
        #expect(SpaceAccountSheet.resultText(deleted: 0, alreadyGone: 2, failed: []) == "2 had already been removed by macOS.")
        #expect(SpaceAccountSheet.resultText(deleted: 1, alreadyGone: 1, failed: ["boom"])
                == "Deleted 1 snapshot. 1 had already been removed by macOS. 1 couldn't be deleted: boom")
        #expect(SpaceAccountSheet.resultText(deleted: 0, failed: ["a", "b"]) == "2 couldn't be deleted: a")
        #expect(SpaceAccountSheet.resultText(deleted: 0, failed: []) == "Nothing was deleted.")
    }

    @Test func summaryCountsAndNamesTheNewest() {
        let one = SpaceAccountSheet.snapshotSummary(["com.apple.TimeMachine.2026-10-22-094830.local"])
        #expect(one.hasPrefix("1 local snapshot:"))
        #expect(one.contains("Newest:"))
        let two = SpaceAccountSheet.snapshotSummary(["com.apple.TimeMachine.2026-10-20-101500.local",
                                                     "com.apple.TimeMachine.2026-10-22-094830.local"])
        #expect(two.hasPrefix("2 local snapshots:"))
        #expect(two.contains("can't be measured"))
    }
}
