import SwiftUI

// The Explore list pages (redesign board 05): chips, a dropdown, the token
// search field and a two-line row with a "can be freed" note and an add button.

/// Column widths for the list tables, by window width.
struct ListColumns {
    let kind: CGFloat
    let bar: CGFloat
    let size: CGFloat = 84
    let changed: CGFloat
    let showFree: Bool
    let free: CGFloat
    let freeMax: CGFloat
    let add: CGFloat
    let rowHeight: CGFloat

    init(_ width: WidthClass) {
        switch width {
        case .compact:
            kind = 110; bar = 70; changed = 118; showFree = false; free = 0; freeMax = 0; add = 40; rowHeight = 46
        case .regular:
            kind = 130; bar = 100; changed = 130; showFree = true; free = 200; freeMax = 280; add = 136; rowHeight = 50
        case .wide:
            kind = 150; bar = 120; changed = 140; showFree = true; free = 260; freeMax = 380; add = 140; rowHeight = 54
        }
    }
}

/// A filter chip: "Video 214". On shows an ink border.
struct ListChip: View {
    var title: String
    var count: Int?
    var isOn: Bool
    var removable = false
    var action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Text(title).font(.system(size: DiskMapType.scaled(12.5), weight: isOn ? .medium : .regular))
                if let count {
                    Text(count.formatted()).font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced))
                        .foregroundStyle(isOn ? DiskMapTheme.ink : DiskMapTheme.ink3)
                }
                if removable { Image(systemName: "xmark").font(.system(size: 9, weight: .semibold)) }
            }
            .lineLimit(1)
            .foregroundStyle(isOn ? DiskMapTheme.ink : DiskMapTheme.ink2)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous).stroke(isOn ? DiskMapTheme.ink : DiskMapTheme.line, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}

/// "Not changed in  Any time ▾".
struct ListDropdown<Option: Hashable & Identifiable>: View {
    var label: String
    var options: [Option]
    @Binding var selection: Option
    var title: KeyPath<Option, String>
    var body: some View {
        Menu {
            ForEach(options) { option in
                Button { selection = option } label: {
                    if option == selection { Label(option[keyPath: title], systemImage: "checkmark") } else { Text(option[keyPath: title]) }
                }
            }
        } label: {
            HStack(spacing: 8) {
                Text(label).foregroundStyle(DiskMapTheme.ink3)
                Text(selection[keyPath: title]).fontWeight(.medium).foregroundStyle(DiskMapTheme.ink)
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold)).foregroundStyle(DiskMapTheme.ink3)
            }
            .font(.system(size: DiskMapType.scaled(12.5)))
            .lineLimit(1)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .accessibilityLabel("\(label) \(selection[keyPath: title])")
    }
}

/// The token search: an ink box with the boards' hard shadow, what it
/// understood as violet tags, and how many rows match.
struct ListSearchField: View {
    @Binding var text: String
    var tokens: [String]
    var matchCount: Int?
    var placeholder: String
    var focused: FocusState<Bool>.Binding
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").font(.system(size: 12, weight: .medium)).foregroundStyle(DiskMapTheme.ink2)
            // Plain fields on macOS ignore a prompt's colour, so the placeholder is drawn here.
            TextField("", text: $text)
                .textFieldStyle(.plain)
                .font(.system(size: DiskMapType.scaled(13), weight: .medium, design: .monospaced))
                .focused(focused)
                .accessibilityLabel("Search the list")
                .accessibilityHint(placeholder)
                .background(alignment: .leading) {
                    if text.isEmpty {
                        Text(placeholder)
                            .font(.system(size: DiskMapType.scaled(13), design: .monospaced))
                            .foregroundStyle(DiskMapTheme.ink3)
                            .lineLimit(1)
                            .allowsHitTesting(false)
                            .accessibilityHidden(true)
                    }
                }
            ForEach(tokens.prefix(3), id: \.self) { BoardTag(text: $0, tone: .accent) }
            if let matchCount {
                Text("\(matchCount.formatted()) match")
                    .font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced))
                    .textCase(.uppercase)
                    .foregroundStyle(DiskMapTheme.ink3)
                    .fixedSize()
            }
            if !text.isEmpty {
                Button { text = "" } label: { Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)) }
                    .buttonStyle(.plain)
                    .foregroundStyle(DiskMapTheme.ink3)
                    .accessibilityLabel("Clear search")
            }
        }
        .padding(.leading, 10)
        .padding(.trailing, 10)
        .frame(height: 34)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.card)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.black).offset(x: 2, y: 2))
        )
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.ink, lineWidth: 1))
    }
}

/// One row of a list page: type tile, name over place, kind, bar and size,
/// last changed, can be freed, add.
struct ListFileRow: View {
    var name: String
    var place: String
    var badge: String
    var kind: String
    var fraction: Double
    var bytes: Int64
    var changed: String
    var note: BiggestFilesView.FreeNote
    var accent: Bool
    var staged: Bool
    var selected: Bool
    var columns: ListColumns
    var select: () -> Void
    /// Nil when it can't go in the basket (protected): no add button.
    var add: (() -> Void)?
    /// Folders open on double-click.
    var open: (() -> Void)? = nil

    var body: some View {
        HStack(spacing: 0) {
            HStack(spacing: 10) {
                Text(badge)
                    .font(.system(size: 8.5, weight: .medium, design: .monospaced))
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                    .foregroundStyle(accent ? DiskMapTheme.accentDeep : DiskMapTheme.ink2)
                    .frame(width: 28, height: 28)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(accent ? DiskMapTheme.accentWash : DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .stroke(accent ? DiskMapTheme.accent.opacity(0.45) : DiskMapTheme.line, lineWidth: 1))
                VStack(alignment: .leading, spacing: 1) {
                    Text(name).font(.system(size: DiskMapType.scaled(13), weight: .medium)).foregroundStyle(DiskMapTheme.ink)
                    Text(place).font(.system(size: DiskMapType.scaled(11), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                }
                .lineLimit(1)
                .truncationMode(.middle)
            }
            .padding(.leading, 12)
            .padding(.trailing, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(kind).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                .frame(width: columns.kind, alignment: .leading)
            HStack(spacing: 10) {
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Rectangle().fill(DiskMapTheme.soft)
                        Rectangle().fill(accent ? DiskMapTheme.accent : DiskMapTheme.ink)
                            .frame(width: max(2, geo.size.width * min(1, fraction)))
                    }
                }
                .frame(height: 4)
                .frame(width: columns.bar)
                Text(ByteFormat.string(bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                    .lineLimit(1).frame(width: columns.size - 10, alignment: .trailing)
            }
            .frame(width: columns.bar + columns.size, alignment: .trailing)
            Text(changed).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                .frame(width: columns.changed, alignment: .leading).padding(.leading, 24)
            if columns.showFree {
                noteView.frame(minWidth: columns.free, maxWidth: columns.freeMax, alignment: .leading)
            }
            addView.frame(width: columns.add, alignment: .trailing).padding(.trailing, 8)
        }
        .frame(height: columns.rowHeight)
        .background(selected ? DiskMapTheme.accentWash : Color.clear)
        .overlay(alignment: .leading) { if selected { Rectangle().fill(DiskMapTheme.accent).frame(width: 2) } }
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture(perform: select)
        .simultaneousGesture(TapGesture(count: 2).onEnded { open?() })
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
        .accessibilityAction(named: "Add to basket") { add?() }
        .accessibilityAction(named: "Open") { open?() }
    }

    /// Notes are written with `code` marks for the inspector; the row shows them plain.
    static func plain(_ text: String) -> String { text.replacingOccurrences(of: "`", with: "") }

    @ViewBuilder private var noteView: some View {
        switch note {
        case .none:
            Text("—").foregroundStyle(DiskMapTheme.ink3)
        case .accent(let text):
            Text(text).font(DiskMapType.body).foregroundStyle(DiskMapTheme.accentDeep).lineLimit(1)
        case .review(let instead):
            HStack(spacing: 8) {
                BoardTag(text: "Review")
                if let instead { Text(ListFileRow.plain(instead)).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1).truncationMode(.tail) }
            }
            .help(instead.map { "Instead: " + ListFileRow.plain($0) } ?? "Look before you remove this")
        }
    }

    @ViewBuilder private var addView: some View {
        if add == nil {
            BoardTag(text: "Kept").help("macOS or your account needs this, so it can't go in the basket")
        } else if staged {
            HStack(spacing: 6) {
                if columns.showFree { Text("In basket").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep) }
                Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundStyle(DiskMapTheme.accent)
                    .frame(width: 26, height: 26)
            }
            .accessibilityLabel("In the basket")
        } else {
            Button { add?() } label: {
                HStack(spacing: 8) {
                    if selected && columns.showFree { Text("Add to basket").font(DiskMapType.body).foregroundStyle(DiskMapTheme.accentDeep) }
                    Image(systemName: "plus")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(selected ? DiskMapTheme.onAccent : DiskMapTheme.ink3)
                        .frame(width: 26, height: 26)
                        .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(selected ? DiskMapTheme.accent : Color.clear))
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Add to basket")
            .accessibilityLabel("Add \(name) to basket")
        }
    }
}
