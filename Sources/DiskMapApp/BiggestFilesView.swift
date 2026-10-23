import AppKit
import DiskMapCore
import SwiftUI

/// Explore → List → Files (redesign board 05): the largest files in the scan,
/// filtered by kind, age and a token search, each one a click from the basket.
struct BiggestFilesView: View {
    @ObservedObject var model: ScanModel
    let tree: FileTree
    let rootURL: URL
    var onOpenCleanup: () -> Void = {}

    /// "Not changed in": how long a file must have sat untouched.
    enum AgeFilter: Int, CaseIterable, Identifiable {
        case any = 0, threeMonths = 90, sixMonths = 180, year = 365, twoYears = 730
        var id: Int { rawValue }
        var title: String {
            switch self {
            case .any: return "Any time"
            case .threeMonths: return "3 months"
            case .sixMonths: return "6 months"
            case .year: return "A year"
            case .twoYears: return "2 years"
            }
        }
    }

    @State private var query = ""
    @State private var kindFilter: FileKind? = nil
    @State private var ageFilter: AgeFilter = .any
    @State private var selectedID: Int32?
    @State private var isPreparingRanking = true
    /// Everything a row needs, worked out once per ranking off the main thread.
    @State private var entries: [Entry] = []
    /// `entries` narrowed — recomputed only when a filter changes, never while drawing.
    @State private var visible: [Entry] = []
    @State private var kindCounts: [FileKind: Int] = [:]
    @Environment(\.widthClass) private var widthClass
    @FocusState private var searchFocused: Bool

    struct Entry: Identifiable, Sendable {
        var id: Int32
        var name: String
        var absolutePath: String
        /// The containing folder, relative to the scan ("Downloads/misc").
        var parent: String
        var kind: FileKind
        var size: Int64
        var modifiedDay: Int32
        /// Lowercased name + display path, for the search box.
        var searchText: String
        /// Lowercased display path of the folder, for `in:`.
        var folderText: String
        /// Storage category (MAC-FIXES-FROM-WINDOWS §4.5) and what removing it means.
        var category: StorageVerdict
    }

    /// Kinds that get a chip, in board order. Everything else is only searchable.
    nonisolated static let chipKinds: [FileKind] = [.video, .archive, .diskImage, .application, .document, .database, .virtualDisk]
    /// Kept for the category filter set from Overview.
    nonisolated private static let kinds: [FileKind] = [.video, .diskImage, .archive, .application, .document, .other]

    private var totals: [Int64] { model.selectedTotals }
    private var parsed: ListQuery { ListQuery.parse(query) }

    private var filterKey: String {
        "\(query)|\(kindFilter?.rawValue ?? "")|\(ageFilter.rawValue)|\(model.folderFilterPath ?? "")|\(model.categoryFilter ?? "")|\(entries.count)"
    }

    private func refilter() {
        visible = Self.filter(entries, query: parsed, kind: kindFilter, age: ageFilter, folder: model.folderFilterPath,
                              category: model.categoryFilter, today: AgeMap.today())
    }

    /// The list as shown: every filter applied, largest first.
    static func filter(_ entries: [Entry], query: ListQuery, kind: FileKind?, age: AgeFilter, folder: String?,
                       category: String?, today: Int32) -> [Entry] {
        let prefix = folder.map { $0.hasSuffix("/") ? $0 : $0 + "/" }
        return entries.filter { entry in
            if let category, entry.category.storageClass.id != category { return false }
            if let folder, let prefix, entry.absolutePath != folder, !entry.absolutePath.hasPrefix(prefix) { return false }
            if let kind, entry.kind != kind { return false }
            let ageDays = entry.modifiedDay > 0 ? Int(today - entry.modifiedDay) : nil
            if age != .any { guard let ageDays, ageDays >= age.rawValue else { return false } }
            return query.matches(searchText: entry.searchText, folderPath: entry.folderText, size: entry.size,
                                 kind: entry.kind, ageDays: ageDays)
        }
        .sorted { $0.size > $1.size }
    }

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
            if isPreparingRanking {
                DiskMapLoadingState(title: "Finding biggest files", detail: "Ranking the scan without blocking the window.")
            } else if visible.isEmpty {
                DiskMapEmptyState(
                    symbol: "doc",
                    title: entries.isEmpty ? "No large files" : "No files match",
                    message: entries.isEmpty ? "The scan found no files of note." : "Try another kind, or clear the search."
                )
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
        .task(id: rankingID) { await prepareRanking() }
        .task(id: model.catalogGeneration) { await model.ensureCatalog(.reviewables) }
        .onChange(of: filterKey) { _, _ in refilter() }
    }

    // MARK: Header and filters

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("Biggest files").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink)
                .fixedSize()
            Text("\(entries.count.formatted()) largest · \(ByteFormat.hero(entries.reduce(0) { $0 + $1.size }))")
                .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                .fixedSize()
            if widthClass != .compact {
                Text("Every file in \(tree.count > 0 ? tree.name(of: 0) : "the scan"), sorted by size. Filter, then add what you want to the basket.")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1).truncationMode(.tail)
            }
            Spacer(minLength: 8)
            ExploreViewSwitch(model: model)
        }
    }

    private var filterRow: some View {
        HStack(spacing: 8) {
            ListPageSwitch(model: model)
            .padding(.trailing, 4)
            ListChip(title: "All", count: entries.count, isOn: kindFilter == nil) { kindFilter = nil }
            ForEach(shownKinds, id: \.self) { kind in
                ListChip(title: Self.chipTitle(kind), count: kindCounts[kind] ?? 0, isOn: kindFilter == kind) {
                    kindFilter = kindFilter == kind ? nil : kind
                }
            }
            if let category = model.categoryFilter {
                ListChip(title: StorageClassifier.storageClass(id: category)?.title ?? category, count: nil, isOn: true, removable: true) {
                    model.categoryFilter = nil
                }
            }
            if let folder = model.folderFilterPath {
                ListChip(title: "In " + CanonicalPath.displayPath(absolutePath: folder), count: nil, isOn: true, removable: true) {
                    model.folderFilterPath = nil
                }
                .help("Show files from everywhere")
            }
            Spacer(minLength: 8)
            ListDropdown(label: "Not changed in", options: AgeFilter.allCases, selection: $ageFilter, title: \.title)
        }
    }

    /// The kinds present, most first, as many as the width allows.
    private var shownKinds: [FileKind] {
        let limit = widthClass == .compact ? 3 : widthClass == .regular ? 5 : 7
        return Self.chipKinds.filter { (kindCounts[$0] ?? 0) > 0 }
            .sorted { (kindCounts[$0] ?? 0) > (kindCounts[$1] ?? 0) }
            .prefix(limit).map { $0 }
    }

    static func chipTitle(_ kind: FileKind) -> String {
        switch kind {
        case .video: return "Video"
        case .archive: return "Archives"
        case .diskImage: return "Disk images"
        case .application: return "Apps"
        case .document: return "Documents"
        case .database: return "Databases"
        case .virtualDisk: return "Virtual disks"
        case .deviceBackup: return "Backups"
        case .other: return "Other"
        }
    }

    private var searchRow: some View {
        HStack(spacing: 12) {
            ListSearchField(text: $query, tokens: parsed.tokens, matchCount: parsed.isEmpty ? nil : visible.count,
                            placeholder: "Search, or try size>1GB  kind:video  changed>1y  in:Downloads",
                            focused: $searchFocused)
                .frame(maxWidth: widthClass == .compact ? .infinity : 760)
            if widthClass != .compact || query.isEmpty {
                HStack(spacing: 12) {
                    Text("Try").foregroundStyle(DiskMapTheme.ink3)
                    ForEach(Self.suggestions(compact: widthClass == .compact), id: \.title) { s in
                        Button(s.title) { query = s.query }
                            .buttonStyle(.plain)
                            .foregroundStyle(DiskMapTheme.ink)
                            .underline(true, color: DiskMapTheme.line)
                    }
                }
                .font(DiskMapType.secondary)
                .lineLimit(1)
                .fixedSize()
            }
            Spacer(minLength: 0)
        }
    }

    /// The "Try" links: each a query the search box understands.
    static func suggestions(compact: Bool) -> [(title: String, query: String)] {
        let all: [(title: String, query: String)] = [
            ("Old installers", "kind:installer changed>6m"),
            ("Videos over 1 GB", "kind:video size>1GB"),
            ("Not changed in a year", "changed>1y"),
            ("In Downloads", "in:downloads"),
        ]
        return compact ? Array(all.prefix(2)) : all
    }

    // MARK: Table

    private var columns: ListColumns { ListColumns(widthClass) }

    private var columnHeader: some View {
        HStack(spacing: 0) {
            Text("Name").frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 12)
            Text("Kind").frame(width: columns.kind, alignment: .leading)
            Text("Size ↓").foregroundStyle(DiskMapTheme.ink).frame(width: columns.bar + columns.size, alignment: .trailing)
            Text("Last changed").frame(width: columns.changed, alignment: .leading).padding(.leading, 24)
            if columns.showFree { Text("Can be freed").frame(minWidth: columns.free, maxWidth: columns.freeMax, alignment: .leading) }
            Color.clear.frame(width: columns.add)
        }
        .font(DiskMapType.secondary)
        .foregroundStyle(DiskMapTheme.ink3)
        .frame(height: 30)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    private var list: some View {
        let rows = Array(visible.prefix(500))
        let ids = rows.map(\.id)
        let multi = model.multiSelection
        let index = model.freeableIndex
        let staged = Set(model.stagedItems.map { $0.url.standardizedFileURL.path })
        let largest = max(1, rows.first?.size ?? 1)
        let today = AgeMap.today()
        return ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(rows) { entry in
                    let freeable = index.contains(entry.id, tree: tree)
                    ListFileRow(
                        name: entry.name, place: entry.parent, badge: Self.badge(entry.name, kind: entry.kind),
                        kind: Self.kindText(entry.kind, entry.category.storageClass),
                        fraction: Double(entry.size) / Double(largest), bytes: entry.size,
                        changed: VisualizeView.lastUsed(entry.modifiedDay, today: today),
                        note: Self.freeNote(freeable: freeable, bytes: entry.size, verdict: entry.category),
                        accent: freeable, staged: staged.contains(URL(fileURLWithPath: entry.absolutePath).standardizedFileURL.path),
                        selected: entry.id == selectedID || multi.contains(entry.id), columns: columns,
                        select: {
                            selectedID = entry.id
                            model.select(entry.id, ordered: ids)
                        },
                        add: { stage(entry) }
                    )
                    .accessibilityLabel("\(entry.name), \(ByteFormat.string(entry.size)), changed \(RelativeAge.long(day: entry.modifiedDay))")
                    .rowActions(path: entry.absolutePath, stage: { stage(entry) })
                }
            }
        }
        .listKeyboard(
            ids: ids, selection: $selectedID,
            path: { id in rows.first { $0.id == id }?.absolutePath },
            stage: { id in if let entry = rows.first(where: { $0.id == id }) { stage(entry) } },
            selectAll: { model.multiSelection = Set(ids) },
            clearSelection: { model.clearMultiSelection() }
        )
        .onChange(of: selectedID) { _, id in if let id { model.selectedNode = id } }
    }

    private var footer: some View {
        let shownBytes = visible.reduce(Int64(0)) { $0 + $1.size }
        let index = model.freeableIndex
        let freeable = visible.filter { index.contains($0.id, tree: tree) }
        let freeBytes = freeable.reduce(Int64(0)) { $0 + $1.size }
        let filtered = visible.count != entries.count
        return HStack(spacing: 14) {
            (Text("\(entries.count.formatted()) largest files · \(ByteFormat.hero(entries.reduce(0) { $0 + $1.size }))")
             + Text(filtered ? "  ·  showing \(visible.count.formatted()) · \(ByteFormat.hero(shownBytes))" : "")
             + Text(freeBytes > 0 ? "  ·  " : "")
             + Text(freeBytes > 0 ? "\(ByteFormat.hero(freeBytes)) can be freed" : "").foregroundColor(DiskMapTheme.accentDeep))
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer(minLength: 8)
            if widthClass != .compact {
                Text("Packages like a Photos library count as one file")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
            }
            if visible.count > 1 {
                Button("Select all \(min(500, visible.count).formatted())") { model.multiSelection = Set(visible.prefix(500).map(\.id)) }
                    .buttonStyle(SecondaryButtonStyle())
            }
            if freeBytes > 0 {
                Button("Add \(ByteFormat.hero(freeBytes)) to basket") {
                    let requests = freeable.map {
                        CleanupStageRequest(url: URL(fileURLWithPath: $0.absolutePath), size: $0.size, reason: "Biggest file: " + $0.name)
                    }
                    Task { _ = await model.confirmStageMany(requests, title: "Biggest files that rebuild by themselves") }
                }
                .buttonStyle(SecondaryButtonStyle())
            }
        }
        .font(DiskMapType.figure)
        .foregroundStyle(DiskMapTheme.ink2)
        .padding(.vertical, 10)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    private func stage(_ entry: Entry) {
        model.stageRow(path: entry.absolutePath, size: entry.size, reason: "Biggest file: " + entry.name)
    }

    // MARK: Words

    /// "Downloads/misc/" → "Downloads/misc"; the scan root itself reads as "/".
    nonisolated static func trimmedPlace(_ place: String) -> String {
        var p = place
        while p.count > 1, p.hasSuffix("/") { p.removeLast() }
        return p.isEmpty ? "/" : p
    }

    /// The tile's short type ("MOV", "ZIP", "DMG").
    static func badge(_ name: String, kind: FileKind) -> String {
        let ext = (name as NSString).pathExtension.uppercased()
        if !ext.isEmpty, ext.count <= 4 { return ext }
        switch kind {
        case .video: return "VID"
        case .diskImage: return "IMG"
        case .archive: return "ZIP"
        case .application: return "APP"
        case .document: return "DOC"
        case .database: return "DB"
        case .virtualDisk: return "VM"
        case .deviceBackup: return "BAK"
        case .other: return ext.isEmpty ? "FILE" : String(ext.prefix(3))
        }
    }

    /// "Disk image", "Video": the type in sentence case.
    static func kindName(_ kind: FileKind) -> String {
        switch kind {
        case .diskImage: return "Disk image"
        case .virtualDisk: return "Virtual disk"
        case .deviceBackup: return "Device backup"
        default: return kind.title
        }
    }

    /// The Kind column: the file type, or the storage class when the type says nothing.
    static func kindText(_ kind: FileKind, _ storageClass: StorageClass) -> String {
        if kind != .other { return kindName(kind) }
        return storageClass.id == "other" ? "File" : storageClass.title
    }

    enum FreeNote: Equatable {
        case none
        /// Violet: "6.01 GB rebuilds by itself", "12 GB can be freed".
        case accent(String)
        case review(String?)
        /// Quiet words, e.g. why a forgotten file was flagged.
        case plain(String)
    }

    /// The "Can be freed" column: rebuilds by itself, look first (with what to do instead), or nothing.
    static func freeNote(freeable: Bool, bytes: Int64, verdict: StorageVerdict) -> FreeNote {
        if freeable { return .accent("\(ByteFormat.hero(bytes)) rebuilds by itself") }
        if verdict.advice.isRisky { return .review(verdict.instead) }
        return .none
    }

    // MARK: Ranking

    private var rankingID: String {
        let rootSize = totals.first ?? 0
        return "\(tree.count):\(rootSize):\(model.sizeBasis)"
    }

    @MainActor
    private func prepareRanking() async {
        guard totals.count == tree.count else {
            entries = []
            visible = []
            isPreparingRanking = false
            return
        }
        isPreparingRanking = true
        let sourceTree = tree
        let sourceTotals = totals
        let root = rootURL
        let (built, counts) = await Task.detached(priority: .userInitiated) { () -> ([Entry], [FileKind: Int]) in
            let ids = TopSizes.rankedFiles(tree: sourceTree, totals: sourceTotals, limit: 2_000)
            var counts: [FileKind: Int] = [:]
            var built: [Entry] = []
            built.reserveCapacity(ids.count)
            for id in ids {
                let name = sourceTree.name(of: id)
                let abs = sourceTree.path(of: id, root: root).path
                let kind = FileKind.classify(fileName: name, path: abs)
                counts[kind, default: 0] += 1
                let parent = Self.trimmedPlace(relativeParent(of: abs, root: root))
                built.append(Entry(
                    id: id, name: name, absolutePath: abs, parent: parent,
                    kind: kind, size: sourceTotals[Int(id)], modifiedDay: sourceTree.modifiedDay[Int(id)],
                    searchText: name.lowercased() + "\n" + CanonicalPath.displayPath(absolutePath: abs).lowercased(),
                    folderText: parent.lowercased(),
                    category: StorageClassifier.classify(path: abs, isDirectory: false)
                ))
            }
            return (built, counts)
        }.value
        guard !Task.isCancelled else { return }
        entries = built
        kindCounts = counts
        refilter()
        isPreparingRanking = false
    }

    /// Categories among `entries`, by bytes, Other last.
    static func categoryCounts(_ entries: [Entry]) -> [(id: String, title: String, count: Int, bytes: Int64)] {
        var byID: [String: (title: String, count: Int, bytes: Int64)] = [:]
        for entry in entries {
            let cls = entry.category.storageClass
            var row = byID[cls.id] ?? (cls.title, 0, 0)
            row.count += 1
            row.bytes += entry.size
            byID[cls.id] = row
        }
        return byID.map { (id: $0.key, title: $0.value.title, count: $0.value.count, bytes: $0.value.bytes) }
            .sorted { ($0.id == "other") != ($1.id == "other") ? $1.id == "other" : $0.bytes > $1.bytes }
    }
}
