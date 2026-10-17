import Foundation

/// What the last cleanup moved to the Trash and where it went, kept so it can
/// be put back — after a relaunch too (TASK-080).
public struct CleanupRecord: Codable, Sendable, Equatable {
    public struct Item: Codable, Sendable, Equatable {
        public var originalPath: String
        public var trashedPath: String
        public var bytes: Int64

        public init(originalPath: String, trashedPath: String, bytes: Int64) {
            self.originalPath = originalPath
            self.trashedPath = trashedPath
            self.bytes = bytes
        }
    }

    public var date: Date
    public var items: [Item]

    public init(date: Date, items: [Item]) {
        self.date = date
        self.items = items
    }

    /// The items a commit moved by themselves (an item that went inside its
    /// folder comes back with the folder), with where the Trash put them.
    public init(report: CleanupQueue.CommitReport, date: Date = Date()) {
        self.date = date
        self.items = report.entries.compactMap { entry in
            guard entry.error == nil, !entry.movedWithFolder, let trashed = entry.trashedURL else { return nil }
            return Item(originalPath: entry.item.url.path, trashedPath: trashed.path, bytes: entry.freedBytes)
        }
    }

    public static func defaultURL() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("DiskMap/last-cleanup.json")
    }

    public static func load(from url: URL) -> CleanupRecord? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(CleanupRecord.self, from: data)
    }

    /// Written whole and atomically; an empty record replaces a used one.
    public func save(to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(self).write(to: url, options: .atomic)
    }
}

public struct PutBackReport: Sendable {
    public var restored: [CleanupRecord.Item]
    public var skipped: [(item: CleanupRecord.Item, reason: String)]
}

extension CleanupQueue {
    /// Moves the last cleanup's items from the Trash back where they were.
    /// A move back into the user's own folders — never a removal: an item is
    /// skipped, with the reason, when it is no longer in the Trash or when
    /// something new already sits at its old path (nothing is replaced). A
    /// missing parent folder is recreated. Only `FileManager.moveItem`.
    ///
    /// `move` is a seam for tests; production code uses the default.
    public static func putBack(
        _ record: CleanupRecord,
        move: (URL, URL) throws -> Void = { try FileManager.default.moveItem(at: $0, to: $1) }
    ) -> PutBackReport {
        var report = PutBackReport(restored: [], skipped: [])
        let fileManager = FileManager.default
        func exists(_ path: String) -> Bool {
            var info = stat()
            return lstat(path, &info) == 0   // a dangling symlink still occupies the name
        }
        for item in record.items {
            guard exists(item.trashedPath) else {
                report.skipped.append((item, "Already removed from the Trash"))
                continue
            }
            guard !exists(item.originalPath) else {
                report.skipped.append((item, "Something new is at \(CanonicalPath.displayPath(absolutePath: item.originalPath))"))
                continue
            }
            let original = URL(fileURLWithPath: item.originalPath)
            do {
                try fileManager.createDirectory(at: original.deletingLastPathComponent(), withIntermediateDirectories: true)
                try move(URL(fileURLWithPath: item.trashedPath), original)
                report.restored.append(item)
            } catch {
                report.skipped.append((item, error.localizedDescription))
            }
        }
        return report
    }
}

/// Every cleanup freedisk.space made, newest first, numbered, with what was
/// put back since (redesign board 10). Only a record of moves to the Trash:
/// nothing here deletes, and Put Back still goes through `CleanupQueue.putBack`.
public struct CleanupHistory: Codable, Sendable, Equatable {
    public struct Entry: Codable, Sendable, Equatable, Identifiable {
        public var number: Int
        public var record: CleanupRecord
        /// Trashed paths of the items already put back.
        public var restored: [String]

        public var id: Int { number }

        public init(number: Int, record: CleanupRecord, restored: [String] = []) {
            self.number = number
            self.record = record
            self.restored = restored
        }
    }

    /// Where an item is now.
    public enum ItemState: Sendable, Equatable {
        case inTrash, putBack, trashEmptied
    }

    public var entries: [Entry]

    public init(entries: [Entry] = []) { self.entries = entries }

    /// Kept to a few hundred cleanups; the oldest drop off.
    public static let limit = 300

    public static func defaultURL() -> URL {
        CleanupRecord.defaultURL().deletingLastPathComponent().appendingPathComponent("cleanup-history.json")
    }

    public static func load(from url: URL) -> CleanupHistory? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(CleanupHistory.self, from: data)
    }

    public func save(to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(self).write(to: url, options: .atomic)
    }

    /// Adds a cleanup at the top with the next number. Empty records are not history.
    public mutating func append(_ record: CleanupRecord) {
        guard !record.items.isEmpty else { return }
        let next = (entries.map(\.number).max() ?? 0) + 1
        entries.insert(Entry(number: next, record: record), at: 0)
        if entries.count > Self.limit { entries.removeLast(entries.count - Self.limit) }
    }

    /// Notes items that came back, so they read "Put back" from now on.
    public mutating func markRestored(_ items: [CleanupRecord.Item], in number: Int) {
        guard let index = entries.firstIndex(where: { $0.number == number }) else { return }
        let new = items.map(\.trashedPath).filter { !entries[index].restored.contains($0) }
        entries[index].restored.append(contentsOf: new)
    }

    /// Put back wins; otherwise the Trash copy decides.
    public static func state(of item: CleanupRecord.Item, in entry: Entry, existsInTrash: (String) -> Bool) -> ItemState {
        if entry.restored.contains(item.trashedPath) { return .putBack }
        return existsInTrash(item.trashedPath) ? .inTrash : .trashEmptied
    }

    /// Bytes moved to the Trash and not put back, over every cleanup.
    public var freedBytes: Int64 {
        entries.reduce(0) { total, entry in
            total + entry.record.items.filter { !entry.restored.contains($0.trashedPath) }.reduce(0) { $0 + $1.bytes }
        }
    }

    /// Bytes still in the Trash that Put Back can return.
    public func bytesInTrash(existsInTrash: (String) -> Bool) -> Int64 {
        entries.reduce(0) { total, entry in
            total + entry.record.items.filter { Self.state(of: $0, in: entry, existsInTrash: existsInTrash) == .inTrash }
                .reduce(0) { $0 + $1.bytes }
        }
    }

    /// The first run after updating: the one cleanup the app already knew about.
    public static func seeded(from last: CleanupRecord?) -> CleanupHistory {
        var history = CleanupHistory()
        if let last { history.append(last) }
        return history
    }
}
