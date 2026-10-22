import Foundation
import Testing
@testable import DiskMapCore

@Suite("Time Machine snapshot removal")
struct TimeMachineSnapshotRemovalTests {
    final class Calls: @unchecked Sendable {
        var runs: [[String]] = []
        var names: [String]
        init(_ names: [String]) { self.names = names }
    }

    @Test func onlyExactTimeMachineNamesGiveADate() {
        #expect(TimeMachineSnapshotRemoval.date(forSnapshotName: "com.apple.TimeMachine.2026-10-22-094830.local") == "2026-10-22-094830")
        let refused = [
            "/", "", "garbage", "2026-10-22-094830",
            "com.apple.os.update-ABC123",
            "com.bombich.ccc.1234-5678",
            "com.apple.TimeMachine.2026-10-22-094830",          // no .local
            "com.apple.TimeMachine.2026-10-22-094830.local.x",
            "com.apple.TimeMachine.2026-10-22-09483.local",     // short
            "com.apple.TimeMachine.2026-10-22-0948301.local",   // long
            "com.apple.TimeMachine.2026/10/22-094830.local",
            "com.apple.TimeMachine.2026-10-22 094830.local",
            "com.apple.TimeMachine.2026-10-22-0948a0.local",
            "com.apple.TimeMachine.; rm -rf ~;-094830.local",
            "com.apple.TimeMachine.//////////////////.local",
            "com.apple.TimeMachine.２０２６-10-22-094830.local", // full-width digits
            "xcom.apple.TimeMachine.2026-10-22-094830.local",
        ]
        for name in refused {
            #expect(TimeMachineSnapshotRemoval.date(forSnapshotName: name) == nil, "\(name)")
        }
    }

    /// The only command ever run is tmutil deletelocalsnapshots <date>, with
    /// the date as its own argument — never a disk, never "/".
    @Test func deletesListedSnapshotsWithTheirDateOnly() {
        let a = "com.apple.TimeMachine.2026-10-20-101500.local", b = "com.apple.TimeMachine.2026-10-21-101500.local"
        let calls = Calls([a, b])
        let results = TimeMachineSnapshotRemoval.delete(
            names: [a, b], volume: "/", list: { calls.names },
            run: { tool, args in
                calls.runs.append([tool] + args)
                calls.names.removeAll { TimeMachineSnapshotRemoval.date(forSnapshotName: $0) == args.last }
                return (0, "Deleted")
            })
        #expect(results.map(\.outcome) == [.deleted, .deleted])
        #expect(calls.runs == [["/usr/bin/tmutil", "deletelocalsnapshots", "2026-10-20-101500"],
                               ["/usr/bin/tmutil", "deletelocalsnapshots", "2026-10-21-101500"]])
    }

    @Test func refusesOtherKindsWithoutRunningAnything() {
        let calls = Calls(["com.apple.os.update-1", "com.bombich.ccc.2", "/"])
        let results = TimeMachineSnapshotRemoval.delete(
            names: ["com.apple.os.update-1", "com.bombich.ccc.2", "/", "garbage"], volume: "/", list: { calls.names },
            run: { _, args in calls.runs.append(args); return (0, "") })
        #expect(results.allSatisfy { $0.outcome == .refused })
        #expect(calls.runs.isEmpty)
    }

    /// macOS thinned it between the list on screen and the click.
    @Test func aSnapshotAlreadyGoneIsNotDeletedAgain() {
        let calls = Calls([])
        let results = TimeMachineSnapshotRemoval.delete(
            names: ["com.apple.TimeMachine.2026-10-20-101500.local"], volume: "/", list: { calls.names },
            run: { _, args in calls.runs.append(args); return (0, "") })
        #expect(results.map(\.outcome) == [.alreadyGone])
        #expect(calls.runs.isEmpty)
    }

    /// tmutil says success but the snapshot is still listed: that's a
    /// failure, not "deleted". And a real error keeps tmutil's own words.
    @Test func trustsTheListNotTheExitCode() {
        let name = "com.apple.TimeMachine.2026-10-20-101500.local"
        let stuck = TimeMachineSnapshotRemoval.delete(names: [name], volume: "/", list: { [name] },
                                                      run: { _, _ in (0, "Deleted local snapshot") })
        #expect(stuck.map(\.outcome) == [.failed("Deleted local snapshot")])
        let error = TimeMachineSnapshotRemoval.delete(names: [name], volume: "/", list: { [name] },
                                                      run: { _, _ in (70, "Failed to delete local snapshot '2026-10-20-101500'\nPOSIXError") })
        #expect(error.map(\.outcome) == [.failed("Failed to delete local snapshot '2026-10-20-101500'")])
    }

    /// Rule 1: this file is the only place that deletes snapshots.
    @Test func noOtherSourceDeletesSnapshots() throws {
        let sources = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("Sources")
        let enumerator = try #require(FileManager.default.enumerator(at: sources, includingPropertiesForKeys: nil))
        var offenders: [String] = [], checked = 0
        for case let url as URL in enumerator where url.pathExtension == "swift" {
            checked += 1
            if url.lastPathComponent == "TimeMachineSnapshotRemoval.swift" { continue }
            let text = try String(contentsOf: url, encoding: .utf8)
            for word in ["deletelocalsnapshots", "thinlocalsnapshots", "fs_snapshot_delete", "deleteSnapshot"] where text.contains(word) {
                offenders.append("\(url.lastPathComponent): \(word)")
            }
        }
        #expect(checked > 100)
        #expect(offenders.isEmpty, "\(offenders)")
    }

    /// Opt-in (DISKMAP_LIVE_TESTS=1): creates a real local snapshot with
    /// `tmutil localsnapshot`, deletes it through this code, and checks
    /// tmutil no longer lists it. Leaves any other snapshot alone.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["DISKMAP_LIVE_TESTS"] == "1"))
    func liveCreateThenDelete() throws {
        let before = Set(TimeMachineSnapshotRemoval.liveNames(volume: "/"))
        let made = TimeMachineSnapshotRemoval.runTool("/usr/bin/tmutil", ["localsnapshot"])
        try #require(made.status == 0, "\(made.output)")
        let created = Set(TimeMachineSnapshotRemoval.liveNames(volume: "/")).subtracting(before)
        let name = try #require(created.first)
        let results = TimeMachineSnapshotRemoval.delete(names: [name], volume: "/")
        #expect(results.map(\.outcome) == [.deleted])
        #expect(!TimeMachineSnapshotRemoval.liveNames(volume: "/").contains(name))
        #expect(Set(TimeMachineSnapshotRemoval.liveNames(volume: "/")) == before)
    }
}
