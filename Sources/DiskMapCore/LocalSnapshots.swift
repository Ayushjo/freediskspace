import Foundation

/// APFS snapshots on a volume, as `diskutil apfs listSnapshots -plist`
/// reports them — no root needed (checked on macOS 26.5). There is no public
/// way to size a snapshot, so only names, kinds and dates are known.
/// Time Machine snapshots count as purgeable to macOS (Apple 102154);
/// macOS-update and other apps' snapshots are not, and their space stays in
/// "still unexplained". Research: docs/STORAGE-RESEARCH-PLAN.md §1.
public enum LocalSnapshots {
    public enum Kind: Sendable, Equatable {
        /// `com.apple.TimeMachine.2026-10-21-201500.local`
        case timeMachine(Date?)
        /// `com.apple.os.update-…`: macOS keeps it for updates and rollback.
        case osUpdate
        /// Another app's (Carbon Copy Cloner, backup tools): named by owner.
        case other(owner: String)

        public var isTimeMachine: Bool {
            if case .timeMachine = self { return true }
            return false
        }
    }

    public struct Snapshot: Sendable, Equatable, Identifiable {
        public var name: String
        public var uuid: String
        public var purgeable: Bool
        public var kind: Kind
        public var volumeMountPoint: String
        public var id: String { uuid.isEmpty ? name : uuid }
    }

    /// Parses `diskutil apfs listSnapshots -plist <volume>` output. Unknown
    /// keys are ignored; malformed input gives no snapshots, not a crash.
    public static func parse(_ data: Data, volumeMountPoint: String) -> [Snapshot] {
        guard let root = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let list = root["Snapshots"] as? [[String: Any]] else { return [] }
        return list.compactMap { entry in
            guard let name = entry["SnapshotName"] as? String, !name.isEmpty else { return nil }
            return Snapshot(name: name,
                            uuid: entry["SnapshotUUID"] as? String ?? "",
                            purgeable: entry["Purgeable"] as? Bool ?? false,
                            kind: kind(of: name),
                            volumeMountPoint: volumeMountPoint)
        }
    }

    public static func kind(of name: String) -> Kind {
        let tmPrefix = "com.apple.TimeMachine."
        if name.hasPrefix(tmPrefix) {
            return .timeMachine(timeMachineDate(name))
        }
        if name.hasPrefix("com.apple.os.update-") { return .osUpdate }
        // "com.bombich.ccc.<uuid>" → "com.bombich.ccc"; anything else as is.
        let parts = name.split(separator: ".")
        let owner = parts.count >= 3 ? parts.prefix(3).joined(separator: ".") : name
        return .other(owner: owner)
    }

    /// `com.apple.TimeMachine.2026-10-21-201500.local` → that local time.
    static func timeMachineDate(_ name: String) -> Date? {
        let stamp = name.dropFirst("com.apple.TimeMachine.".count).prefix { $0 != "." }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyy-MM-dd-HHmmss"
        return formatter.date(from: String(stamp))
    }

    /// Lists snapshots on the given volumes (normally "/" and
    /// "/System/Volumes/Data"). Runs diskutil locally with a time limit;
    /// a volume that can't be listed is skipped.
    public static func read(volumes: [String], timeout: TimeInterval = 10) -> [Snapshot] {
        volumes.flatMap { volume -> [Snapshot] in
            guard let data = run("/usr/sbin/diskutil", ["apfs", "listSnapshots", "-plist", volume], timeout: timeout) else { return [] }
            return parse(data, volumeMountPoint: volume)
        }
    }

    static func run(_ tool: String, _ arguments: [String], timeout: TimeInterval) -> Data? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        let out = Pipe()
        process.standardOutput = out
        process.standardError = FileHandle.nullDevice
        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        do { try process.run() } catch { return nil }
        // Read on another thread so a large output can't fill the pipe and
        // stall the tool while we wait for it.
        let collected = LockedData()
        let readerDone = DispatchSemaphore(value: 0)
        DispatchQueue.global(qos: .utility).async {
            collected.set(out.fileHandleForReading.readDataToEndOfFile())
            readerDone.signal()
        }
        guard finished.wait(timeout: .now() + timeout) == .success else {
            process.terminate()
            return nil
        }
        _ = readerDone.wait(timeout: .now() + 2)
        return process.terminationStatus == 0 ? collected.get() : nil
    }

    private final class LockedData: @unchecked Sendable {
        private let lock = NSLock()
        private var data = Data()
        func set(_ value: Data) { lock.lock(); data = value; lock.unlock() }
        func get() -> Data { lock.lock(); defer { lock.unlock() }; return data }
    }
}
