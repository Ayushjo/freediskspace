import Foundation

/// Deletes Apple Time Machine local snapshots. This is the one place the Mac
/// app removes data without going to the Trash: a snapshot can't be moved to
/// the Trash, and the maintainer approved this exception (AGENTS.md rule 1,
/// decision D1 in docs/STORAGE-RESEARCH-PLAN.md). `SnapshotRemovalPolicyTests`
/// fails if `deletelocalsnapshots` appears in any other source file.
///
/// Measured on macOS 26.5 (2026-10-22), as a normal user, no root:
/// - `tmutil deletelocalsnapshots 2026-10-22-094830` deleted that snapshot
///   (exit 0) and it was gone from `listlocalsnapshots` and diskutil.
/// - A date with no snapshot exits 70; anything not shaped like a date is
///   read as a DISK ("garbage is not a valid disk", exit 22) — so "/" would
///   delete every snapshot on the disk. Only a validated date is ever passed.
///
/// Only `com.apple.TimeMachine.<yyyy-MM-dd-HHmmss>.local` snapshots qualify.
/// macOS-update snapshots and other apps' snapshots (Carbon Copy Cloner…)
/// are never touched. The backup disk itself is never touched either:
/// `deletelocalsnapshots` only removes local copies.
public enum TimeMachineSnapshotRemoval {
    public enum Outcome: Sendable, Equatable {
        case deleted
        /// It had already gone (macOS thins these on its own).
        case alreadyGone
        /// Not an Apple Time Machine snapshot, or a malformed name.
        case refused
        case failed(String)
    }

    public struct Result: Sendable, Equatable {
        public var name: String
        public var outcome: Outcome
    }

    /// Runs a tool with arguments (never through a shell) and returns its exit
    /// status and combined output. Injected by tests.
    public typealias Runner = @Sendable (_ tool: String, _ arguments: [String]) -> (status: Int32, output: String)

    /// The date argument `tmutil deletelocalsnapshots` takes, or nil when
    /// `name` isn't exactly an Apple Time Machine snapshot name.
    public static func date(forSnapshotName name: String) -> String? {
        let prefix = "com.apple.TimeMachine.", suffix = ".local"
        guard name.hasPrefix(prefix), name.hasSuffix(suffix),
              name.count == prefix.count + 17 + suffix.count else { return nil }
        let stamp = name.dropFirst(prefix.count).dropLast(suffix.count)
        // yyyy-MM-dd-HHmmss: digits with dashes at fixed places, nothing else.
        for (index, character) in stamp.enumerated() {
            let isDash = index == 4 || index == 7 || index == 10
            if isDash ? character != "-" : !("0"..."9").contains(character) { return nil }
        }
        return String(stamp)
    }

    /// Deletes each of `names` that is an Apple Time Machine snapshot still
    /// listed on `volume` right now, one at a time, and checks it's gone.
    /// `list` re-reads the volume's snapshot names.
    public static func delete(names: [String], volume: String,
                              list: (@Sendable () -> [String])? = nil,
                              run: Runner = runTool) -> [Result] {
        let list = list ?? { liveNames(volume: volume) }
        var results: [Result] = []
        for name in names {
            guard let date = date(forSnapshotName: name) else {
                results.append(Result(name: name, outcome: .refused))
                continue
            }
            guard list().contains(name) else {
                results.append(Result(name: name, outcome: .alreadyGone))
                continue
            }
            let (status, output) = run("/usr/bin/tmutil", ["deletelocalsnapshots", date])
            let stillThere = list().contains(name)
            if status == 0 && !stillThere {
                results.append(Result(name: name, outcome: .deleted))
            } else if !stillThere {
                results.append(Result(name: name, outcome: .alreadyGone))
            } else {
                let reason = output.split(separator: "\n").first.map(String.init) ?? "tmutil exited \(status)"
                results.append(Result(name: name, outcome: .failed(reason)))
            }
        }
        return results
    }

    /// Time Machine snapshot names on the boot disk, from `tmutil`.
    public static func liveNames(volume: String) -> [String] {
        let (status, output) = runTool("/usr/bin/tmutil", ["listlocalsnapshots", volume])
        guard status == 0 else { return [] }
        return output.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { date(forSnapshotName: $0) != nil }
    }

    public static let runTool: Runner = { tool, arguments in
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        do { try process.run() } catch { return (-1, error.localizedDescription) }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (process.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}
