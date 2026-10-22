import AppKit
import DiskMapCore
import SwiftUI

/// One row that shows a name and a figure, and folds its explanation away
/// until clicked — the sheet stays a list of numbers, not a page of text.
struct FoldingRow<Leading: View, Trailing: View, Detail: View>: View {
    var title: String
    var startsOpen = false
    @ViewBuilder var leading: Leading
    @ViewBuilder var trailing: Trailing
    @ViewBuilder var detail: Detail
    @State private var open: Bool?

    var body: some View {
        let isOpen = open ?? startsOpen
        VStack(alignment: .leading, spacing: 6) {
            Button {
                withAnimation(.easeOut(duration: 0.15)) { open = !isOpen }
            } label: {
                HStack(spacing: 10) {
                    leading
                    Text(title).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                    Spacer(minLength: DiskMapSpace.sm)
                    trailing
                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(DiskMapTheme.ink3)
                        .rotationEffect(.degrees(isOpen ? 90 : 0))
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityHint(isOpen ? "Hides the explanation" : "Shows what this is")
            if isOpen {
                VStack(alignment: .leading, spacing: 6) { detail }
                    .padding(.leading, 20)
                    .transition(.opacity)
            }
        }
    }
}

/// "Where's the rest?": the disk's used space as parts that add up — one
/// stacked bar, then a row per part. Shown from Overview's coverage line.
struct SpaceAccountBreakdown: View {
    let account: SpaceAccount

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            stackedBar
            VStack(alignment: .leading, spacing: 10) {
                ForEach(Array(account.rows.enumerated()), id: \.element.id) { index, row in
                    FoldingRow(title: row.title) {
                        Circle().fill(Self.color(row, index)).frame(width: 8, height: 8)
                    } trailing: {
                        Text(ByteFormat.string(row.bytes))
                            .font(DiskMapType.figureStrong)
                            .foregroundStyle(DiskMapTheme.ink)
                    } detail: {
                        Text(row.detail)
                            .font(DiskMapType.secondary)
                            .foregroundStyle(DiskMapTheme.ink2)
                            .fixedSize(horizontal: false, vertical: true)
                        if row.confidence == .remainder {
                            Text("Worked out from the other figures, not measured directly.")
                                .font(DiskMapType.secondary)
                                .foregroundStyle(DiskMapTheme.ink3)
                        }
                    }
                    .accessibilityIdentifier("space-account-row")
                }
            }
            if account.purgeableBytes > 0 {
                HStack(spacing: 6) {
                    Text("Purgeable, part of the above")
                    Spacer()
                    Text(ByteFormat.string(account.purgeableBytes)).font(DiskMapType.figure)
                }
                .font(DiskMapType.secondary)
                .foregroundStyle(DiskMapTheme.ink3)
                .help("Space macOS can free by itself when it needs to. It overlaps the parts above, so it isn't added.")
                .accessibilityIdentifier("space-account-purgeable")
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .accessibilityIdentifier("space-account")
    }

    /// Every part as one bar, in row order, so the split reads at a glance.
    private var stackedBar: some View {
        GeometryReader { geo in
            HStack(spacing: 2) {
                ForEach(Array(account.rows.enumerated()), id: \.element.id) { index, row in
                    let fraction = account.usedBytes > 0 ? Double(row.bytes) / Double(account.usedBytes) : 0
                    if fraction > 0 {
                        Rectangle()
                            .fill(Self.color(row, index))
                            .frame(width: max(3, (geo.size.width - CGFloat(account.rows.count * 2)) * fraction))
                    }
                }
            }
            .frame(width: geo.size.width, alignment: .leading)
        }
        .frame(height: 10)
        .clipShape(RoundedRectangle(cornerRadius: 3, style: .continuous))
        .accessibilityHidden(true)
    }

    /// The scan in the accent; macOS's own parts in the data palette; what's
    /// left over in a quiet grey.
    static func color(_ row: SpaceAccount.Row, _ index: Int) -> Color {
        switch row.part {
        case .scanned: return DiskMapTheme.accent
        case .notSeen: return DiskMapTheme.ink3.opacity(0.45)
        case .recoveryAndMetadata: return DiskMapTheme.ink3
        // Skips the palette's lilac (index 1), too close to the accent.
        case .volume: return DiskMapTheme.data([0, 2, 3, 5, 4, 6][index % 6])
        }
    }
}

/// The sheet Overview opens from "Where's the rest?".
struct SpaceAccountSheet: View {
    @ObservedObject var model: ScanModel
    let account: SpaceAccount
    var onDone: () -> Void
    /// Starts true only for the snapshot harness's confirm render.
    @State var confirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top, spacing: 14) {
                IconTile(systemName: "chart.pie", size: 40)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Where's the rest?")
                        .font(.system(size: DiskMapType.scaled(20), weight: .semibold))
                        .foregroundStyle(DiskMapTheme.ink)
                    Text("\(ByteFormat.string(account.usedBytes)) in use, split into parts that add up")
                        .font(DiskMapType.body)
                        .foregroundStyle(DiskMapTheme.ink2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: DiskMapSpace.lg) {
                        SpaceAccountBreakdown(account: account)
                        if let items = model.systemStorage, !items.isEmpty {
                            SystemStorageList(items: items, model: model).id("system-storage")
                        }
                    }
                }
                .onAppear {
                    // Snapshot harness: `--rest-state system` shows this part.
                    if SnapshotHarness.restState == "system" { proxy.scrollTo("system-storage", anchor: .top) }
                }
            }
            .frame(maxHeight: account.timeMachineSnapshotNames.isEmpty && model.snapshotDeletion == .idle ? 520 : 380)
            snapshotSection
            HStack {
                Spacer()
                Button("Done", action: onDone)
                    .buttonStyle(AccentButtonStyle(large: true))
                    .keyboardShortcut(confirming ? nil : .defaultAction)
                    .disabled(model.snapshotDeletion == .running)
                    .accessibilityIdentifier("space-account-done")
            }
        }
        .padding(24)
        .frame(width: 520)
        .background(DiskMapTheme.raised)
        .interactiveDismissDisabled(model.snapshotDeletion == .running)
    }

    // MARK: Time Machine snapshots (decision D1)

    @ViewBuilder
    private var snapshotSection: some View {
        let names = account.timeMachineSnapshotNames
        if !names.isEmpty || model.snapshotDeletion != .idle {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 10) {
                    Image(systemName: "clock.arrow.circlepath").foregroundStyle(DiskMapTheme.ink2)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(names.isEmpty ? "Time Machine snapshots" : Self.snapshotTitle(names))
                            .font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                        if let newest = Self.newest(names) {
                            Text("Newest \(newest.formatted(date: .abbreviated, time: .shortened)) · size unknown")
                                .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                        }
                    }
                    .help(Self.snapshotSummary(names))
                    Spacer()
                    if model.snapshotDeletion == .running {
                        ProgressView().controlSize(.small).accessibilityIdentifier("snapshot-deleting")
                    } else if !names.isEmpty && !confirming {
                        Button("Delete…") { confirming = true }
                            .buttonStyle(SecondaryButtonStyle())
                            .accessibilityIdentifier("snapshot-delete")
                    }
                }
                if case .finished(let deleted, let alreadyGone, let failed) = model.snapshotDeletion {
                    Text(Self.resultText(deleted: deleted, alreadyGone: alreadyGone, failed: failed))
                        .font(DiskMapType.secondary)
                        .foregroundStyle(failed.isEmpty ? DiskMapTheme.ink2 : DiskMapTheme.review)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("snapshot-result")
                }
                if confirming && !names.isEmpty && model.snapshotDeletion != .running {
                    confirmBox(count: names.count)
                }
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        }
    }

    static func snapshotTitle(_ names: [String]) -> String {
        names.count == 1 ? "1 Time Machine snapshot" : "\(names.count) Time Machine snapshots"
    }

    static func newest(_ names: [String]) -> Date? {
        names.compactMap { name -> Date? in
            if case .timeMachine(let date) = LocalSnapshots.kind(of: name) { return date }
            return nil
        }.max()
    }

    private func confirmBox(count: Int) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(count == 1 ? "Delete this snapshot?" : "Delete \(count) snapshots?")
                .font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
            Text("Deleted right away, not to the Trash. Your Time Machine backups aren't touched.")
                .font(DiskMapType.secondary)
                .foregroundStyle(DiskMapTheme.ink2)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                Spacer()
                Button("Cancel") { confirming = false }
                    .buttonStyle(SecondaryButtonStyle())
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("snapshot-cancel")
                Button(count == 1 ? "Delete Snapshot" : "Delete \(count) Snapshots") {
                    confirming = false
                    model.deleteTimeMachineSnapshots()
                }
                .buttonStyle(SecondaryButtonStyle())
                .foregroundStyle(DiskMapTheme.review)
                .accessibilityIdentifier("snapshot-confirm")
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.raised))
        .accessibilityIdentifier("snapshot-confirm-box")
    }

    static func snapshotSummary(_ names: [String]) -> String {
        let dates = names.compactMap { name -> Date? in
            if case .timeMachine(let date) = LocalSnapshots.kind(of: name) { return date }
            return nil
        }.sorted()
        let count = names.count == 1 ? "1 local snapshot" : "\(names.count) local snapshots"
        var text = "\(count): copies Time Machine keeps on this Mac between backups. Their size can't be measured; it's part of what's left over above. macOS frees them when space runs low."
        if let newest = dates.last {
            text += " Newest: \(newest.formatted(date: .abbreviated, time: .shortened))."
        }
        return text
    }

    static func resultText(deleted: Int, alreadyGone: Int = 0, failed: [String]) -> String {
        var parts: [String] = []
        if deleted > 0 { parts.append(deleted == 1 ? "Deleted 1 snapshot." : "Deleted \(deleted) snapshots.") }
        if alreadyGone > 0 {
            parts.append(alreadyGone == 1 ? "1 had already been removed by macOS." : "\(alreadyGone) had already been removed by macOS.")
        }
        if let first = failed.first {
            parts.append(failed.count == 1 ? "1 couldn't be deleted: \(first)" : "\(failed.count) couldn't be deleted: \(first)")
        }
        if parts.isEmpty { parts.append("Nothing was deleted.") }
        return parts.joined(separator: " ")
    }
}

/// "What macOS keeps" (P2): storage macOS fills by itself, with how to free
/// it through macOS's own settings. Only installer apps can go to Cleanup.
struct SystemStorageList: View {
    let items: [SystemStorageCatalog.Item]
    @ObservedObject var model: ScanModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            MonoLabel("What macOS keeps")
            VStack(alignment: .leading, spacing: 10) {
                ForEach(items) { item in row(item) }
            }
            .padding(16)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        }
        .accessibilityIdentifier("system-storage")
    }

    private func row(_ item: SystemStorageCatalog.Item) -> some View {
        FoldingRow(title: item.entry.title, startsOpen: SnapshotHarness.restState == "system-open" && item.entry.action == .guide && item.entry.settingsPane != nil) {
            Image(systemName: Self.symbol(item.entry.action))
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(item.entry.action == .trash ? DiskMapTheme.accent : DiskMapTheme.ink3)
                .frame(width: 12)
                .help(Self.actionHelp(item.entry.action))
        } trailing: {
            Text(ByteFormat.string(item.bytes) + (item.isPartial ? "+" : ""))
                .font(DiskMapType.figureStrong).foregroundStyle(DiskMapTheme.ink)
                .help(item.isPartial ? "At least this much: some folders inside need Full Disk Access." : "")
        } detail: {
            Text(item.entry.why)
                .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                .fixedSize(horizontal: false, vertical: true)
            ForEach(item.entry.steps, id: \.self) { step in
                Text(step).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 14) {
                if let pane = item.entry.settingsPane, let url = Self.settingsURL(pane) {
                    Button("Open Settings") { NSWorkspace.shared.open(url) }
                        .buttonStyle(LinkButtonStyle())
                        .font(DiskMapType.secondary)
                        .accessibilityIdentifier("system-storage-settings-\(item.id)")
                }
                if item.entry.action == .trash {
                    ForEach(item.found, id: \.path) { found in
                        Button(item.found.count == 1 ? "Add to Cleanup"
                               : "Add \(URL(fileURLWithPath: found.path).deletingPathExtension().lastPathComponent) to Cleanup") {
                            model.stageRow(path: found.path, size: found.bytes, reason: item.entry.title)
                        }
                        .buttonStyle(LinkButtonStyle())
                        .font(DiskMapType.secondary)
                        .accessibilityIdentifier("system-storage-stage")
                    }
                }
            }
        }
        .accessibilityIdentifier("system-storage-row-\(item.id)")
    }

    /// Lock: macOS manages it. Gear: freed in Settings. Trash: can go to Cleanup.
    static func symbol(_ action: SystemStorageCatalog.Action) -> String {
        switch action {
        case .never: return "lock.fill"
        case .guide: return "gearshape.fill"
        case .trash: return "trash.fill"
        }
    }

    static func actionHelp(_ action: SystemStorageCatalog.Action) -> String {
        switch action {
        case .never: return "macOS manages this; nothing to do."
        case .guide: return "Freed through macOS's own settings."
        case .trash: return "Can go to Cleanup (Trash first)."
        }
    }

    static func settingsURL(_ pane: String) -> URL? {
        URL(string: "x-apple.systempreferences:" + pane)
    }
}
