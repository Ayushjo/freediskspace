import DiskMapCore
import Foundation

/// The Explore list's search box: plain words plus a few tokens
/// (`size>1GB`, `kind:video`, `changed>1y`, `in:Downloads`, `name:report`).
/// Anything it doesn't recognise is searched as text, so nothing typed is lost.
struct ListQuery: Equatable {
    var words: [String] = []
    var minSize: Int64?
    var maxSize: Int64?
    var kind: FileKind?
    /// Not changed for at least this many days.
    var olderThanDays: Int?
    /// Changed within this many days.
    var newerThanDays: Int?
    /// Lowercased folder name or path fragment the item must sit under.
    var inFolder: String?
    /// The tokens that were understood, as typed, for the field to show.
    var tokens: [String] = []

    var isEmpty: Bool { words.isEmpty && tokens.isEmpty }

    static func parse(_ text: String) -> ListQuery {
        var q = ListQuery()
        for raw in text.split(whereSeparator: \.isWhitespace).map(String.init) {
            let part = raw.lowercased()
            if let (op, value) = split(part, key: "size"), let bytes = bytes(value) {
                if op == ">" { q.minSize = bytes } else if op == "<" { q.maxSize = bytes } else { continue }
                q.tokens.append(raw)
            } else if part.hasPrefix("kind:"), let kind = kind(String(part.dropFirst(5))) {
                q.kind = kind
                q.tokens.append(raw)
            } else if let (op, value) = split(part, key: "changed") ?? split(part, key: "opened") ?? split(part, key: "modified"),
                      let days = days(value) {
                if op == ">" { q.olderThanDays = days } else if op == "<" { q.newerThanDays = days } else { continue }
                q.tokens.append(raw)
            } else if part.hasPrefix("in:"), part.count > 3 {
                q.inFolder = String(part.dropFirst(3))
                q.tokens.append(raw)
            } else if part.hasPrefix("name:"), part.count > 5 {
                q.words.append(String(part.dropFirst(5)))
                q.tokens.append(raw)
            } else {
                q.words.append(part)
            }
        }
        return q
    }

    /// `searchText` is the lowercased name and display path; `folderPath` the
    /// lowercased display path of the containing folder.
    func matches(searchText: String, folderPath: String, size: Int64, kind: FileKind, ageDays: Int?) -> Bool {
        if let minSize, size <= minSize { return false }
        if let maxSize, size >= maxSize { return false }
        if let want = self.kind, kind != want { return false }
        if let olderThanDays { guard let ageDays, ageDays >= olderThanDays else { return false } }
        if let newerThanDays { guard let ageDays, ageDays <= newerThanDays else { return false } }
        if let inFolder, !folderPath.contains(inFolder) { return false }
        return words.allSatisfy { searchText.contains($0) }
    }

    // MARK: Parsing pieces

    /// "size>1gb" → (">", "1gb"); also accepts "size:>1gb" and "size:1gb" (meaning >).
    private static func split(_ part: String, key: String) -> (String, String)? {
        guard part.hasPrefix(key) else { return nil }
        var rest = part.dropFirst(key.count)
        if rest.first == ":" { rest = rest.dropFirst() }
        guard let first = rest.first else { return nil }
        if first == ">" || first == "<" { return (String(first), String(rest.dropFirst())) }
        return part.dropFirst(key.count).first == ":" ? (">", String(rest)) : nil
    }

    /// "1gb", "500mb", "2.5tb", "100k" → bytes (decimal, as Finder counts).
    static func bytes(_ text: String) -> Int64? {
        let units: [(String, Double)] = [("tb", 1e12), ("gb", 1e9), ("mb", 1e6), ("kb", 1e3), ("t", 1e12), ("g", 1e9), ("m", 1e6), ("k", 1e3), ("b", 1)]
        for (suffix, scale) in units where text.hasSuffix(suffix) {
            guard let n = Double(text.dropLast(suffix.count)), n >= 0 else { return nil }
            return Int64(n * scale)
        }
        guard let n = Double(text), n >= 0 else { return nil }
        return Int64(n)
    }

    /// "30d", "6w", "3m", "1y" → days.
    static func days(_ text: String) -> Int? {
        guard let unit = text.last, let n = Double(text.dropLast()), n >= 0 else { return nil }
        switch unit {
        case "d": return Int(n)
        case "w": return Int(n * 7)
        case "m": return Int(n * 30)
        case "y": return Int(n * 365)
        default: return nil
        }
    }

    private static func kind(_ text: String) -> FileKind? {
        switch text {
        case "video", "videos", "movie", "movies": return .video
        case "image", "disk", "diskimage", "dmg", "iso", "installer", "installers": return .diskImage
        case "archive", "archives", "zip": return .archive
        case "app", "apps", "application", "applications": return .application
        case "doc", "docs", "document", "documents": return .document
        case "db", "database", "databases": return .database
        case "vm", "vms", "virtualdisk": return .virtualDisk
        case "backup", "backups": return .deviceBackup
        default: return nil
        }
    }
}
