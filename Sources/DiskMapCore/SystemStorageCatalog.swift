import Foundation

/// Storage macOS fills by itself — models, voices, pending updates, aerial
/// videos, installers, logs — measured where it lives, so the card works even
/// after scanning only the home folder. Entries are data
/// (`system-storage.json`, AGENTS.md rule 6). Research and sources:
/// docs/STORAGE-RESEARCH-PLAN.md §2. Only `trash` entries may be offered for
/// Cleanup; the rest are explained and linked to macOS's own settings.
public enum SystemStorageCatalog {
    public enum Action: String, Sendable, Codable {
        /// macOS manages it; nothing to do.
        case never
        /// Free it through macOS's own settings.
        case guide
        /// Safe to add to Cleanup (goes to the Trash).
        case trash
    }

    public struct Entry: Sendable, Equatable {
        public var id: String
        public var title: String
        public var paths: [String]
        public var action: Action
        public var why: String
        public var steps: [String]
        /// System Settings extension id, opened with `x-apple.systempreferences:`.
        public var settingsPane: String?
        public var regrows: Bool
        /// A tool command to copy (never run by the app), e.g. simctl.
        public var command: String?
        /// List each child of the matched folders as its own row.
        public var perChild: Bool
        /// With `perChild`: suggest keeping the highest version in each folder.
        public var keepNewest: Bool

        public init(id: String, title: String, paths: [String], action: Action, why: String, steps: [String],
                    settingsPane: String?, regrows: Bool, command: String? = nil, perChild: Bool = false,
                    keepNewest: Bool = false) {
            self.id = id; self.title = title; self.paths = paths; self.action = action
            self.why = why; self.steps = steps; self.settingsPane = settingsPane; self.regrows = regrows
            self.command = command; self.perChild = perChild; self.keepNewest = keepNewest
        }
    }

    public struct Item: Sendable, Equatable, Identifiable {
        public var entry: Entry
        /// The paths that exist on this Mac, with what each holds on disk.
        public var found: [(path: String, bytes: Int64)]
        public var bytes: Int64 { found.reduce(0) { $0 + $1.bytes } }
        /// Some folder inside couldn't be read (Full Disk Access): the size is
        /// a lower bound, never shown as complete.
        public var isPartial: Bool
        /// Paths suggested to keep (the newest version per folder).
        public var keep: Set<String>
        public var id: String { entry.id }
        /// What removing every suggested path would free.
        public var suggestedBytes: Int64 { found.filter { !keep.contains($0.path) }.reduce(0) { $0 + $1.bytes } }

        public init(entry: Entry, found: [(path: String, bytes: Int64)], isPartial: Bool, keep: Set<String> = []) {
            self.entry = entry; self.found = found; self.isPartial = isPartial; self.keep = keep
        }

        public static func == (lhs: Item, rhs: Item) -> Bool {
            lhs.entry == rhs.entry && lhs.isPartial == rhs.isPartial && lhs.keep == rhs.keep
                && lhs.found.map(\.path) == rhs.found.map(\.path) && lhs.found.map(\.bytes) == rhs.found.map(\.bytes)
        }
    }

    private struct File: Decodable {
        struct Raw: Decodable {
            var id: String
            var title: String
            var paths: [String]
            var action: String
            var why: String
            var steps: [String]
            var settingsPane: String?
            var regrows: Bool?
            var command: String?
            var perChild: Bool?
            var keepNewest: Bool?
        }
        var entries: [Raw]
    }

    /// A malformed file yields no entries; an entry with an unknown action is
    /// dropped rather than guessed. The suite checks the bundled file loads.
    public static func loadEntries(from data: Data? = nil, resource: String = "system-storage") -> [Entry] {
        let bytes = data ?? DiskMapResources.url(forResource: resource, withExtension: "json")
            .flatMap { try? Data(contentsOf: $0) }
        guard let bytes, let file = try? JSONDecoder().decode(File.self, from: bytes) else { return [] }
        return file.entries.compactMap { raw in
            guard let action = Action(rawValue: raw.action), !raw.paths.isEmpty else { return nil }
            return Entry(id: raw.id, title: raw.title, paths: raw.paths, action: action, why: raw.why,
                         steps: raw.steps, settingsPane: raw.settingsPane, regrows: raw.regrows ?? false,
                         command: raw.command, perChild: raw.perChild ?? false, keepNewest: raw.keepNewest ?? false)
        }
    }

    /// Expands "~" and a trailing "*" in the last component (prefix match on
    /// that folder's listing). Missing parents give nothing.
    public static func expand(_ pattern: String, home: String = NSHomeDirectory(),
                              list: (String) -> [String]? = { try? FileManager.default.contentsOfDirectory(atPath: $0) }) -> [String] {
        let path = pattern.hasPrefix("~/") ? home + pattern.dropFirst(1) : pattern
        guard path.hasSuffix("*") else { return [path] }
        let parent = (path as NSString).deletingLastPathComponent
        let prefix = String((path as NSString).lastPathComponent.dropLast())
        return (list(parent) ?? []).filter { $0.hasPrefix(prefix) }.sorted().map { parent + "/" + $0 }
    }

    /// Size on disk of one path and whether all of it could be read.
    public typealias Sizer = @Sendable (String) -> (bytes: Int64, complete: Bool)?

    /// Walks each path with the scan engine (allocated bytes, same rules as a
    /// scan). Nil when the path doesn't exist.
    public static let walkSizer: Sizer = { path in
        var info = stat()
        guard lstat(path, &info) == 0 else { return nil }
        if (info.st_mode & S_IFMT) != S_IFDIR { return (Int64(info.st_blocks) * 512, true) }
        let result = BulkScan.walk(root: URL(fileURLWithPath: path, isDirectory: true), progress: nil)
        let totals = result.tree.rollUpBoth()
        return (totals.allocated.first ?? 0, result.deniedDirectoryIDs.isEmpty)
    }

    /// Measures every entry. Entries with nothing on this Mac are left out.
    public static func measure(entries: [Entry] = loadEntries(), home: String = NSHomeDirectory(),
                               list: (String) -> [String]? = { try? FileManager.default.contentsOfDirectory(atPath: $0) },
                               sizer: Sizer = walkSizer) -> [Item] {
        entries.compactMap { entry in
            var found: [(String, Int64)] = []
            var partial = false
            var seen = Set<String>()
            var keep = Set<String>()
            for pattern in entry.paths {
                for folder in expand(pattern, home: home, list: list) {
                    let paths = entry.perChild
                        ? (list(folder) ?? []).filter { !$0.hasPrefix(".") }.sorted().map { folder + "/" + $0 }
                        : [folder]
                    var measured: [String] = []
                    for path in paths where seen.insert(path).inserted {
                        guard let size = sizer(path) else { continue }
                        found.append((path, max(0, size.bytes)))
                        measured.append(path)
                        if !size.complete { partial = true }
                    }
                    if entry.keepNewest {
                        // No readable version: suggest nothing rather than everything.
                        if let newest = newestVersion(measured) { keep.insert(newest) } else { keep.formUnion(measured) }
                    }
                }
            }
            guard !found.isEmpty else { return nil }
            return Item(entry: entry, found: found, isPartial: partial, keep: keep)
        }
        .sorted { $0.bytes > $1.bytes }
    }

    /// The version in a DeviceSupport-style folder name: the first token made
    /// only of digits and dots ("17.4 (21E219)" → 17.4, "iPhone15,2 17.4
    /// (21E219)" → 17.4). Nil when there is none.
    public static func version(of name: String) -> [Int]? {
        for token in name.split(separator: " ") {
            let parts = token.split(separator: ".", omittingEmptySubsequences: false)
            guard !parts.isEmpty, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isASCII) && $0.allSatisfy(\.isNumber) }) else { continue }
            return parts.compactMap { Int($0) }
        }
        return nil
    }

    /// The path whose last component has the highest version; nil when none
    /// has a version (then nothing is suggested to keep or remove).
    static func newestVersion(_ paths: [String]) -> String? {
        let versioned = paths.compactMap { path -> (String, [Int])? in
            version(of: (path as NSString).lastPathComponent).map { (path, $0) }
        }
        return versioned.max { lhs, rhs in lhs.1.lexicographicallyPrecedes(rhs.1) }?.0
    }
}
