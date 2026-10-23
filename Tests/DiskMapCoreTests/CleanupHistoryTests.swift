import Foundation
import Testing
@testable import DiskMapCore

/// The cleanup history behind History & Put Back (redesign board 10).
@Suite("Cleanup history")
struct CleanupHistoryTests {
    private func record(_ names: [String], bytes: Int64 = 10, at date: Date = Date()) -> CleanupRecord {
        CleanupRecord(date: date, items: names.map { .init(originalPath: "/u/\($0)", trashedPath: "/t/\($0)", bytes: bytes) })
    }

    @Test func appendNumbersNewestFirstAndSkipsEmptyCleanups() {
        var history = CleanupHistory()
        history.append(record(["a"]))
        history.append(record([]))
        history.append(record(["b", "c"]))
        #expect(history.entries.map(\.number) == [2, 1])
        #expect(history.entries.first?.record.items.count == 2)
    }

    @Test func itIsCappedAndKeepsTheNewest() {
        var history = CleanupHistory()
        for i in 0..<(CleanupHistory.limit + 5) { history.append(record(["f\(i)"])) }
        #expect(history.entries.count == CleanupHistory.limit)
        #expect(history.entries.first?.number == CleanupHistory.limit + 5)
    }

    @Test func statesComeFromPutBackAndTheTrash() {
        var history = CleanupHistory()
        history.append(record(["a", "b", "c"], bytes: 100))
        history.markRestored([history.entries[0].record.items[0]], in: 1)
        history.markRestored([history.entries[0].record.items[0]], in: 1)
        #expect(history.entries[0].restored == ["/t/a"], "marking twice records once")
        let inTrash: Set<String> = ["/t/a", "/t/b"]
        let entry = history.entries[0]
        let states = entry.record.items.map { CleanupHistory.state(of: $0, in: entry) { inTrash.contains($0) } }
        #expect(states == [.putBack, .inTrash, .trashEmptied])
        #expect(history.freedBytes == 200, "put back items no longer count as freed")
        #expect(history.bytesInTrash { inTrash.contains($0) } == 100)
    }

    @Test func roundTripsThroughDisk() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("history-\(UUID().uuidString)")
        let url = dir.appendingPathComponent("cleanup-history.json")
        var history = CleanupHistory()
        history.append(record(["x"], at: Date(timeIntervalSince1970: 1_790_000_000)))
        try history.save(to: url)
        #expect(CleanupHistory.load(from: url) == history)
        #expect(CleanupHistory.load(from: dir.appendingPathComponent("missing.json")) == nil)
        try? FileManager.default.trashItem(at: dir, resultingItemURL: nil)
    }

    @Test func seededFromTheLastCleanup() {
        #expect(CleanupHistory.seeded(from: nil).entries.isEmpty)
        #expect(CleanupHistory.seeded(from: record(["a"])).entries.map(\.number) == [1])
    }
}
