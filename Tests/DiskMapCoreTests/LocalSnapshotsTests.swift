import Foundation
import Testing
@testable import DiskMapCore

@Suite("Local APFS snapshots")
struct LocalSnapshotsTests {
    /// Shape of real `diskutil apfs listSnapshots -plist` output (macOS 26.5,
    /// the os.update entry captured on the maintainer's Mac); the Time
    /// Machine and CCC entries follow the same keys.
    private let plist = """
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0"><dict><key>Snapshots</key><array>
      <dict><key>LimitingContainerShrink</key><true/><key>Purgeable</key><false/>
        <key>SnapshotName</key><string>com.apple.os.update-5B92CE4BA1034457A0921532291F4A4AD939CBE48D3A1381163A9AD3687D5694</string>
        <key>SnapshotUUID</key><string>5CD9985F-EE3B-447D-BCEA-46A6BE7A25BD</string><key>SnapshotXID</key><integer>511426</integer></dict>
      <dict><key>Purgeable</key><true/>
        <key>SnapshotName</key><string>com.apple.TimeMachine.2026-10-21-201500.local</string>
        <key>SnapshotUUID</key><string>11111111-2222-3333-4444-555555555555</string></dict>
      <dict><key>Purgeable</key><false/>
        <key>SnapshotName</key><string>com.bombich.ccc.6E3B21A0-1111-2222-3333-444455556666</string>
        <key>SnapshotUUID</key><string>AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE</string></dict>
    </array></dict></plist>
    """

    @Test func parsesKindsAndFlags() throws {
        let snapshots = LocalSnapshots.parse(Data(plist.utf8), volumeMountPoint: "/System/Volumes/Data")
        #expect(snapshots.count == 3)
        #expect(snapshots[0].kind == .osUpdate)
        #expect(snapshots[0].purgeable == false)
        #expect(snapshots[1].kind.isTimeMachine)
        #expect(snapshots[1].purgeable)
        #expect(snapshots[2].kind == .other(owner: "com.bombich.ccc"))
        #expect(snapshots.allSatisfy { $0.volumeMountPoint == "/System/Volumes/Data" })
    }

    @Test func timeMachineNameCarriesItsDate() throws {
        guard case .timeMachine(let date?) = LocalSnapshots.kind(of: "com.apple.TimeMachine.2026-10-21-201500.local") else {
            Issue.record("no date"); return
        }
        let parts = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        #expect(parts.year == 2026 && parts.month == 10 && parts.day == 21)
        #expect(parts.hour == 20 && parts.minute == 15 && parts.second == 0)
        #expect(LocalSnapshots.kind(of: "com.apple.TimeMachine.garbage.local") == .timeMachine(nil))
    }

    @Test func malformedOutputGivesNothing() {
        #expect(LocalSnapshots.parse(Data("not a plist".utf8), volumeMountPoint: "/").isEmpty)
        #expect(LocalSnapshots.parse(Data("<plist><dict/></plist>".utf8), volumeMountPoint: "/").isEmpty)
    }

    /// Live: diskutil lists the System volume's snapshots without root.
    @Test func listsThisMacsSnapshotsWithoutRoot() {
        let snapshots = LocalSnapshots.read(volumes: ["/"])
        // Every sealed-system Mac has macOS's own update snapshot on "/".
        #expect(snapshots.contains { $0.kind == .osUpdate })
    }
}
