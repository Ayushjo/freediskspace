import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// First launch and scanning (redesign boards 11 and 12).
@Suite("First launch and scanning")
struct FirstLaunchTests {
    @Test func readingFolderIsTheTopFolderThatHoldsIt() {
        #expect(ScanningBoard.topFolder(of: "/Users/me/Library/Caches/x", root: "/Users/me") == "Library")
        #expect(ScanningBoard.topFolder(of: "/Users/me/Downloads", root: "/Users/me/") == "Downloads")
        #expect(ScanningBoard.topFolder(of: "/Users/meow/x", root: "/Users/me") == nil, "a sibling is not inside")
        #expect(ScanningBoard.topFolder(of: "/Applications/X.app", root: "/") == "Applications")
    }

    @Test func foundFigureSplitsNumberAndUnit() {
        #expect(ScanningBoard.splitBytes(212_000_000_000) == ("212", "GB"))
        #expect(ScanningBoard.splitBytes(67_300_000_000) == ("67.3", "GB"))
    }

    @Test func onlyAWholeDiskScanClaimsAPercentage() {
        #expect(ScanningBoard.fraction(found: 10, root: URL(fileURLWithPath: NSHomeDirectory())) == nil,
                "a home scan has no known end, so no made-up percentage")
        #expect(ScanningBoard.fraction(found: 10, root: nil) == nil)
        if let f = ScanningBoard.fraction(found: Int64.max / 2, root: URL(fileURLWithPath: "/")) {
            #expect(f <= 0.99, "never 100% before the walk ends")
        }
    }

    @Test func titlesSayWhatIsBeingScanned() {
        #expect(ScanningBoard.title(root: URL(fileURLWithPath: NSHomeDirectory()), phase: .walking) == "Scanning your home folder")
        #expect(ScanningBoard.title(root: URL(fileURLWithPath: "/tmp/project"), phase: .walking) == "Scanning project")
        #expect(ScanningBoard.title(root: nil, phase: .summarizing) == "Putting it together")
        #expect(ScanningBoard.title(root: nil, phase: .checkingChanges) == "Checking what changed")
    }
}
