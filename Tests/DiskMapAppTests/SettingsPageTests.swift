import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Settings (redesign board 14): only real choices, and Reset leaves your data alone.
@Suite("Settings page")
@MainActor
struct SettingsPageTests {
    @Test func resetCoversEveryChoiceButNotYourData() {
        let keys = Set(SettingsView.resettableKeys)
        for key in [AppAppearance.storageKey, TextSize.storageKey, DustyPreference.key, CloneAccounting.key,
                    ScanModel.keepHistoryKey, CleanUpView.tickKey, DuplicatesView.KeepPolicy.key, "ShowMenuBarExtra"] {
            #expect(keys.contains(key), "\(key) is a choice on the page")
        }
        #expect(!keys.contains(LastScanRecord.key), "the last scan record is data, not a setting")
        #expect(keys.count == SettingsView.resettableKeys.count, "no key listed twice")
    }

    @Test func aBareBuildNeverAsksToOpenAtLogin() {
        // Tests run from a bare executable, never an .app: the system is not asked.
        #expect(!LoginItem.isAvailable)
        #expect(!LoginItem.isEnabled)
        #expect(throws: LoginItem.Unavailable.self) { try LoginItem.set(true) }
    }
}

@Suite("Sheets")
struct SheetTests {
    @Test func explainShortensHomePaths() {
        #expect(ExplainStorageSheet.shortenPaths("51 GB at \(NSHomeDirectory())/Pictures/a.heic.") == "51 GB at ~/Pictures/a.heic.")
        #expect(ExplainStorageSheet.shortenPaths("/Applications/X.app") == "/Applications/X.app")
    }

    @Test func confirmListIsCappedAndBiggestFirst() {
        #expect(PathListConfirmSheet.rowLimit == 400)
    }
}
