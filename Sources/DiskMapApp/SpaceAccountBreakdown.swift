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
    let account: SpaceAccount
    var onDone: () -> Void

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
            .frame(maxHeight: 460)
            HStack {
                Spacer()
                Button("Done", action: onDone)
                    .buttonStyle(AccentButtonStyle(large: true))
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("space-account-done")
            }
        }
        .padding(24)
        .frame(width: 520)
        .background(DiskMapTheme.raised)
    }
}
