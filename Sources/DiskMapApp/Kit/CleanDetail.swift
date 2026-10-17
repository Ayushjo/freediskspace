import AppKit
import DiskMapCore
import SwiftUI

// The Clean up detail pages (Caches, Old downloads, Large media): one shell
// built from the list kit, so they read as Clean up's deeper lists.

/// One row of a detail page, whatever it came from.
struct CleanDetailRow: Identifiable {
    var id: String
    /// The scan node, when there is one (for selection and Explore).
    var nodeID: Int32?
    var name: String
    var path: String
    var place: String
    var badge: String
    var kind: String
    var bytes: Int64
    var changed: String
    var note: BiggestFilesView.FreeNote
    var accent: Bool
    /// What Add puts in the basket; empty when it can't go in.
    var requests: [CleanupStageRequest]
}

/// Caches | Old downloads | Large media.
struct CleanDetailSwitch: View {
    @ObservedObject var model: ScanModel
    var body: some View {
        SegmentSwitch(items: [
            .init(title: "Caches", isOn: model.destination == .cleanCaches) { model.destination = .cleanCaches },
            .init(title: "Old downloads", isOn: model.destination == .cleanDownloads) { model.destination = .cleanDownloads },
            .init(title: "Large media", isOn: model.destination == .cleanMedia) { model.destination = .cleanMedia },
        ])
    }
}

struct CleanDetailPage<Filters: View, Trailing: View>: View {
    @ObservedObject var model: ScanModel
    var title: String
    var subtitle: String
    var rows: [CleanDetailRow]
    /// Rows before filters: for the header's total.
    var totalCount: Int
    var totalBytes: Int64
    var kindTitle: String
    var changedTitle: String
    var noteTitle: String
    @Binding var query: String
    var searchPlaceholder: String
    var emptyTitle: String
    var emptyMessage: String
    /// The footer's bulk add: what it adds and how it is named.
    var bulkTitle: (_ count: Int, _ bytes: Int64) -> String
    var bulkFilter: (CleanDetailRow) -> Bool
    @ViewBuilder var filters: Filters
    @ViewBuilder var trailing: Trailing

    @Environment(\.widthClass) private var widthClass
    @FocusState private var searchFocused: Bool
    @State private var selectedID: String?

    static var rowLimit: Int { 300 }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                header
                HStack(spacing: 8) {
                    CleanDetailSwitch(model: model).padding(.trailing, 4)
                    filters
                }
                HStack(spacing: 12) {
                    ListSearchField(text: $query, tokens: [], matchCount: query.isEmpty ? nil : rows.count,
                                    placeholder: searchPlaceholder, focused: $searchFocused)
                        .frame(maxWidth: widthClass == .compact ? .infinity : 520)
                    Spacer(minLength: 0)
                    trailing
                }
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.top, widthClass == .compact ? 14 : 20)
            .padding(.bottom, 10)
            if rows.isEmpty {
                DiskMapEmptyState(symbol: "checkmark.circle", title: emptyTitle, message: emptyMessage)
            } else {
                VStack(spacing: 0) {
                    columnHeader
                    list
                    footer
                }
                .padding(.horizontal, widthClass.pagePadding)
            }
            NodeSelectionToolbar(model: model)
        }
        .background(DiskMapTheme.page)
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Button { model.destination = .cleanSafe } label: {
                HStack(spacing: 4) {
                    Image(systemName: "chevron.left").font(.system(size: 11, weight: .semibold))
                    Text("Clean up")
                }
                .font(DiskMapType.body)
                .foregroundStyle(DiskMapTheme.ink2)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Back to Clean up")
            Text(title).font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink).fixedSize()
            Text("\(totalCount.formatted()) · \(ByteFormat.hero(totalBytes))").font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).fixedSize()
            if widthClass != .compact {
                Text(subtitle).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1).truncationMode(.tail)
            }
            Spacer(minLength: 8)
        }
    }

    private var columns: ListColumns { ListColumns(widthClass) }

    private var columnHeader: some View {
        HStack(spacing: 0) {
            Text("Name").frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 12)
            Text(kindTitle).frame(width: columns.kind, alignment: .leading)
            Text("Size").frame(width: columns.bar + columns.size, alignment: .trailing)
            Text(changedTitle).frame(width: columns.changed, alignment: .leading).padding(.leading, 24)
            if columns.showFree { Text(noteTitle).frame(minWidth: columns.free, maxWidth: columns.freeMax, alignment: .leading) }
            Color.clear.frame(width: columns.add)
        }
        .font(DiskMapType.secondary)
        .foregroundStyle(DiskMapTheme.ink3)
        .frame(height: 30)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    private var list: some View {
        let shown = Array(rows.prefix(Self.rowLimit))
        let largest = max(1, shown.map(\.bytes).max() ?? 1)
        let staged = Set(model.stagedItems.map { $0.url.standardizedFileURL.path })
        let nodeIDs = shown.compactMap(\.nodeID)
        return ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(shown) { row in
                    let inBasket = !row.requests.isEmpty && row.requests.allSatisfy { staged.contains($0.url.standardizedFileURL.path) }
                    ListFileRow(
                        name: row.name, place: row.place, badge: row.badge, kind: row.kind,
                        fraction: Double(row.bytes) / Double(largest), bytes: row.bytes, changed: row.changed,
                        note: row.note, accent: row.accent, staged: inBasket,
                        selected: row.id == selectedID || row.nodeID.map(model.multiSelection.contains) == true,
                        columns: columns,
                        select: {
                            selectedID = row.id
                            if let id = row.nodeID { model.select(id, ordered: nodeIDs) }
                        },
                        add: row.requests.isEmpty ? nil : { add([row]) }
                    )
                    .rowActions(path: row.path, stage: row.requests.isEmpty ? nil : { add([row]) })
                }
            }
        }
        .listKeyboard(
            ids: shown.map(\.id), selection: $selectedID,
            path: { id in shown.first { $0.id == id }?.path },
            stage: { id in if let row = shown.first(where: { $0.id == id }), !row.requests.isEmpty { add([row]) } },
            selectAll: { model.multiSelection = Set(nodeIDs) },
            clearSelection: { model.clearMultiSelection() }
        )
    }

    private var footer: some View {
        let bulk = rows.filter { !$0.requests.isEmpty && bulkFilter($0) }
        let bulkBytes = bulk.reduce(Int64(0)) { $0 + $1.bytes }
        return HStack(spacing: 14) {
            Text(rows.count > Self.rowLimit
                 ? "Largest \(Self.rowLimit) of \(rows.count.formatted()) shown — narrow the filters for the rest"
                 : "\(rows.count.formatted()) shown · \(ByteFormat.hero(rows.reduce(0) { $0 + $1.bytes }))")
                .lineLimit(1)
            Spacer(minLength: 8)
            if !bulk.isEmpty {
                Button(bulkTitle(bulk.count, bulkBytes)) { add(bulk) }
                    .buttonStyle(SecondaryButtonStyle())
            }
        }
        .font(DiskMapType.figure)
        .foregroundStyle(DiskMapTheme.ink2)
        .padding(.vertical, 10)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    /// One row goes through the single-item checks; several through the confirm sheet.
    private func add(_ picked: [CleanDetailRow]) {
        let requests = picked.flatMap(\.requests)
        if requests.count == 1, let one = requests.first {
            model.stageRow(path: one.url.path, size: one.size, reason: one.reason)
        } else {
            Task { _ = await model.confirmStageMany(requests, title: title) }
        }
    }
}
