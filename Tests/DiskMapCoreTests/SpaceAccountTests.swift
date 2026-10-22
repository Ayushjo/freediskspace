import Foundation
import Testing
@testable import DiskMapCore

/// "Where's the rest?" rows must add up to what the disk reports used, and
/// never claim a size nobody can measure (snapshots, purgeable contents).
@Suite("Where's the rest")
struct SpaceAccountTests {
    private let gb: Int64 = 1_000_000_000

    /// The maintainer's Mac: 495 GB container, 98.9 GB free.
    private var mac: ContainerBreakdown {
        ContainerBreakdown(containerTotalBytes: 494 * gb, containerFreeBytes: 98 * gb, volumes: [
            .init(mountPoint: "/System/Volumes/Data", role: .data, usedBytes: 348 * gb),
            .init(mountPoint: "/System/Volumes/VM", role: .swap, usedBytes: 21 * gb),
            .init(mountPoint: "/", role: .system, usedBytes: 13 * gb),
            .init(mountPoint: "/System/Volumes/Preboot", role: .preboot, usedBytes: 11 * gb),
        ], swapInUseBytes: 20 * gb)
    }

    private func snapshot(_ name: String) -> LocalSnapshots.Snapshot {
        .init(name: name, uuid: name, purgeable: true, kind: LocalSnapshots.kind(of: name), volumeMountPoint: "/System/Volumes/Data")
    }

    @Test func aHomeFolderScanAddsUpToUsed() {
        let account = SpaceAccount.build(breakdown: mac, scanRootPath: "/Users/me", scanRootMountPoint: "/System/Volumes/Data",
                                         scannedOnDiskBytes: 300 * gb, purgeableBytes: 12 * gb,
                                         snapshots: [snapshot("com.apple.TimeMachine.2026-10-21-101500.local")],
                                         unreadableFolders: 2)
        #expect(account.total == account.usedBytes)
        #expect(account.usedBytes == 396 * gb)
        #expect(account.rows.first { $0.part == .scanned }?.bytes == 300 * gb)
        #expect(account.rows.first { $0.part == .volume(.swap) }?.bytes == 21 * gb)
        #expect(account.rows.first { $0.part == .volume(.system) }?.bytes == 13 * gb)
        #expect(account.rows.first { $0.part == .recoveryAndMetadata }?.bytes == 3 * gb)
        let notSeen = account.rows.first { $0.part == .notSeen }
        #expect(notSeen?.bytes == 48 * gb)
        #expect(notSeen?.title == "Elsewhere on this disk")
        #expect(notSeen?.detail.contains("outside the folder") == true)
        #expect(notSeen?.detail.contains("2 folders the scan couldn't read") == true)
        #expect(notSeen?.detail.contains("1 Time Machine snapshot") == true)
        // Purgeable is a note, never a row: it overlaps scanned caches.
        #expect(account.purgeableBytes == 12 * gb)
        #expect(!account.rows.contains { $0.title.lowercased().contains("purgeable") })
    }

    @Test func aWholeDiskScanCoversSystemAndData() {
        let account = SpaceAccount.build(breakdown: mac, scanRootPath: "/", scanRootMountPoint: "/",
                                         scannedOnDiskBytes: 355 * gb, purgeableBytes: 0, snapshots: [], unreadableFolders: 0)
        #expect(account.total == account.usedBytes)
        #expect(!account.rows.contains { $0.part == .volume(.system) })
        #expect(!account.rows.contains { $0.part == .volume(.data) })
        #expect(account.rows.contains { $0.part == .volume(.swap) })
        let notSeen = account.rows.first { $0.part == .notSeen }
        #expect(notSeen?.bytes == 6 * gb)
        #expect(notSeen?.title == "Not seen by the scan")
        #expect(account.reasons.outsideScannedFolder == false)
    }

    @Test func otherAppsSnapshotsAreNamedNotSized() {
        let account = SpaceAccount.build(breakdown: mac, scanRootPath: "/", scanRootMountPoint: "/",
                                         scannedOnDiskBytes: 300 * gb, purgeableBytes: 0,
                                         snapshots: [snapshot("com.bombich.ccc.1234"), snapshot("com.bombich.ccc.5678")],
                                         unreadableFolders: 0)
        #expect(account.reasons.otherSnapshots == ["com.bombich.ccc"])
        #expect(account.rows.first { $0.part == .notSeen }?.detail.contains("kept by com.bombich.ccc") == true)
    }

    /// Clones are counted per copy in a walk: the scan can exceed the
    /// volumes. Nothing goes negative and the overshoot is flagged.
    @Test func clonedScanThatExceedsTheDiskIsFlagged() {
        let account = SpaceAccount.build(breakdown: mac, scanRootPath: "/", scanRootMountPoint: "/",
                                         scannedOnDiskBytes: 400 * gb, purgeableBytes: 0, snapshots: [], unreadableFolders: 0)
        #expect(account.scanExceedsCovered)
        #expect(!account.rows.contains { $0.part == .notSeen })
        #expect(account.rows.allSatisfy { $0.bytes >= 0 })
        // Capped, so the parts still add up to used, and the row says why.
        #expect(account.total == account.usedBytes)
        let found = try! #require(account.rows.first { $0.part == .scanned })
        #expect(found.detail.contains("once per copy"))
    }

    /// Live on this Mac: the home folder's account adds up to used.
    @Test func thisMacsHomeAccountAddsUp() throws {
        let breakdown = try #require(ContainerBreakdown.read(forPath: NSHomeDirectory()))
        let account = SpaceAccount.build(breakdown: breakdown, scanRootPath: NSHomeDirectory(),
                                         scanRootMountPoint: "/System/Volumes/Data", scannedOnDiskBytes: 1 * gb,
                                         purgeableBytes: 0, snapshots: [], unreadableFolders: 0)
        #expect(account.total == account.usedBytes)
    }

    /// The home folder lives on the Data volume of the boot container.
    @Test func homeFolderMountsOnTheDataVolume() throws {
        #expect(SpaceAccount.mountPoint(of: NSHomeDirectory()) == "/System/Volumes/Data")
        #expect(SpaceAccount.mountPoint(of: "/") == "/")
        #expect(SpaceAccount.mountPoint(of: "/no/such/path/\(UUID().uuidString)") == nil)
    }

    /// Opt-in (DISKMAP_LIVE_TESTS=1, runs diskutil): the full live read
    /// for a home scan adds up and names the home folder as partial.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["DISKMAP_LIVE_TESTS"] == "1"))
    func liveReadOfTheHomeFolderAddsUp() throws {
        let account = try #require(SpaceAccount.read(scanRootPath: NSHomeDirectory(), scannedOnDiskBytes: 1 * gb,
                                                     purgeableBytes: 0, unreadableFolders: 2))
        #expect(account.total == account.usedBytes)
        #expect(account.reasons.outsideScannedFolder)
        #expect(account.reasons.unreadableFolders == 2)
    }

    /// The snapshot caveat appears only when there are snapshots.
    @Test func snapshotCaveatOnlyWithSnapshots() {
        let plain = SpaceAccount.notSeenDetail(.init(outsideScannedFolder: true))
        #expect(plain == "Likely files outside the folder you scanned.")
        let withTM = SpaceAccount.notSeenDetail(.init(timeMachineSnapshots: 2))
        #expect(withTM.contains("2 Time Machine snapshots"))
        #expect(withTM.contains("can't be measured"))
        let none = SpaceAccount.notSeenDetail(.init())
        #expect(none.hasPrefix("Space no folder accounts for"))
    }

    /// Only Apple Time Machine snapshots are offered for deletion, newest
    /// first, once each even when listed on two volumes.
    @Test func offersOnlyTimeMachineSnapshotsForDeletion() {
        let tm1 = "com.apple.TimeMachine.2026-10-20-101500.local", tm2 = "com.apple.TimeMachine.2026-10-21-101500.local"
        let snaps: [LocalSnapshots.Snapshot] = [tm1, tm2, "com.apple.os.update-ABC", "com.bombich.ccc.9"].map {
            .init(name: $0, uuid: "", purgeable: true, kind: LocalSnapshots.kind(of: $0), volumeMountPoint: "/System/Volumes/Data")
        } + [.init(name: tm2, uuid: "", purgeable: true, kind: LocalSnapshots.kind(of: tm2), volumeMountPoint: "/")]
        let account = SpaceAccount.build(breakdown: mac, scanRootPath: "/", scanRootMountPoint: "/",
                                         scannedOnDiskBytes: 100 * gb, purgeableBytes: 0, snapshots: snaps, unreadableFolders: 0)
        #expect(account.timeMachineSnapshotNames == [tm2, tm1])
    }
}
