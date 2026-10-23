import Darwin
import DiskMapCore
import SwiftUI

/// History & Put Back (redesign board 10): every cleanup freedisk.space made,
/// what is still in the Trash, and Put Back for each item or a whole cleanup.
struct HistoryView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.widthClass) private var widthClass

    @State private var expanded: Set<Int> = []
    @State private var didSetDefaults = false
    @State private var query = ""
    /// Which Trash copies still exist, checked when the page opens or history changes.
    @State private var inTrash: Set<String> = []

    private var history: CleanupHistory { model.cleanupHistory }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: widthClass.gap) {
                header
                if history.entries.isEmpty {
                    BoardCard(padding: 24) {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Nothing moved yet").font(DiskMapType.heading).foregroundStyle(DiskMapTheme.ink)
                            Text("When you clean up, each cleanup appears here so you can put things back while they're in the Trash.")
                                .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                } else {
                    stats
                    timeline
                }
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.top, widthClass == .compact ? 14 : 20)
            .padding(.bottom, widthClass.pagePadding)
        }
        .background(DiskMapTheme.page)
        .task(id: history) { await refreshTrash() }
    }

    private func exists(_ path: String) -> Bool { inTrash.contains(path) }

    private func refreshTrash() async {
        let paths = history.entries.flatMap { $0.record.items.map(\.trashedPath) }
        let found = await Task.detached(priority: .userInitiated) { () -> Set<String> in
            Set(paths.filter { path in
                var info = Darwin.stat()
                return lstat(path, &info) == 0
            })
        }.value
        inTrash = found
        if !didSetDefaults {
            didSetDefaults = true
            // Open the cleanups you can still put back; the first one always.
            expanded = Set(history.entries.enumerated().filter { index, entry in
                index == 0 || entry.record.items.contains { CleanupHistory.state(of: $0, in: entry, existsInTrash: exists) == .inTrash }
            }.map { $0.element.number })
        }
    }

    // MARK: Header and stats

    private var header: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                (Text("Freed \(ByteFormat.hero(history.freedBytes)) with freedisk.space").foregroundColor(DiskMapTheme.ink)
                 + Text(widthClass == .compact ? "" : " · 0 files deleted permanently").foregroundColor(DiskMapTheme.ink3))
                    .font(DiskMapType.pageTitle)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
                Text("Every cleanup goes to the Trash first. While it's there, Put Back returns it to the same place, with the same name.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(2)
            }
            Spacer(minLength: 8)
            if !history.entries.isEmpty {
                DiskMapSearchField(placeholder: "Find a file you removed", text: $query)
                    .frame(maxWidth: widthClass == .compact ? 200 : 260)
            }
        }
    }

    private var stats: some View {
        let trashBytes = history.bytesInTrash(existsInTrash: exists)
        let first = history.entries.last?.record.date
        return HStack(spacing: 0) {
            stat(label: "Freed so far", value: ByteFormat.hero(history.freedBytes), unit: "in \(history.entries.count) cleanup\(history.entries.count == 1 ? "" : "s")",
                 detail: first.map { "Since you started on \(Self.dayText($0))." } ?? "", accent: false)
            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
            // Only moves to the Trash: rule 1 of the project, so this is always 0 on the Mac.
            stat(label: "Deleted permanently", value: "0", unit: "files", detail: "Nothing skipped the Trash.", accent: false)
            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
            stat(label: "Still in Trash", value: ByteFormat.hero(trashBytes), unit: trashBytes > 0 ? "can be put back" : "",
                 detail: trashBytes > 0 ? "Empty the Trash in Finder when you're sure." : "The Trash has been emptied since.", accent: trashBytes > 0)
        }
        .fixedSize(horizontal: false, vertical: true)
        .background(BoardGrid())
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func stat(label: String, value: String, unit: String, detail: String, accent: Bool) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(label.uppercased()).font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(value).font(DiskMapType.hero(widthClass == .compact ? 32 : 44)).foregroundStyle(accent ? DiskMapTheme.accent : DiskMapTheme.ink)
                    .lineLimit(1).minimumScaleFactor(0.6)
                Text(unit).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
            }
            Text(detail).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(2)
        }
        .padding(.horizontal, widthClass == .compact ? 18 : 28)
        .padding(.vertical, 22)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: Timeline

    private var shownEntries: [CleanupHistory.Entry] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !q.isEmpty else { return history.entries }
        return history.entries.compactMap { entry in
            let hits = entry.record.items.filter { $0.originalPath.lowercased().contains(q) }
            guard !hits.isEmpty else { return nil }
            var copy = entry
            copy.record = CleanupRecord(date: entry.record.date, items: hits)
            return copy
        }
    }

    private var timeline: some View {
        let entries = shownEntries
        return VStack(spacing: 14) {
            if entries.isEmpty {
                Text("No removed file matches “\(query)”.").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 40)
            }
            ForEach(Array(entries.enumerated()), id: \.element.id) { index, entry in
                HStack(alignment: .top, spacing: 16) {
                    Circle()
                        .fill(index == 0 && query.isEmpty ? DiskMapTheme.accent : DiskMapTheme.card)
                        .overlay(Circle().stroke(DiskMapTheme.accent, lineWidth: 1.5))
                        .frame(width: 14, height: 14)
                        .padding(.top, 22)
                        .accessibilityHidden(true)
                    cleanupCard(entry, open: !query.isEmpty || expanded.contains(entry.number))
                }
            }
        }
        .background(alignment: .leading) {
            // The dashed line the markers sit on.
            Rectangle().fill(.clear).frame(width: 1)
                .overlay(Line().stroke(DiskMapTheme.ink3.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [3, 4])))
                .padding(.leading, 7).padding(.vertical, 28)
        }
    }

    private func cleanupCard(_ entry: CleanupHistory.Entry, open: Bool) -> some View {
        let items = entry.record.items.sorted { $0.bytes > $1.bytes }
        let states = items.map { CleanupHistory.state(of: $0, in: entry, existsInTrash: exists) }
        let back = items.enumerated().filter { states[$0.offset] == .inTrash }.map(\.element)
        let total = items.reduce(Int64(0)) { $0 + $1.bytes }
        return VStack(spacing: 0) {
            Button {
                if expanded.contains(entry.number) { expanded.remove(entry.number) } else { expanded.insert(entry.number) }
            } label: {
                HStack(spacing: 12) {
                    Image(systemName: open ? "chevron.down" : "chevron.right").font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(DiskMapTheme.ink3).frame(width: 12)
                    Text(Self.whenText(entry.record.date)).font(.system(size: DiskMapType.scaled(15), weight: .semibold)).foregroundStyle(DiskMapTheme.ink)
                    Text("\(items.count) item\(items.count == 1 ? "" : "s") · cleanup #\(entry.number)".uppercased())
                        .font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                        .lineLimit(1)
                    let tag = Self.tag(states)
                    BoardTag(text: tag.text, tone: tag.accent ? .accent : .neutral)
                    Spacer(minLength: 8)
                    Text(ByteFormat.hero(total)).font(.system(size: DiskMapType.scaled(14), weight: .semibold, design: .monospaced)).foregroundStyle(DiskMapTheme.ink)
                }
                .padding(.horizontal, 18)
                .frame(height: 56)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Cleanup \(entry.number), \(Self.whenText(entry.record.date)), \(items.count) items, \(ByteFormat.string(total))")
            if open {
                ForEach(Array(items.enumerated()), id: \.element.trashedPath) { index, item in
                    itemRow(item, state: states[index], number: entry.number)
                }
                if !back.isEmpty {
                    HStack {
                        Text("Each item goes back to where it was, with the same name.").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                        Spacer(minLength: 8)
                        Button {
                            Task { await model.putBack(back, fromCleanup: entry.number) }
                        } label: {
                            Label(back.count == items.count ? "Put Back all \(back.count)" : "Put Back \(back.count) in Trash", systemImage: "arrow.uturn.backward")
                        }
                        .buttonStyle(AccentButtonStyle())
                    }
                    .padding(.horizontal, 18)
                    .frame(height: 56)
                    .background(DiskMapTheme.soft.opacity(0.5))
                    .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
                }
            }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func itemRow(_ item: CleanupRecord.Item, state: CleanupHistory.ItemState, number: Int) -> some View {
        let gone = state == .trashEmptied
        return HStack(spacing: 14) {
            Text(URL(fileURLWithPath: item.originalPath).lastPathComponent)
                .font(.system(size: DiskMapType.scaled(13.5), weight: .medium))
                .foregroundStyle(gone ? DiskMapTheme.ink3 : DiskMapTheme.ink)
                .lineLimit(1)
                .frame(width: widthClass == .compact ? 150 : 260, alignment: .leading)
            Text(CanonicalPath.displayPath(absolutePath: URL(fileURLWithPath: item.originalPath).deletingLastPathComponent().path))
                .font(.system(size: DiskMapType.scaled(11.5), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                .lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(ByteFormat.hero(item.bytes)).font(DiskMapType.figure).foregroundStyle(gone ? DiskMapTheme.ink3 : DiskMapTheme.ink)
                .frame(width: 80, alignment: .trailing)
            Group {
                switch state {
                case .inTrash:
                    HStack(spacing: 12) {
                        if widthClass != .compact {
                            HStack(spacing: 6) {
                                Circle().fill(DiskMapTheme.accent).frame(width: 6, height: 6)
                                Text("IN TRASH").font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.accentDeep)
                            }
                        }
                        Button { Task { await model.putBack([item], fromCleanup: number) } } label: {
                            Label("Put Back", systemImage: "arrow.uturn.backward")
                        }
                        .buttonStyle(SecondaryButtonStyle())
                        .accessibilityLabel("Put back \(URL(fileURLWithPath: item.originalPath).lastPathComponent)")
                    }
                case .putBack:
                    BoardTag(text: "Put back", tone: .accent)
                case .trashEmptied:
                    HStack(spacing: 6) {
                        Image(systemName: "lock").font(.system(size: 10))
                        Text(widthClass == .compact ? "TRASH EMPTIED" : "TRASH EMPTIED · CAN'T RESTORE")
                            .font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
                    }
                    .foregroundStyle(DiskMapTheme.ink3)
                    .help("The Trash was emptied after this cleanup, so there is nothing to put back.")
                }
            }
            .frame(width: widthClass == .compact ? 120 : 250, alignment: .trailing)
        }
        .padding(.horizontal, 18)
        .frame(height: 50)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .accessibilityElement(children: .contain)
    }

    // MARK: Words

    /// The card's tag: ALL IN TRASH, 2 IN TRASH, PUT BACK, TRASH EMPTIED.
    static func tag(_ states: [CleanupHistory.ItemState]) -> (text: String, accent: Bool) {
        let inTrash = states.filter { $0 == .inTrash }.count
        if inTrash > 0 { return (inTrash == states.count ? "All in Trash" : "\(inTrash) in Trash", true) }
        if !states.isEmpty, states.allSatisfy({ $0 == .putBack }) { return ("Put back", false) }
        return ("Trash emptied", false)
    }

    /// "Today, 11:42", "Yesterday, 09:10", "Thursday, 18:05", "2 Oct".
    static func whenText(_ date: Date, now: Date = Date()) -> String {
        let calendar = Calendar.current
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: date), to: calendar.startOfDay(for: now)).day ?? 0
        let time = date.formatted(.dateTime.hour(.twoDigits(amPM: .omitted)).minute(.twoDigits))
        switch days {
        case ..<1: return "Today, " + time
        case 1: return "Yesterday, " + time
        case 2...6:
            let f = DateFormatter()
            f.locale = Locale(identifier: "en_GB")
            f.dateFormat = "EEEE"
            return f.string(from: date) + ", " + time
        default: return dayText(date)
        }
    }

    /// "2 Oct", or "2 Oct 2025" in another year.
    static func dayText(_ date: Date, now: Date = Date()) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_GB")
        f.dateFormat = Calendar.current.isDate(date, equalTo: now, toGranularity: .year) ? "d MMM" : "d MMM yyyy"
        return f.string(from: date)
    }
}

private struct Line: Shape {
    func path(in rect: CGRect) -> Path {
        Path { p in
            p.move(to: CGPoint(x: rect.midX, y: rect.minY))
            p.addLine(to: CGPoint(x: rect.midX, y: rect.maxY))
        }
    }
}
