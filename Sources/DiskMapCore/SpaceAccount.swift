import Foundation

/// "Where's the rest?": the disk's used space split into parts that add up,
/// each with how sure we are. Replaces one vague "snapshots or purgeable"
/// line. Only figures the system gives without root are used; what can't be
/// known (a snapshot's size, what purgeable space holds, how Storage
/// Settings classifies files) is never claimed. Research:
/// docs/STORAGE-RESEARCH-PLAN.md §1.
public struct SpaceAccount: Sendable, Equatable {
    public enum Confidence: String, Sendable, Equatable {
        /// Reported by the system for exactly this.
        case exact
        /// The system's own estimate, or what is left after the exact parts.
        case remainder
    }

    public enum Part: Sendable, Equatable {
        case scanned
        case volume(ContainerBreakdown.Role)
        case recoveryAndMetadata
        case notSeen
    }

    public struct Row: Sendable, Equatable, Identifiable {
        public var part: Part
        public var title: String
        public var bytes: Int64
        public var confidence: Confidence
        public var detail: String
        public var id: String { title }
    }

    /// Why part of the volume wasn't seen. Counts, not sizes: APFS gives no
    /// size for a snapshot, and an unreadable folder can't be measured.
    public struct NotSeenReasons: Sendable, Equatable {
        public var outsideScannedFolder: Bool
        public var timeMachineSnapshots: Int
        public var otherSnapshots: [String]
        public var unreadableFolders: Int
        public init(outsideScannedFolder: Bool = false, timeMachineSnapshots: Int = 0,
                    otherSnapshots: [String] = [], unreadableFolders: Int = 0) {
            self.outsideScannedFolder = outsideScannedFolder
            self.timeMachineSnapshots = timeMachineSnapshots
            self.otherSnapshots = otherSnapshots
            self.unreadableFolders = unreadableFolders
        }
    }

    public var usedBytes: Int64
    public var rows: [Row]
    public var reasons: NotSeenReasons
    /// macOS's own purgeable estimate. Not a row: it overlaps files the scan
    /// sees (caches) and snapshot space, so it can't be added to the others.
    public var purgeableBytes: Int64
    /// The scan counted more than the volumes report used — APFS clones are
    /// counted once per copy in a walk.
    public var scanExceedsCovered: Bool

    public var total: Int64 { rows.reduce(0) { $0 + $1.bytes } }

    /// Pure. `scanRootPath` decides which volumes the scan covered: a scan of
    /// "/" walks the System volume and, through firmlinks, the Data volume;
    /// anything else covers only the volume holding it.
    public static func build(breakdown: ContainerBreakdown, scanRootPath: String, scanRootMountPoint: String,
                             scannedOnDiskBytes: Int64, purgeableBytes: Int64,
                             snapshots: [LocalSnapshots.Snapshot], unreadableFolders: Int) -> SpaceAccount {
        let wholeDisk = scanRootPath == "/"
        let covered: (ContainerBreakdown.Volume) -> Bool = { volume in
            wholeDisk ? (volume.role == .system || volume.role == .data) : volume.mountPoint == scanRootMountPoint
        }
        let coveredUsed = breakdown.volumes.filter(covered).reduce(Int64(0)) { $0 + $1.usedBytes }
        let scanned = max(0, scannedOnDiskBytes)
        let notSeen = max(0, coveredUsed - scanned)
        let rootIsVolumeRoot = wholeDisk || scanRootPath == scanRootMountPoint
            || (scanRootMountPoint == "/System/Volumes/Data" && scanRootPath == "/System/Volumes/Data")

        var rows: [Row] = [Row(part: .scanned, title: "Found by this scan", bytes: scanned,
                               confidence: .exact, detail: "Every folder and file the scan could read.")]
        for volume in breakdown.volumes where !covered(volume) && volume.usedBytes > 0 {
            rows.append(Row(part: .volume(volume.role), title: volume.role.title, bytes: volume.usedBytes,
                            confidence: .exact, detail: detail(for: volume, swap: breakdown.swapInUseBytes)))
        }
        if breakdown.unmountedAndMetadataBytes > 0 {
            rows.append(Row(part: .recoveryAndMetadata, title: "Recovery and disk bookkeeping",
                            bytes: breakdown.unmountedAndMetadataBytes, confidence: .remainder,
                            detail: "The Recovery system (not mounted while macOS runs) and APFS's own records."))
        }
        let timeMachine = snapshots.filter { $0.kind.isTimeMachine }.count
        let others = Array(Set(snapshots.compactMap { snapshot -> String? in
            if case .other(let owner) = snapshot.kind { return owner }
            return nil
        })).sorted()
        let reasons = NotSeenReasons(outsideScannedFolder: !rootIsVolumeRoot, timeMachineSnapshots: timeMachine,
                                     otherSnapshots: others, unreadableFolders: unreadableFolders)
        if notSeen > 0 {
            rows.append(Row(part: .notSeen, title: rootIsVolumeRoot ? "Not seen by the scan" : "Elsewhere on this disk",
                            bytes: notSeen, confidence: .remainder, detail: notSeenDetail(reasons)))
        }
        return SpaceAccount(usedBytes: breakdown.containerUsedBytes, rows: rows, reasons: reasons,
                            purgeableBytes: max(0, purgeableBytes), scanExceedsCovered: scanned > coveredUsed)
    }

    static func detail(for volume: ContainerBreakdown.Volume, swap: Int64?) -> String {
        switch volume.role {
        case .system: return "The sealed macOS system. Read-only; it isn't yours to clean."
        case .swap:
            let inUse = swap.map { " \(ByteCountFormatter.string(fromByteCount: $0, countStyle: .file)) of it is in use right now." } ?? ""
            return "Memory macOS moved to disk, plus the sleep image.\(inUse) It shrinks after a restart."
        case .preboot: return "What your Mac needs to start up, kept by macOS."
        case .update: return "Staging for macOS updates; macOS clears it."
        case .data: return "Your files, apps and settings."
        case .other: return "Another volume on this disk."
        }
    }

    static func notSeenDetail(_ reasons: NotSeenReasons) -> String {
        var parts: [String] = []
        if reasons.outsideScannedFolder { parts.append("files outside the folder you scanned") }
        if reasons.unreadableFolders > 0 {
            parts.append("\(reasons.unreadableFolders) folder\(reasons.unreadableFolders == 1 ? "" : "s") the scan couldn't read")
        }
        if reasons.timeMachineSnapshots > 0 {
            let n = reasons.timeMachineSnapshots
            parts.append("\(n) Time Machine snapshot\(n == 1 ? "" : "s") (macOS frees them when space runs low)")
        }
        if !reasons.otherSnapshots.isEmpty {
            parts.append("snapshots kept by \(reasons.otherSnapshots.joined(separator: ", "))")
        }
        if parts.isEmpty {
            return "Space no folder accounts for — often cached or deleted data macOS still holds, or files only it can read."
        }
        return "Likely " + parts.joined(separator: ", ") + ". Snapshot sizes can't be measured, so this is what's left over."
    }
}
