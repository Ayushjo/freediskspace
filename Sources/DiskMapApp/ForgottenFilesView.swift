import AppKit
import DiskMapCore
import SwiftUI

/// Find → Forgotten Files. Uses ScanModel.cachedForgotten (computed once per scan).
struct ForgottenFilesView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.diskMapContentWidth) private var contentWidth
    let tree: FileTree
    let rootURL: URL
    var onOpenCleanup: () -> Void = {}

    enum FilterTab: String, CaseIterable, Identifiable {
        case all, likely, worth, excluded
        var id: String { rawValue }
        var title: String {
            switch self {
            case .all: return "All"
            case .likely: return "Likely forgotten"
            case .worth: return "Worth reviewing"
            case .excluded: return "Excluded"
            }
        }
    }

    enum SortMode: String, CaseIterable, Identifiable {
        case reviewValue, largest, oldest, name
        var id: String { rawValue }
        var title: String {
            switch self {
            case .reviewValue: return "Most relevant"
            case .largest: return "Largest"
            case .oldest: return "Oldest"
            case .name: return "Name"
            }
        }
    }

    @State private var query = ""
    @State private var filterTab: FilterTab = .all
    @State private var sortMode: SortMode = .reviewValue
    @State private var selectedID: Int32?
    @State private var ageFilter: ForgottenAgeBucket?
    @State private var onlyLarge = false
    @State private var onlyDownloads = false
    @State private var onlyMedia = false

    private var allCandidates: [ForgottenCandidate] { model.cachedForgotten }
    private var summary: ForgottenSummary { model.cachedForgottenSummary }

    private var visible: [ForgottenCandidate] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        var list = allCandidates.filter { c in
            switch filterTab {
            case .all: return c.isReviewable
            case .likely: return c.confidence == .likelyForgotten
            case .worth: return c.confidence == .worthReviewing
            case .excluded: return c.confidence == .oldImportant
            }
        }
        if let ageFilter {
            list = list.filter { ForgottenAgeBucket.bucket(ageDays: $0.ageDays) == ageFilter }
        }
        if onlyLarge { list = list.filter { $0.bytes >= 1_000_000_000 } }
        if onlyDownloads { list = list.filter { $0.absolutePath.lowercased().contains("/downloads/") } }
        if onlyMedia {
            list = list.filter { [.video, .diskImage, .archive, .deviceBackup].contains($0.kind) }
        }
        if !q.isEmpty {
            list = list.filter {
                $0.name.lowercased().contains(q)
                    || $0.displayPath.lowercased().contains(q)
                    || $0.parentDisplay.lowercased().contains(q)
            }
        }
        switch sortMode {
        case .reviewValue: list.sort { $0.score > $1.score }
        case .largest: list.sort { $0.bytes > $1.bytes }
        case .oldest: list.sort { $0.ageDays > $1.ageDays }
        case .name: list.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        }
        return list
    }

    private var active: ForgottenCandidate? {
        if let selectedID, let hit = visible.first(where: { $0.id == selectedID }) { return hit }
        if let selectedID, let hit = allCandidates.first(where: { $0.id == selectedID }) { return hit }
        return visible.first
    }

    /// ⌘-selected rows that may be staged: excluded (app-managed) files never are.
    private var checkedReviewable: [ForgottenCandidate] {
        visible.filter { model.multiSelection.contains($0.id) && $0.isReviewable && $0.safety.level != .protected }
    }

    @Environment(\.widthClass) private var widthClass

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                header
                filterRow
                searchRow
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.top, widthClass == .compact ? 14 : 20)
            .padding(.bottom, 10)
            if visible.isEmpty {
                emptyState
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
        .catalogGate(.forgotten, model: model, title: "Finding forgotten files…")
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("Forgotten files").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink).fixedSize()
            Text("\(summary.reviewableCount.formatted()) files · \(ByteFormat.hero(summary.reviewableBytes))")
                .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).fixedSize()
            if widthClass != .compact {
                Text("Your own files, untouched for a year or more. App and system data is left out.")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1).truncationMode(.tail)
            }
            Spacer(minLength: 8)
            ExploreViewSwitch(model: model)
        }
    }

    private var filterRow: some View {
        HStack(spacing: 8) {
            ListPageSwitch(model: model).padding(.trailing, 4)
            ListChip(title: "All", count: summary.reviewableCount, isOn: filterTab == .all) { filterTab = .all }
            ListChip(title: "Likely forgotten", count: summary.likelyCount, isOn: filterTab == .likely) { filterTab = filterTab == .likely ? .all : .likely }
            if widthClass != .compact {
                ListChip(title: "Worth a look", count: summary.worthCount, isOn: filterTab == .worth) { filterTab = filterTab == .worth ? .all : .worth }
            }
            ListChip(title: "Over 1 GB", count: nil, isOn: onlyLarge) { onlyLarge.toggle() }
            if widthClass == .wide {
                ListChip(title: "Downloads", count: nil, isOn: onlyDownloads) { onlyDownloads.toggle() }
                ListChip(title: "Media", count: nil, isOn: onlyMedia) { onlyMedia.toggle() }
            }
            Spacer(minLength: 8)
            ListDropdown(label: "Untouched for", options: AgeChoice.all, selection: Binding(
                get: { AgeChoice(bucket: ageFilter) }, set: { ageFilter = $0.bucket }), title: \.title)
        }
    }

    /// The age dropdown's choices: any, or one bucket.
    struct AgeChoice: Hashable, Identifiable {
        var bucket: ForgottenAgeBucket?
        var id: String { bucket?.rawValue ?? "any" }
        var title: String { bucket?.title ?? "Any age" }
        static var all: [AgeChoice] { [AgeChoice(bucket: nil)] + ForgottenAgeBucket.allCases.map { AgeChoice(bucket: $0) } }
    }

    private var searchRow: some View {
        HStack(spacing: 12) {
            ListSearchField(text: $query, tokens: [], matchCount: query.isEmpty ? nil : visible.count,
                            placeholder: "Search forgotten files by name or folder", focused: $searchFocused)
                .frame(maxWidth: widthClass == .compact ? .infinity : 520)
            Spacer(minLength: 0)
            if filterTab != .excluded, summary.excludedCount > 0 {
                Button("\(summary.excludedCount) left out on purpose") { filterTab = .excluded }
                    .buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    .help("Old files that belong to apps, package managers or macOS. They are never suggested.")
            } else if filterTab == .excluded {
                Button("Back to suggestions") { filterTab = .all }.buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
            }
            ListDropdown(label: "Sort", options: SortMode.allCases, selection: $sortMode, title: \.title)
        }
    }

    @FocusState private var searchFocused: Bool
    private var columns: ListColumns { ListColumns(widthClass) }

    private var columnHeader: some View {
        HStack(spacing: 0) {
            Text("Name").frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 12)
            Text("Suggestion").frame(width: columns.kind, alignment: .leading)
            Text(sortMode == .largest ? "Size ↓" : "Size").frame(width: columns.bar + columns.size, alignment: .trailing)
            Text(sortMode == .oldest ? "Untouched ↓" : "Untouched").frame(width: columns.changed, alignment: .leading).padding(.leading, 24)
            if columns.showFree { Text("Why").frame(minWidth: columns.free, maxWidth: columns.freeMax, alignment: .leading) }
            Color.clear.frame(width: columns.add)
        }
        .font(DiskMapType.secondary)
        .foregroundStyle(DiskMapTheme.ink3)
        .frame(height: 30)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    private var list: some View {
        let items = visible
        let ids = items.map(\.id)
        let largest = max(1, items.map(\.bytes).max() ?? 1)
        let staged = Set(model.stagedItems.map { $0.url.standardizedFileURL.path })
        return ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(items) { c in
                    let stageable = c.isReviewable && c.safety.level != .protected
                    ListFileRow(
                        name: c.name, place: BiggestFilesView.trimmedPlace(relativeParent(of: c.absolutePath, root: rootURL)),
                        badge: BiggestFilesView.badge(c.name, kind: c.kind), kind: Self.confidenceLabel(c.confidence),
                        fraction: Double(c.bytes) / Double(largest), bytes: c.bytes,
                        changed: ForgottenAgeFormat.string(c.ageDays),
                        note: .plain(Self.why(c.reasons)),
                        accent: c.confidence == .likelyForgotten,
                        staged: staged.contains(URL(fileURLWithPath: c.absolutePath).standardizedFileURL.path),
                        selected: c.id == selectedID || model.multiSelection.contains(c.id), columns: columns,
                        select: {
                            selectedID = c.id
                            model.select(c.id, ordered: ids)
                        },
                        add: stageable ? { stageOne(c) } : nil
                    )
                    .rowActions(path: c.absolutePath, stage: stageable ? { stageOne(c) } : nil)
                }
            }
        }
        .listKeyboard(
            ids: ids, selection: $selectedID,
            path: { id in items.first { $0.id == id }?.absolutePath },
            stage: { id in if let candidate = items.first(where: { $0.id == id }) { stageOne(candidate) } },
            selectAll: { model.multiSelection = Set(items.filter(\.isReviewable).map(\.id)) },
            clearSelection: { model.clearMultiSelection() }
        )
        .onChange(of: selectedID) { _, id in if let id { model.selectedNode = id } }
    }

    private var footer: some View {
        let likely = visible.filter { $0.confidence == .likelyForgotten && $0.isReviewable && $0.safety.level != .protected }
        let likelyBytes = likely.reduce(Int64(0)) { $0 + $1.bytes }
        return HStack(spacing: 14) {
            Text("\(visible.count.formatted()) shown · \(ByteFormat.hero(visible.reduce(0) { $0 + $1.bytes }))").lineLimit(1)
            Spacer(minLength: 8)
            if widthClass != .compact {
                Text("By last-modified date: a file you only open may still look old.")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
            }
            if !likely.isEmpty {
                Button("Add \(likely.count) likely forgotten · \(ByteFormat.hero(likelyBytes))") {
                    Task {
                        _ = await model.confirmStageMany(likely.map {
                            CleanupStageRequest(url: URL(fileURLWithPath: $0.absolutePath).standardizedFileURL, size: $0.bytes,
                                                reason: "Forgotten: " + $0.confidence.title)
                        }, title: "Forgotten files")
                    }
                }
                .buttonStyle(SecondaryButtonStyle())
            }
        }
        .font(DiskMapType.figure)
        .foregroundStyle(DiskMapTheme.ink2)
        .padding(.vertical, 10)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    static func ageTint(_ bucket: ForgottenAgeBucket) -> Color {
        switch bucket {
        case .oneToTwoYears: return DiskMapTheme.hex("B9A071")
        case .twoToThreeYears: return DiskMapTheme.hex("C99A7E")
        case .threeToFiveYears: return DiskMapTheme.hex("C78797")
        case .overFiveYears: return DiskMapTheme.hex("A795C7")
        }
    }

    /// The Why column: the reasons, without the age (the Untouched column says it).
    static func why(_ reasons: [String]) -> String {
        reasons.filter { !$0.lowercased().hasPrefix("not modified") }.joined(separator: " · ")
    }

    static func confidenceLabel(_ confidence: ForgottenConfidence) -> String {
        switch confidence {
        case .likelyForgotten: return "Likely forgotten"
        case .worthReviewing: return "Worth a look"
        case .oldImportant: return "Left out"
        }
    }

    static func confidenceTint(_ confidence: ForgottenConfidence) -> Color {
        switch confidence {
        case .likelyForgotten: return DiskMapTheme.safe
        case .worthReviewing: return DiskMapTheme.review
        case .oldImportant: return DiskMapTheme.ink3
        }
    }

    @ViewBuilder
    private var emptyState: some View {
        if summary.excludedCount > 0 && summary.reviewableCount == 0 && filterTab != .excluded {
            VStack(spacing: 12) {
                DiskMapEmptyState(symbol: "clock", title: "Nothing worth reviewing yet",
                                  message: "Old files were found, but they belong to apps, package managers or macOS, so they aren’t suggested here.")
                    .frame(maxHeight: 200)
                Button("Show excluded") { filterTab = .excluded }
                    .buttonStyle(SecondaryButtonStyle())
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            DiskMapEmptyState(symbol: "clock",
                              title: filterTab == .excluded ? "No excluded old files" : "No forgotten files found",
                              message: "Try another filter, or clear the search.")
        }
    }

    private func stageOne(_ c: ForgottenCandidate) {
        guard c.isReviewable, c.safety.level != .protected else {
            model.showToast("Not recommended for cleanup")
            return
        }
        model.stageRow(path: c.absolutePath, size: c.bytes, reason: "Forgotten: " + c.confidence.title)
    }
}

enum ForgottenAgeFormat {
    /// For list columns: "8 mo", "3 y", "Very old".
    static func short(_ ageDays: Int32) -> String {
        if ageDays >= 15 * 365 { return "Very old" }
        if ageDays < 365 { return "\(max(1, ageDays / 30)) mo" }
        return "\(ageDays / 365) y"
    }

    static func string(_ ageDays: Int32) -> String {
        // Packaging mtimes (cargo/npm sources) can look absurdly old — keep honest but compact.
        if ageDays >= 15 * 365 { return "Very old" }
        if ageDays < 60 { return "\(ageDays)d ago" }
        if ageDays < 365 {
            let m = max(1, ageDays / 30)
            return m == 1 ? "1 month ago" : "\(m) months ago"
        }
        let y = max(1, ageDays / 365)
        return y == 1 ? "1 year ago" : "\(y) years ago"
    }
}

