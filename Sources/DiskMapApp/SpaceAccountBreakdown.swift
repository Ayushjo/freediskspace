import DiskMapCore
import SwiftUI

/// "Where's the rest?": the disk's used space as parts that add up, each
/// with a bar and how sure we are. Shown from Overview's coverage line.
struct SpaceAccountBreakdown: View {
    let account: SpaceAccount

    var body: some View {
        VStack(alignment: .leading, spacing: DiskMapSpace.md) {
            VStack(alignment: .leading, spacing: 14) {
                ForEach(account.rows) { row in
                    rowView(row)
                }
            }
            .padding(16)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            if account.purgeableBytes > 0 {
                Text("macOS also counts about \(ByteFormat.string(account.purgeableBytes)) as purgeable: space it can free by itself when it needs to. That overlaps the parts above, so it isn't added.")
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.ink2)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("space-account-purgeable")
            }
        }
        .accessibilityIdentifier("space-account")
    }

    private func rowView(_ row: SpaceAccount.Row) -> some View {
        let fraction = account.usedBytes > 0 ? min(1, Double(row.bytes) / Double(account.usedBytes)) : 0
        return VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline, spacing: DiskMapSpace.xs) {
                Text(row.title)
                    .font(DiskMapType.bodyEmphasis)
                    .foregroundStyle(DiskMapTheme.ink)
                if row.confidence == .remainder {
                    Text("WHAT'S LEFT")
                        .font(DiskMapType.label)
                        .foregroundStyle(DiskMapTheme.ink3)
                        .help("Worked out from the other figures, not measured directly.")
                }
                Spacer(minLength: DiskMapSpace.sm)
                Text(ByteFormat.string(row.bytes))
                    .font(DiskMapType.figureStrong)
                    .foregroundStyle(DiskMapTheme.ink)
            }
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(DiskMapTheme.line)
                    Capsule()
                        .fill(row.part == .scanned ? DiskMapTheme.accent : DiskMapTheme.ink3)
                        .frame(width: max(fraction > 0 ? 3 : 0, geo.size.width * fraction))
                }
            }
            .frame(height: 5)
            .accessibilityHidden(true)
            Text(row.detail)
                .font(DiskMapType.secondary)
                .foregroundStyle(DiskMapTheme.ink2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("space-account-row")
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
                    Text("\(ByteFormat.string(account.usedBytes)) of this disk is in use. These parts add up to all of it.")
                        .font(DiskMapType.body)
                        .foregroundStyle(DiskMapTheme.ink2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            ScrollView {
                SpaceAccountBreakdown(account: account)
            }
            .frame(maxHeight: account.timeMachineSnapshotNames.isEmpty && model.snapshotDeletion == .idle ? 460 : 300)
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
                Text("Time Machine snapshots").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                if !names.isEmpty {
                    Text(Self.snapshotSummary(names))
                        .font(DiskMapType.secondary)
                        .foregroundStyle(DiskMapTheme.ink2)
                        .fixedSize(horizontal: false, vertical: true)
                }
                switch model.snapshotDeletion {
                case .running:
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("Deleting snapshots…").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                    }
                    .accessibilityIdentifier("snapshot-deleting")
                case .finished(let deleted, let alreadyGone, let failed):
                    Text(Self.resultText(deleted: deleted, alreadyGone: alreadyGone, failed: failed))
                        .font(DiskMapType.secondary)
                        .foregroundStyle(failed.isEmpty ? DiskMapTheme.ink2 : DiskMapTheme.review)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("snapshot-result")
                case .idle:
                    EmptyView()
                }
                if !names.isEmpty && model.snapshotDeletion != .running {
                    if confirming { confirmBox(count: names.count) } else {
                        Button(names.count == 1 ? "Delete this snapshot…" : "Delete \(names.count) snapshots…") {
                            confirming = true
                        }
                        .buttonStyle(SecondaryButtonStyle())
                        .accessibilityIdentifier("snapshot-delete")
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        }
    }

    private func confirmBox(count: Int) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(count == 1 ? "Delete this snapshot?" : "Delete \(count) snapshots?")
                .font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
            Text("They're deleted right away, not moved to the Trash, and can't be put back. Your backups on the Time Machine disk aren't touched, and Time Machine makes new snapshots as it backs up.")
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
