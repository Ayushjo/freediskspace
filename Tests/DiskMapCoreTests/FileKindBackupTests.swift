import Foundation
import Testing
@testable import DiskMapCore

/// iPhone / iPad backups live in MobileSync/Backup/<device id>/ as hashed
/// files plus Manifest.db (Apple 108809). Before 2026-10-21 none of these
/// were recognised: the old patterns looked for "mobilebackup", or "backup"
/// with ".backup"/"iphone", and Manifest.db was called a database.
@Suite("Device backup kind")
struct FileKindBackupTests {
    private let backup = "/Users/me/Library/Application Support/MobileSync/Backup/00008110-001A2C3E0A88801E"

    @Test func filesInsideAPhoneBackupAreDeviceBackups() {
        #expect(FileKind.classify(fileName: "3d0d7e5fb2ce288813306e4d4636395e047a3d28",
                                  path: backup + "/3d/3d0d7e5fb2ce288813306e4d4636395e047a3d28") == .deviceBackup)
        #expect(FileKind.classify(fileName: "Manifest.db", path: backup + "/Manifest.db") == .deviceBackup)
        #expect(FileKind.classify(fileName: "Info.plist", path: backup + "/Info.plist") == .deviceBackup)
    }

    @Test func similarLookingPathsAreNotBackups() {
        #expect(FileKind.classify(fileName: "backup.db", path: "/Users/me/Documents/backup.db") == .database)
        #expect(FileKind.classify(fileName: "notes.pdf", path: "/Users/me/MobileSync notes/notes.pdf") == .document)
    }
}
