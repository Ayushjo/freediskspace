import Foundation
import Testing
@testable import DiskMapCore

/// Leftover matching must not take another app's data (AGENTS.md: fewer
/// false positives). Until 2026-10-21 any item starting with the bundle ID
/// matched, so Chrome's uninstall listed Chrome Canary's folders.
@Suite("App leftover matching")
struct AppLeftoverMatchingTests {
    private func matches(_ item: String, _ bid: String?, name: String = "Some App", installed: Set<String> = []) -> Bool {
        AppLeftoverFinder.matchesApp(itemName: item, bundleID: bid, appName: name, otherInstalledIDs: installed)
    }

    @Test func siblingAppsWithALongerBundleIDAreNotMatched() {
        #expect(!matches("com.google.Chrome.canary", "com.google.Chrome"))
        #expect(!matches("com.google.Chrome.canary.plist", "com.google.Chrome"))
        #expect(!matches("org.mozilla.firefoxdeveloperedition", "org.mozilla.firefox"))
        #expect(!matches("com.google.chrome.for.testing.plist", "com.google.Chrome"))
        #expect(!matches("com.apple.Safari", "com.apple.Saf"))
    }

    @Test func theAppsOwnItemsAreMatched() {
        #expect(matches("com.google.Chrome", "com.google.Chrome"))
        #expect(matches("com.google.chrome", "com.google.Chrome"))
        #expect(matches("com.google.Chrome.plist", "com.google.Chrome"))
        #expect(matches("com.google.Chrome.savedState", "com.google.Chrome"))
        #expect(matches("com.google.Chrome.binarycookies", "com.google.Chrome"))
        #expect(matches("com.example.app.6F9619FF-8B86-D011-B42D-00C04FC964FF", "com.example.app"))
    }

    /// Real items from installed apps on the maintainer's Mac that the
    /// first strict version missed: updater caches, extra prefs, extensions.
    @Test func theAppsOwnPartsAreMatched() {
        #expect(matches("com.anthropic.claudefordesktop.ShipIt", "com.anthropic.claudefordesktop"))
        #expect(matches("dev.notchy.app.sparkle", "dev.notchy.app"))
        #expect(matches("com.lwouis.alt-tab-macos.usage.plist", "com.lwouis.alt-tab-macos"))
        #expect(matches("com.lwouis.alt-tab-macos.license.plist", "com.lwouis.alt-tab-macos"))
        #expect(matches("com.apple.iWork.Keynote.KeynoteNotificationServiceExtension", "com.apple.iWork.Keynote"))
        #expect(matches("dev.notchy.app.widget", "dev.notchy.app"))
        #expect(matches("com.cloudflare.1dot1dot1dot1.macos.warp.daemon.plist", "com.cloudflare.1dot1dot1dot1.macos"))
        #expect(matches("ru.keepcoder.Telegram.TelegramShare", "ru.keepcoder.Telegram"))
        #expect(matches("net.whatsapp.WhatsApp.WAAppKitBridgeService", "net.whatsapp.WhatsApp"))
        #expect(matches("com.microsoft.rdc.macos.qlx", "com.microsoft.rdc.macos"))
    }

    /// A sibling app that is installed never loses its data to another's
    /// uninstall, even when its ID contains a helper-like word.
    @Test func anInstalledSiblingKeepsItsItems() {
        let installed: Set = ["com.example.app", "com.example.app.helper"]
        #expect(!matches("com.example.app.helper", "com.example.app", installed: installed))
        #expect(!matches("com.example.app.helper.plist", "com.example.app", installed: installed))
        #expect(matches("com.example.app.helper", "com.example.app"))
    }

    @Test func nameFallbackNeedsAnExactSpecificName() {
        #expect(matches("Obsidian", nil, name: "Obsidian"))
        #expect(matches("obsidian.plist", nil, name: "Obsidian"))
        #expect(!matches("Obsidian Helper", nil, name: "Obsidian"))
        #expect(!matches("Mail", nil, name: "Mail"))
    }
}
