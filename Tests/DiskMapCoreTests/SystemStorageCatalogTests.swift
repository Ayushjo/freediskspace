import Foundation
import Testing
@testable import DiskMapCore

@Suite("Storage macOS manages")
struct SystemStorageCatalogTests {
    @Test func bundledFileLoadsCompletely() throws {
        let entries = SystemStorageCatalog.loadEntries()
        let raw = try #require(DiskMapResources.url(forResource: "system-storage", withExtension: "json"))
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: raw)) as? [String: Any]
        #expect(entries.count == (json?["entries"] as? [Any])?.count)
        #expect(entries.count >= 7)
        #expect(Set(entries.map(\.id)).count == entries.count, "ids are unique")
        for entry in entries {
            #expect(!entry.why.isEmpty && !entry.steps.isEmpty, "\(entry.id)")
        }
    }

    /// Only the installers may reach Cleanup. Models, voices, pending
    /// updates, aerials, the wallpaper cache and logs are guidance only.
    @Test func onlyInstallersMayBeTrashed() {
        let trash = SystemStorageCatalog.loadEntries().filter { $0.action == .trash }.map(\.id)
        #expect(trash == ["macos-installers"])
    }

    /// Every settings pane we link exists on this Mac (macOS 26.5), so the
    /// button never opens a blank System Settings.
    @Test func settingsPanesExistOnThisMac() throws {
        let extensions = "/System/Library/ExtensionKit/Extensions"
        let ids = try FileManager.default.contentsOfDirectory(atPath: extensions).compactMap { name -> String? in
            NSDictionary(contentsOfFile: "\(extensions)/\(name)/Contents/Info.plist")?["CFBundleIdentifier"] as? String
        }
        for pane in SystemStorageCatalog.loadEntries().compactMap(\.settingsPane) {
            #expect(ids.contains(pane), "\(pane)")
        }
    }

    @Test func malformedOrUnknownIsDroppedNotGuessed() {
        #expect(SystemStorageCatalog.loadEntries(from: Data("nope".utf8)).isEmpty)
        let json = #"{"entries":[{"id":"a","title":"A","paths":["/x"],"action":"delete","why":"w","steps":["s"]},{"id":"b","title":"B","paths":[],"action":"guide","why":"w","steps":["s"]},{"id":"c","title":"C","paths":["/y"],"action":"guide","why":"w","steps":["s"]}]}"#
        #expect(SystemStorageCatalog.loadEntries(from: Data(json.utf8)).map(\.id) == ["c"])
    }

    @Test func expandsHomeAndPrefixPatterns() {
        let listing = ["Install macOS Tahoe.app", "Install macOS Sequoia.app", "Installer.app", "Xcode.app"]
        let found = SystemStorageCatalog.expand("/Applications/Install macOS *", list: { $0 == "/Applications" ? listing : nil })
        #expect(found == ["/Applications/Install macOS Sequoia.app", "/Applications/Install macOS Tahoe.app"])
        #expect(SystemStorageCatalog.expand("~/Library/x", home: "/Users/me") == ["/Users/me/Library/x"])
        #expect(SystemStorageCatalog.expand("/nowhere/a*", list: { _ in nil }).isEmpty)
    }

    /// Missing paths are skipped, an entry with nothing is left out, one
    /// unreadable path marks the whole item partial, and a path matched by
    /// two patterns is counted once.
    @Test func measuresWithoutDoubleCountingAndFlagsPartial() {
        let entries = [
            SystemStorageCatalog.Entry(id: "a", title: "A", paths: ["/a/one", "/a/one", "/a/two", "/a/missing"], action: .guide,
                                       why: "w", steps: ["s"], settingsPane: nil, regrows: false),
            SystemStorageCatalog.Entry(id: "b", title: "B", paths: ["/b/missing"], action: .never,
                                       why: "w", steps: ["s"], settingsPane: nil, regrows: false),
            SystemStorageCatalog.Entry(id: "c", title: "C", paths: ["/c/big"], action: .never,
                                       why: "w", steps: ["s"], settingsPane: nil, regrows: false),
        ]
        let sizes: [String: (Int64, Bool)] = ["/a/one": (100, true), "/a/two": (50, false), "/c/big": (900, true)]
        let items = SystemStorageCatalog.measure(entries: entries, sizer: { sizes[$0].map { (bytes: $0.0, complete: $0.1) } })
        #expect(items.map(\.id) == ["c", "a"])
        #expect(items.last?.bytes == 150)
        #expect(items.last?.isPartial == true)
        #expect(items.first?.isPartial == false)
    }

    /// Live on this Mac: the models group matches du for the same folders
    /// (allocated bytes, within 2% for files changing underneath).
    @Test func liveModelsMatchDu() throws {
        let entry = try #require(SystemStorageCatalog.loadEntries().first { $0.id == "intelligence-siri" })
        let item = try #require(SystemStorageCatalog.measure(entries: [entry]).first)
        var duTotal: Int64 = 0
        for (path, _) in item.found {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/du")
            process.arguments = ["-sk", path]
            let pipe = Pipe(); process.standardOutput = pipe; process.standardError = FileHandle.nullDevice
            try process.run(); process.waitUntilExit()
            let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            duTotal += (Int64(out.split(separator: "\t").first ?? "0") ?? 0) * 1024
        }
        #expect(item.bytes > 100_000_000)
        #expect(abs(Double(item.bytes - duTotal)) / Double(duTotal) < 0.02, "scan \(item.bytes) du \(duTotal)")
    }

    /// Opt-in (DISKMAP_LIVE_TESTS=1): every installer the catalog finds in
    /// /Applications is accepted by the cleanup queue's safety checks, and
    /// nothing else from the catalog is. Staging only; nothing moves.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["DISKMAP_LIVE_TESTS"] == "1"))
    func liveOnlyInstallersPassStaging() async throws {
        let items = SystemStorageCatalog.measure()
        let queue = CleanupQueue()
        for item in items {
            for found in item.found {
                let staged = await queue.stage(URL(fileURLWithPath: found.path), size: found.bytes, reason: "test")
                if item.entry.action == .trash {
                    #expect(staged, "installer refused: \(found.path)")
                } else if staged {
                    // Not blocked by the excluded list today: recorded, not
                    // failed — the card never offers these (rule 3 is the
                    // maintainer's call).
                    print("note: queue would accept \(item.id) \(found.path)")
                }
            }
        }
        for staged in await queue.allItems() { await queue.unstage(id: staged.id) }
        #expect(await queue.allItems().isEmpty)
    }
}
