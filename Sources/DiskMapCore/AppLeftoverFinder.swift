import Foundation

public struct AppLeftoverEntry: Sendable, Equatable {
    public let url: URL
    public let bytes: Int64

    public init(url: URL, bytes: Int64) {
        self.url = url
        self.bytes = bytes
    }
}

public struct AppLeftovers: Sendable {
    public let bundleID: String?
    public let appName: String
    public let bundlePath: URL
    public let bundleSize: Int64
    public let leftoverPaths: [URL]
    public let leftoverEntries: [AppLeftoverEntry]
    public let leftoverSize: Int64

    public init(
        bundleID: String?,
        appName: String,
        bundlePath: URL,
        bundleSize: Int64,
        leftoverPaths: [URL],
        leftoverEntries: [AppLeftoverEntry] = [],
        leftoverSize: Int64
    ) {
        self.bundleID = bundleID
        self.appName = appName
        self.bundlePath = bundlePath
        self.bundleSize = bundleSize
        self.leftoverPaths = leftoverPaths
        self.leftoverEntries = leftoverEntries.isEmpty
            ? leftoverPaths.map { AppLeftoverEntry(url: $0, bytes: 0) }
            : leftoverEntries
        self.leftoverSize = leftoverSize
    }
}

/// Finds files an app scattered outside its own .app bundle when it was
/// installed/run — caches, preferences, saved state, containers, logs.
///
/// There is no Apple API that enumerates "everything this app touched."
/// Every open-source uninstaller in this space (AppCleaner, Pearcleaner,
/// this one) does the same heuristic: search known Library locations for
/// items whose name contains the app's bundle identifier (reverse-DNS,
/// e.g. "com.example.app" — rarely collides by accident) or, failing
/// that, the app's display name (fuzzier, more false-positive risk).
///
/// Because it's heuristic, leftovers should always land in a *staged*
/// cleanup list for the user to review — never delete automatically.
public enum AppLeftoverFinder {

    static let searchLocations: [String] = [
        "~/Library/Caches",
        "~/Library/Application Support",
        "~/Library/Preferences",
        "~/Library/Saved Application State",
        "~/Library/Containers",
        "~/Library/Group Containers",
        "~/Library/HTTPStorages",
        "~/Library/WebKit",
        "~/Library/Logs",
        "~/Library/LaunchAgents",
        "/Library/LaunchDaemons",
        "/Library/Application Support",
    ]

    public static func defaultApplicationDirectories() -> [URL] {
        [
            URL(fileURLWithPath: "/Applications", isDirectory: true),
            URL(fileURLWithPath: NSHomeDirectory() + "/Applications", isDirectory: true),
        ]
    }

    public static func applicationBundles(in directories: [URL]) -> [URL] {
        let fm = FileManager.default
        var apps: [URL] = []
        for directory in directories {
            guard let contents = try? fm.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: nil,
                options: [.skipsHiddenFiles]
            ) else { continue }
            apps.append(contentsOf: contents.filter { $0.pathExtension == "app" })
        }
        return apps.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
    }

    public static func findLeftovers(for appURL: URL, measureRelated: Bool = true,
                                     otherInstalledIDs: Set<String> = []) -> AppLeftovers {
        let bundle = Bundle(url: appURL)
        let bundleID = bundle?.bundleIdentifier
        let appName = appURL.deletingPathExtension().lastPathComponent

        var matches: [URL] = []
        let fm = FileManager.default

        for location in searchLocations {
            let expanded = (location as NSString).expandingTildeInPath
            let dirURL = URL(fileURLWithPath: expanded)
            guard let contents = try? fm.contentsOfDirectory(at: dirURL, includingPropertiesForKeys: nil) else {
                continue
            }

            for item in contents {
                let itemName = item.lastPathComponent
                if matchesApp(itemName: itemName, bundleID: bundleID, appName: appName, otherInstalledIDs: otherInstalledIDs) {
                    matches.append(item)
                }
            }
        }

        let bundleSize = allocatedSize(of: appURL)
        var entries: [AppLeftoverEntry] = []
        var leftoverSize: Int64 = 0
        if measureRelated {
            for url in matches {
                let bytes = allocatedSize(of: url)
                leftoverSize += bytes
                entries.append(AppLeftoverEntry(url: url, bytes: bytes))
            }
        } else {
            entries = matches.map { AppLeftoverEntry(url: $0, bytes: 0) }
        }

        return AppLeftovers(
            bundleID: bundleID,
            appName: appName,
            bundlePath: appURL,
            bundleSize: bundleSize,
            leftoverPaths: matches,
            leftoverEntries: entries,
            leftoverSize: leftoverSize
        )
    }


    /// File extensions an app's own items carry: `com.app.plist`,
    /// `com.app.savedState`, `com.app.binarycookies`, …
    static let fileSuffixes: Set<String> = ["plist", "savedstate", "binarycookies", "log", "lockfile", "sfl2", "sfl3"]

    /// Words that mark a sub-item as the app's own part rather than another
    /// app: its updater cache (`.ShipIt`, `.sparkle`), extra prefs
    /// (`.usage.plist`), extensions and helpers (`.widget`,
    /// `.NotificationServiceExtension`, `.warp.daemon.plist`).
    static let ownPartWords = ["shipit", "sparkle", "updater", "helper", "extension", "widget", "intents", "daemon",
                               "agent", "launcher", "login", "xpc", "crashpad", "usage", "license", "settings", "prefs",
                               "share", "service", "quicklook", "qlx", "thumbnail", "fileprovider"]

    /// Matches an item to an app by bundle ID. Before 2026-10-21 any item
    /// that merely started with the ID matched, so Chrome's uninstall listed
    /// `com.google.chrome.for.testing.plist` and Chrome Canary's data, and
    /// Firefox took Firefox Developer Edition's. Now, after the ID and an
    /// optional file extension, what is left must be empty, a UUID, or name
    /// an own part (`ownPartWords`), and must not be another installed app's
    /// ID. A missed leftover costs a few KB; a wrong one costs another app's
    /// data (AGENTS.md: bias toward fewer false positives). Name fallback
    /// only without a bundle ID, and only for specific names (not "Mail").
    static func matchesApp(itemName: String, bundleID: String?, appName: String,
                           otherInstalledIDs: Set<String> = []) -> Bool {
        let item = itemName.lowercased()
        if let bid = bundleID?.lowercased(), !bid.isEmpty {
            if item == bid { return true }
            guard item.hasPrefix(bid), item.count > bid.count else { return false }
            var rest = String(item.dropFirst(bid.count))
            guard let separator = rest.first, separator == "." || separator == "-" else { return false }
            rest.removeFirst()
            var labels = rest.split(separator: ".").map(String.init)
            if let last = labels.last, fileSuffixes.contains(last) { labels.removeLast() }
            if labels.isEmpty { return true }
            let middle = labels.joined(separator: ".")
            if UUID(uuidString: middle) != nil { return true }
            // Another installed app owns this ID (Canary, Chrome for Testing).
            let candidate = bid + "." + middle
            if otherInstalledIDs.contains(where: { $0.lowercased() == candidate || candidate.hasPrefix($0.lowercased() + ".") && $0.count > bid.count }) {
                return false
            }
            return labels.contains { label in ownPartWords.contains { label.contains($0) } }
        }
        let name = appName.lowercased()
        guard name.count >= 5 else { return false }
        return item == name || fileSuffixes.contains { item == name + "." + $0 }
    }

    /// On-disk size of a file or a folder's contents. A plain file used to
    /// come back as 0 (the enumerator yields nothing for a non-directory),
    /// so every .plist leftover showed 0 bytes.
    public static func allocatedSize(of url: URL) -> Int64 {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .totalFileAllocatedSizeKey, .fileAllocatedSizeKey]
        if let values = try? url.resourceValues(forKeys: keys), values.isDirectory != true {
            return Int64(values.totalFileAllocatedSize ?? values.fileAllocatedSize ?? 0)
        }
        guard let enumerator = FileManager.default.enumerator(
            at: url, includingPropertiesForKeys: [.totalFileAllocatedSizeKey]
        ) else { return 0 }

        var total: Int64 = 0
        for case let fileURL as URL in enumerator {
            if let size = try? fileURL.resourceValues(forKeys: [.totalFileAllocatedSizeKey]).totalFileAllocatedSize {
                total += Int64(size)
            }
        }
        return total
    }
}
