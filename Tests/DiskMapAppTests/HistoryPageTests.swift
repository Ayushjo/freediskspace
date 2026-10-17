import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// History & Put Back (redesign board 10): a real Put Back of one item from a
/// cleanup, the card tags and the dates.
@Suite("History page")
@MainActor
struct HistoryPageTests {
    @Test func puttingBackOneItemMovesItAndRecordsIt() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("putback-\(UUID().uuidString)", isDirectory: true)
        let trash = dir.appendingPathComponent("Trash", isDirectory: true)
        try FileManager.default.createDirectory(at: trash, withIntermediateDirectories: true)
        let trashed = trash.appendingPathComponent("report.pdf")
        try Data("x".utf8).write(to: trashed)
        let original = dir.appendingPathComponent("Documents/report.pdf")
        let other = CleanupRecord.Item(originalPath: dir.appendingPathComponent("gone.zip").path,
                                       trashedPath: trash.appendingPathComponent("gone.zip").path, bytes: 5)
        let item = CleanupRecord.Item(originalPath: original.path, trashedPath: trashed.path, bytes: 1)

        let model = ScanModel()
        model.cleanupHistory.append(CleanupRecord(date: Date(), items: [item, other]))
        await model.putBack([item], fromCleanup: 1)

        #expect(FileManager.default.fileExists(atPath: original.path), "it is back where it was, with the same name")
        #expect(!FileManager.default.fileExists(atPath: trashed.path))
        let entry = try #require(model.cleanupHistory.entries.first)
        #expect(entry.restored == [trashed.path])
        #expect(CleanupHistory.state(of: item, in: entry) { FileManager.default.fileExists(atPath: $0) } == .putBack)
        #expect(CleanupHistory.state(of: other, in: entry) { FileManager.default.fileExists(atPath: $0) } == .trashEmptied)

        // Something new at the old path is never replaced.
        try Data("y".utf8).write(to: trashed)
        var second = CleanupHistory()
        second.append(CleanupRecord(date: Date(), items: [item]))
        model.cleanupHistory = second
        await model.putBack([item], fromCleanup: 1)
        #expect(model.cleanupHistory.entries.first?.restored.isEmpty == true)
        #expect(FileManager.default.fileExists(atPath: trashed.path), "skipped, so it stays in the Trash")
        try? FileManager.default.trashItem(at: dir, resultingItemURL: nil)
    }

    @Test func cardTags() {
        #expect(HistoryView.tag([.inTrash, .inTrash]) == ("All in Trash", true))
        #expect(HistoryView.tag([.inTrash, .trashEmptied]) == ("1 in Trash", true))
        #expect(HistoryView.tag([.putBack, .putBack]) == ("Put back", false))
        #expect(HistoryView.tag([.trashEmptied, .putBack]) == ("Trash emptied", false))
    }

    @Test func whenWords() {
        let calendar = Calendar.current
        let now = calendar.date(from: DateComponents(year: 2026, month: 10, day: 17, hour: 12)) ?? Date()
        let morning = calendar.date(from: DateComponents(year: 2026, month: 10, day: 17, hour: 9, minute: 5)) ?? Date()
        #expect(HistoryView.whenText(morning, now: now) == "Today, 09:05")
        #expect(HistoryView.whenText(morning.addingTimeInterval(-86_400), now: now) == "Yesterday, 09:05")
        #expect(HistoryView.whenText(morning.addingTimeInterval(-3 * 86_400), now: now) == "Wednesday, 09:05")
        #expect(HistoryView.dayText(morning.addingTimeInterval(-15 * 86_400), now: now) == "2 Oct")
        #expect(HistoryView.dayText(morning.addingTimeInterval(-400 * 86_400), now: now).hasSuffix("2025"))
    }
}
