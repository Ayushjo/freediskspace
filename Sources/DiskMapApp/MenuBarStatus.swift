import AppKit
import DiskMapCore
import SwiftUI

// TASK-064: a menu bar glance — free space, change since the last scan, and
// a one-click rescan. Strictly passive: it scans only when clicked, never
// notifies, and its only periodic work is one statfs(2) for the label every
// few minutes. Shown while DiskMap runs; hide it from the DiskMap menu.

/// What the menu bar compares against, kept across launches.
struct LastScanRecord: Codable, Equatable {
    var rootPath: String
    var scannedAt: Date
    var freeBytes: UInt64
    var scannedBytes: Int64

    static let key = "LastScanRecord"

    static func load(from defaults: UserDefaults = .standard) -> LastScanRecord? {
        defaults.data(forKey: key).flatMap { try? JSONDecoder().decode(LastScanRecord.self, from: $0) }
    }

    func save(to defaults: UserDefaults = .standard) {
        if let data = try? JSONEncoder().encode(self) { defaults.set(data, forKey: Self.key) }
    }
}

enum MenuBarText {
    /// Changes under 50 MB are noise (caches, logs), not news.
    static let noise: Int64 = 50_000_000

    static func delta(previousFree: UInt64, currentFree: UInt64) -> String {
        let change = Int64(currentFree) - Int64(previousFree)
        if abs(change) < noise { return "Free space is about the same as at your last scan." }
        let amount = ByteFormat.string(abs(change))
        return change < 0 ? "\(amount) less free than at your last scan." : "\(amount) more free than at your last scan."
    }

    /// "Free space −12 GB this week · Library +8 GB" (TASK-079).
    static func week(_ comparison: StorageHistory.Comparison) -> String? {
        var parts: [String] = []
        let period = comparison.isWeek ? "this week" : "since \(comparison.since.formatted(date: .abbreviated, time: .omitted))"
        if abs(comparison.freeDelta) >= noise {
            parts.append("Free space \(comparison.freeDelta < 0 ? "−" : "+")\(ByteFormat.string(abs(comparison.freeDelta))) \(period)")
        }
        if let top = comparison.growers.first {
            parts.append("\(top.path) +\(ByteFormat.string(top.delta))")
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// Low enough to say so in the menu bar itself.
    static func isLow(_ volume: VolumeStats) -> Bool {
        volume.totalBytes > 0 && Double(volume.freeBytes) / Double(volume.totalBytes) < 0.10
    }
}

/// Free space for the menu bar label, re-read every five minutes — one
/// statfs(2), the only periodic work. It publishes only when the value
/// changes. (A `TimelineView` in the label sent SwiftUI's menu bar
/// controller into an endless update loop at launch; found by sampling.)
@MainActor
final class MenuBarVolume: ObservableObject {
    static let shared = MenuBarVolume()
    @Published private(set) var volume: VolumeStats? = VolumeStats.forPath("/")
    private var timer: Timer?

    private init() {
        timer = Timer.scheduledTimer(withTimeInterval: 300, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }

    func refresh() {
        let current = VolumeStats.forPath("/")
        if current != volume { volume = current }
    }
}

struct MenuBarLabel: View {
    @ObservedObject private var state = MenuBarVolume.shared

    var body: some View {
        if let volume = state.volume, MenuBarText.isLow(volume) {
            Label(ByteFormat.string(Int64(volume.freeBytes)) + " free", systemImage: "externaldrive.badge.exclamationmark")
        } else {
            Image(systemName: "internaldrive")
                .accessibilityLabel("freedisk.space")
        }
    }
}

struct MenuBarStatusView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.openWindow) private var openWindow
    @AppStorage("ShowMenuBarExtra") private var showMenuBarExtra = true
    @AppStorage(DustyPreference.key) private var showDusty = true
    // Read up front so the first layout already has its real height.
    @State private var volume: VolumeStats? = VolumeStats.forPath("/")
    @State private var record: LastScanRecord? = LastScanRecord.load()
    @State private var week: StorageHistory.Comparison?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let volume {
                let low = MenuBarText.isLow(volume)
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .center, spacing: 6) {
                        Text(volume.volumeName).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                        if low { BoardTag(text: "Low on space", tone: .ink) }
                        Spacer(minLength: 0)
                        if showDusty {
                            Dusty(pose: low ? .smallDusty : .smallHello, width: 26)
                                .help(low ? "Dusty’s gathering dust: space is low" : "Plenty of room")
                        }
                    }
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(ByteFormat.hero(Int64(volume.freeBytes))).font(DiskMapType.hero(34)).foregroundStyle(DiskMapTheme.ink)
                        Text("free of \(ByteFormat.hero(Int64(volume.totalBytes)))").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    }
                    GeometryReader { geo in
                        HStack(spacing: 0) {
                            Rectangle().fill(DiskMapTheme.ink).frame(width: geo.size.width * min(1, max(0, volume.usedFraction)))
                            FreeStripes()
                        }
                    }
                    .frame(height: 8)
                    .clipShape(RoundedRectangle(cornerRadius: 2))
                    .accessibilityLabel("\(Int((volume.usedFraction * 100).rounded())) percent used")
                }
            }
            let safe = model.safeReclaimableBytes
            if safe > 0 {
                Button { open(.cleanSafe) } label: {
                    HStack(spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("You can free").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                            Text(ByteFormat.hero(safe)).font(.system(size: DiskMapType.scaled(18), weight: .semibold, design: .monospaced))
                                .foregroundStyle(DiskMapTheme.accent)
                        }
                        Spacer()
                        Text("Clean up →").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
                    }
                    .padding(12)
                    .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.accentWash))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.accent.opacity(0.35), lineWidth: 1))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Opens Clean up. Nothing is removed from here.")
            }
            if let record {
                VStack(alignment: .leading, spacing: 4) {
                    // One change line: the week when there is history, else since the last scan.
                    if let week, let line = MenuBarText.week(week) {
                        Text(line).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink).fixedSize(horizontal: false, vertical: true)
                    } else if let volume {
                        Text(MenuBarText.delta(previousFree: record.freeBytes, currentFree: volume.freeBytes))
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink).fixedSize(horizontal: false, vertical: true)
                    }
                    Text("Last scan \(CanonicalPath.displayPath(absolutePath: record.rootPath)) · \(record.scannedAt.formatted(.relative(presentation: .named)))")
                        .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3).lineLimit(2)
                }
            } else {
                Text("No scan yet. Open freedisk.space to see what fills your Mac.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
            }
            if model.cachedDeveloper.summary.staleProjectCount > 0 {
                Button { open(.developerStorage) } label: {
                    Text("\(countLabel(model.cachedDeveloper.summary.staleProjectCount, "project")) untouched for 6+ months hold \(ByteFormat.hero(model.cachedDeveloper.summary.staleReclaimableBytes)) →")
                        .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                        .fixedSize(horizontal: false, vertical: true)
                        .multilineTextAlignment(.leading)
                }
                .buttonStyle(.plain)
            }
            Rectangle().fill(DiskMapTheme.line).frame(height: 1)
            HStack(spacing: 8) {
                Button(model.isScanning ? "Scanning…" : "Rescan") { rescan() }
                    .buttonStyle(SecondaryButtonStyle())
                    .fixedSize()
                    .disabled(model.isScanning || (model.rootURL ?? record.map { URL(fileURLWithPath: $0.rootPath) }) == nil)
                Button("Open freedisk.space") { openApp() }
                    .buttonStyle(AccentButtonStyle(fullWidth: true))
            }
        }
        .padding(18)
        .frame(width: 320)
        .fixedSize(horizontal: false, vertical: true)   // the panel is as tall as its content
        .background(DiskMapTheme.page)
        .contextMenu {
            Button("Hide from Menu Bar") { showMenuBarExtra = false }
        }
        .onAppear(perform: refresh)
        .onChange(of: model.isScanning) { _, _ in refresh() }
    }

    private func open(_ destination: AppDestination) {
        model.destination = destination
        openApp()
    }

    private func refresh() {
        volume = VolumeStats.forPath("/")
        record = LastScanRecord.load()
        week = model.weekComparison ?? record.flatMap { record in
            // Before any scan this launch: the history file says it.
            let entries = ScanModel.appHistory().entries(for: record.rootPath)
            return entries.last.flatMap { StorageHistory.compare(entries, latest: $0) }
        }
        MenuBarVolume.shared.refresh()
    }

    /// The same quick rescan as the toolbar; it runs only because it was clicked.
    private func rescan() {
        guard let root = model.rootURL ?? record.map({ URL(fileURLWithPath: $0.rootPath, isDirectory: true) }) else { return }
        Task { await model.scan(root) }
    }

    private func openApp() {
        NSApp.activate(ignoringOtherApps: true)
        if let window = NSApp.windows.first(where: { $0.canBecomeMain && $0.isVisible }) {
            window.makeKeyAndOrderFront(nil)
        } else {
            openWindow(id: "main")
        }
    }
}

/// DiskMap ▸ Show in Menu Bar.
struct MenuBarCommands: Commands {
    @AppStorage("ShowMenuBarExtra") private var showMenuBarExtra = true

    var body: some Commands {
        CommandGroup(after: .appSettings) {
            Toggle("Show in Menu Bar", isOn: $showMenuBarExtra)
        }
    }
}
