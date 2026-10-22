import AppKit
import DiskMapCore
import SwiftUI

/// Find (TASK-060): one list over the whole scan, narrowed by chips and the
/// query language (TASK-059). A chip is only a shortcut for a query token, so
/// the text box always shows exactly what is being asked.
///
/// Also the old Search page: a query of one bare word (optionally with
/// `type:`) runs through `FileSearchIndex`, the as-you-type name index;
/// anything else runs through `FileQuery`. One page, both engines.
struct FindView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.diskMapContentWidth) private var contentWidth
    let tree: FileTree
    let rootURL: URL

    struct QueryChip: Identifiable {
        var id: String
        var title: String
        var token: String
        var symbol: String
    }

    static let chips: [QueryChip] = [
        QueryChip(id: "large", title: "Large", token: "size>500MB", symbol: "arrow.up.right.square"),
        QueryChip(id: "old", title: "Old", token: "age>1y", symbol: "clock"),
        QueryChip(id: "duplicated", title: "Duplicated", token: "is:duplicate", symbol: "doc.on.doc"),
        QueryChip(id: "cached", title: "Cached", token: "in:caches", symbol: "internaldrive"),
        QueryChip(id: "media", title: "Media", token: "kind:media", symbol: "film"),
        QueryChip(id: "downloads", title: "Downloads", token: "in:downloads", symbol: "arrow.down.circle"),
        QueryChip(id: "folders", title: "Folders", token: "type:folder", symbol: "folder"),
    ]

    static let examples = [
        "ext:mp4,mov size>500MB",
        "in:downloads age>6m",
        "name:*.log size>50MB",
        "type:folder node_modules",
        "kind:archive in:library",
        "is:hardlink",
    ]

    static let exampleMeanings = [
        "Videos over 500 MB",
        "Downloads untouched for six months",
        "Log files over 50 MB",
        "Every node_modules folder",
        "Archives inside Library",
        "Files with more than one hard link",
    ]

    static let keyReference: [(String, String)] = [
        ("ext:  name:", "extension or name pattern"),
        ("kind:", "video audio image media document developer archive"),
        ("size>  size<", "500MB, 2GB"),
        ("age>  age<", "30d, 6m, 1y"),
        ("in:", "downloads desktop documents library caches"),
        ("is:", "duplicate hardlink cloud"),
        ("type:", "file folder any"),
        ("word  -word", "name contains / excludes"),
    ]

    struct Row: Identifiable, Sendable {
        var id: Int32
        var name: String
        var absolutePath: String
        var kind: FileKind
        var bytes: Int64
        var modifiedDay: Int32
        var isDirectory: Bool
    }

    @State private var rows: [Row] = []
    @State private var savingSearch = false
    @State private var matchCount = 0
    /// Nil when the name index answered (it counts matches, not bytes).
    @State private var matchedBytes: Int64? = 0
    @State private var notes: [String] = []
    @State private var isRunning = false
    @State private var selectedID: Int32?
    @State private var index: FileSearchIndex?
    @State private var indexGeneration = 0

    private var home: String { NSHomeDirectory() }
    private var trimmedQuery: String { model.findQuery.trimmingCharacters(in: .whitespacesAndNewlines) }
    private var parsed: FileQuery.Parsed { FileQuery.parse(model.findQuery, home: home, root: rootURL.path) }

    private var runKey: String {
        "\(model.findQuery)|\(model.findSort.rawValue)|\(tree.count)|\(model.sizeBasis)|\(model.duplicateDidRun)|\(model.duplicateGroups.count)|\(indexGeneration)"
    }

    private var activeSelection: Int32? {
        if let selectedID, rows.contains(where: { $0.id == selectedID }) { return selectedID }
        return rows.first?.id
    }

    @Environment(\.widthClass) private var widthClass
    @FocusState private var searchFocused: Bool

    var body: some View {
        // One layout whether or not there's a query: switching layouts on the
        // first keystroke rebuilt the field and dropped focus after one letter.
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 12) {
                header
                searchRow
                chipRow
                if !trimmedQuery.isEmpty { meaning }
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.top, widthClass == .compact ? 14 : 20)
            .padding(.bottom, 10)
            if trimmedQuery.isEmpty {
                startState
            } else if rows.isEmpty && !isRunning {
                DiskMapEmptyState(symbol: "magnifyingglass", title: "Nothing matches",
                                  message: "Remove a chip or loosen a size or age.")
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
        .task(id: runKey) { await run() }
        .task(id: model.scanID) {
            index = nil
            let source = tree
            let built = await Task.detached(priority: .userInitiated) { FileSearchIndex(tree: source) }.value
            guard !Task.isCancelled else { return }
            index = built
            indexGeneration += 1
        }
        .sheet(isPresented: $savingSearch) {
            SaveSearchSheet(model: model, isPresented: $savingSearch,
                            suggestedName: SavedSearches.defaultName(for: model.findQuery, home: home, root: rootURL.path))
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("Find").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink).fixedSize()
            if !trimmedQuery.isEmpty {
                Text(summaryParts.joined(separator: " · ")).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).fixedSize()
            } else if widthClass != .compact {
                Text("Type a name, or narrow by size, age, kind and place.")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
            }
            Spacer(minLength: 8)
            ExploreViewSwitch(model: model)
        }
    }

    private var summaryParts: [String] {
        if isRunning && rows.isEmpty { return ["Searching…"] }
        var parts = ["\(matchCount.formatted()) match\(matchCount == 1 ? "" : "es")"]
        if let matchedBytes, matchCount > 0 { parts.append(ByteFormat.hero(matchedBytes)) }
        return parts
    }

    // MARK: Controls

    /// Tokens the query understood, shown as violet tags in the field.
    private var tokens: [String] {
        model.findQuery.split(whereSeparator: \.isWhitespace).map(String.init)
            .filter { $0.contains(":") || $0.contains(">") || $0.contains("<") }
    }

    private var searchRow: some View {
        HStack(spacing: 10) {
            ListSearchField(text: $model.findQuery, tokens: tokens, matchCount: trimmedQuery.isEmpty ? nil : matchCount,
                            placeholder: "Name, or ext:mp4 size>500MB age>1y in:downloads", focused: $searchFocused)
                .accessibilityLabel("Find query")
            ListDropdown(label: "Sort", options: FileQuery.Sort.allCases, selection: $model.findSort, title: \.findTitle)
            // TASK-081: keep this query in the sidebar (⌘S).
            Button("Save…") { savingSearch = true }
                .buttonStyle(SecondaryButtonStyle())
                .keyboardShortcut("s", modifiers: .command)
                .disabled(trimmedQuery.isEmpty)
                .help("Keep this search in the sidebar, with its size kept up to date")
        }
        .onAppear { searchFocused = true }
    }

    private var chipRow: some View {
        HStack(spacing: 8) {
            ForEach(Self.chips.prefix(widthClass == .compact ? 5 : 7)) { chip in
                let on = FileQuery.contains(chip.token, in: model.findQuery)
                ListChip(title: chip.title, count: nil, isOn: on) {
                    if !on { Self.recordChipUse(chip.id) }
                    model.findQuery = FileQuery.toggling(chip.token, in: model.findQuery)
                }
                .help(chip.token)
                .accessibilityLabel("\(chip.title), adds \(chip.token)")
            }
            Spacer(minLength: 0)
        }
    }

    static func sortTitle(_ sort: FileQuery.Sort) -> String { sort.findTitle }

    /// What the query means, in words, plus anything it could not use.
    private var meaning: some View {
        let parsed = self.parsed
        return VStack(alignment: .leading, spacing: 4) {
            Text(parsed.query.describe(home: home).joined(separator: "  ·  "))
                .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3).lineLimit(2)
            ForEach(parsed.problems, id: \.token) { problem in
                Text("Ignoring \(problem.token) — \(problem.message)").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.review)
            }
            ForEach(notes, id: \.self) { note in
                HStack(spacing: 8) {
                    Text(note).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.review)
                    if note.hasPrefix("Duplicates") {
                        Button("Find duplicates") { model.destination = .duplicates }
                            .buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
                    }
                }
            }
        }
    }

    // MARK: Start state

    private var startState: some View {
        ScrollView {
            let layout = widthClass == .compact ? AnyLayout(VStackLayout(alignment: .leading, spacing: 16)) : AnyLayout(HStackLayout(alignment: .top, spacing: 16))
            layout {
                startCard("Try") {
                    ForEach(Array(Self.examples.enumerated()), id: \.offset) { offset, example in
                        ExampleRow(query: example, meaning: Self.exampleMeanings[offset]) { model.findQuery = example }
                            .overlay(alignment: .top) { if offset > 0 { Rectangle().fill(DiskMapTheme.line).frame(height: 1) } }
                    }
                }
                VStack(spacing: 16) {
                    startCard("Keys") {
                        Grid(alignment: .leading, horizontalSpacing: 20, verticalSpacing: 8) {
                            ForEach(Self.keyReference, id: \.0) { key, detail in
                                GridRow {
                                    Text(key).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                                    Text(detail).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                                }
                            }
                        }
                        .padding(16)
                    }
                    startCard("Keep one in the sidebar") {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(SavedSearches.starters.enumerated()), id: \.element.query) { index, starter in
                                let saved = model.savedSearches.contains { $0.query == starter.query }
                                HStack {
                                    Text(starter.name).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                                    Text(starter.query).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
                                    Spacer()
                                    Button(saved ? "Kept" : "Keep") {
                                        model.saveSearch(name: starter.name, query: starter.query, sort: .largest)
                                    }
                                    .buttonStyle(SecondaryButtonStyle())
                                    .disabled(saved)
                                }
                                .padding(.horizontal, 16).frame(height: 48)
                                .overlay(alignment: .top) { if index > 0 { Rectangle().fill(DiskMapTheme.line).frame(height: 1) } }
                            }
                        }
                    }
                }
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.bottom, widthClass.pagePadding)
        }
    }

    private func startCard<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title).font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
                .padding(.horizontal, 16).frame(height: 44)
                .frame(maxWidth: .infinity, alignment: .leading)
                .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            content()
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .frame(maxWidth: .infinity, alignment: .topLeading)
    }

    // MARK: List

    private var columns: ListColumns { ListColumns(widthClass) }

    private var columnHeader: some View {
        HStack(spacing: 0) {
            Text("Name").frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 12)
            Text("Kind").frame(width: columns.kind, alignment: .leading)
            Text(model.findSort == .largest ? "Size ↓" : "Size").frame(width: columns.bar + columns.size, alignment: .trailing)
            Text(model.findSort == .largest ? "Last changed" : "Last changed ↓").frame(width: columns.changed, alignment: .leading).padding(.leading, 24)
            if columns.showFree { Text("Can be freed").frame(minWidth: columns.free, maxWidth: columns.freeMax, alignment: .leading) }
            Color.clear.frame(width: columns.add)
        }
        .font(DiskMapType.secondary)
        .foregroundStyle(DiskMapTheme.ink3)
        .frame(height: 30)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    private var list: some View {
        let ids = rows.map(\.id)
        let index = model.freeableIndex
        let staged = Set(model.stagedItems.map { $0.url.standardizedFileURL.path })
        let largest = max(1, rows.map(\.bytes).max() ?? 1)
        let today = AgeMap.today()
        return ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(rows) { row in
                    let freeable = index.contains(row.id, tree: tree)
                    let verdict = StorageClassifier.classify(path: row.absolutePath, isDirectory: row.isDirectory)
                    let stage = { model.stageRow(path: row.absolutePath, size: row.bytes, reason: "Find: \(trimmedQuery)") }
                    ListFileRow(
                        name: row.name, place: BiggestFilesView.trimmedPlace(relativeParent(of: row.absolutePath, root: rootURL)),
                        badge: row.isDirectory ? "DIR" : BiggestFilesView.badge(row.name, kind: row.kind),
                        kind: row.isDirectory ? BiggestFoldersView.folderKind(verdict.storageClass) : BiggestFilesView.kindText(row.kind, verdict.storageClass),
                        fraction: Double(row.bytes) / Double(largest), bytes: row.bytes,
                        changed: VisualizeView.lastUsed(row.modifiedDay, today: today),
                        note: BiggestFilesView.freeNote(freeable: freeable, bytes: row.bytes, verdict: verdict),
                        accent: freeable,
                        staged: staged.contains(URL(fileURLWithPath: row.absolutePath).standardizedFileURL.path),
                        selected: row.id == selectedID || model.multiSelection.contains(row.id), columns: columns,
                        select: {
                            selectedID = row.id
                            model.select(row.id, ordered: ids)
                        },
                        add: stage,
                        open: row.isDirectory ? {
                            model.currentNode = row.id
                            model.selectedNode = row.id
                            model.destination = .biggestFolders
                        } : nil
                    )
                    .rowActions(path: row.absolutePath, stage: stage)
                    .contextMenu {
                        Button("Show in Explore") {
                            model.selectedNode = row.id
                            model.currentNode = row.isDirectory ? row.id : max(0, tree.parent[Int(row.id)])
                            model.destination = .visualize
                        }
                    }
                }
            }
        }
        .listKeyboard(
            ids: ids, selection: $selectedID,
            path: { id in rows.first { $0.id == id }?.absolutePath },
            stage: { id in
                if let row = rows.first(where: { $0.id == id }) {
                    model.stageRow(path: row.absolutePath, size: row.bytes, reason: "Find: \(trimmedQuery)")
                }
            },
            selectAll: { model.multiSelection = Set(ids) },
            clearSelection: { model.clearMultiSelection() }
        )
        .onChange(of: selectedID) { _, id in if let id { model.selectedNode = id } }
        .opacity(isRunning ? 0.6 : 1)
    }

    private var footer: some View {
        HStack(spacing: 14) {
            Text(matchCount > rows.count
                 ? "Showing the first \(rows.count.formatted()) of \(matchCount.formatted()) — narrow the query to see the rest."
                 : "\(rows.count.formatted()) shown" + (matchedBytes.map { " · \(ByteFormat.hero($0))" } ?? ""))
                .lineLimit(1)
            Spacer(minLength: 8)
            if rows.count > 1 {
                Button("Select all \(rows.count.formatted())") { model.multiSelection = Set(rows.map(\.id)) }
                    .buttonStyle(SecondaryButtonStyle())
            }
        }
        .font(DiskMapType.figure)
        .foregroundStyle(DiskMapTheme.ink2)
        .padding(.vertical, 10)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    static func relativeModified(_ day: Int32) -> String { RelativeAge.long(day: day) }

    // MARK: Running

    /// The name index answers a single bare word ranked by size — it is
    /// instant on million-item trees. Everything else needs `FileQuery`.
    static func indexNeedle(for query: FileQuery, sort: FileQuery.Sort) -> (String, FileSearchIndex.KindFilter)? {
        guard sort == .largest, query.words.count == 1 else { return nil }
        var plain = FileQuery()
        plain.words = query.words
        plain.explicitType = query.explicitType
        guard query == plain else { return nil }
        let kind: FileSearchIndex.KindFilter
        switch query.explicitType {
        case .files: kind = .files
        case .folders: kind = .folders
        case .any, .none: kind = .all
        }
        return (query.words[0], kind)
    }

    nonisolated static func makeRow(_ id: Int32, tree: FileTree, root: URL, totals: [Int64]) -> Row {
        let path = tree.path(of: id, root: root).path
        let name = tree.name(of: id)
        return Row(id: id, name: name, absolutePath: path,
                   kind: FileKind.classify(fileName: name, path: path),
                   bytes: totals.indices.contains(Int(id)) ? totals[Int(id)] : 0,
                   modifiedDay: tree.modifiedDay[Int(id)],
                   isDirectory: tree.isDirectory[Int(id)])
    }

    @MainActor
    private func run() async {
        let text = trimmedQuery
        guard !text.isEmpty else {
            rows = []; matchCount = 0; matchedBytes = 0; notes = []; isRunning = false
            return
        }
        isRunning = true
        try? await Task.sleep(for: .milliseconds(120))   // let typing settle
        guard !Task.isCancelled else { return }
        let query = parsed.query
        let totals = model.selectedTotals
        let sort = model.findSort
        let sourceTree = tree
        let root = rootURL

        if let index, let (needle, kind) = Self.indexNeedle(for: query, sort: sort) {
            let work = Task.detached(priority: .userInitiated) { () -> (FileSearchIndex.Result, [Row]) in
                let found = index.search(needle, in: sourceTree, totals: totals, kind: kind, limit: 500)
                return (found, found.ids.map { Self.makeRow($0, tree: sourceTree, root: root, totals: totals) })
            }
            let (found, newRows) = await withTaskCancellationHandler { await work.value } onCancel: { work.cancel() }
            guard !Task.isCancelled else { return }
            rows = newRows
            matchCount = found.totalMatches
            // A folder's size already holds its matching children, so only a
            // complete, files-only answer can be summed.
            matchedBytes = found.totalMatches == newRows.count && !newRows.contains(where: \.isDirectory)
                ? newRows.reduce(0) { $0 + $1.bytes } : nil
            notes = []
            isRunning = false
            return
        }

        let duplicates: Set<Int32>? = model.duplicateDidRun ? Set(model.duplicateGroups.flatMap(\.fileIDs)) : nil
        let context = FileQuery.Context(home: home, duplicateFileIDs: duplicates)
        let work = Task.detached(priority: .userInitiated) { () -> (FileQuery.Result, [Row]) in
            let result = query.run(tree: sourceTree, root: root, totals: totals, context: context,
                                   sort: sort, limit: 500, isCancelled: { Task.isCancelled })
            return (result, result.ids.map { Self.makeRow($0, tree: sourceTree, root: root, totals: totals) })
        }
        let (result, newRows) = await withTaskCancellationHandler { await work.value } onCancel: { work.cancel() }
        guard !Task.isCancelled, !result.wasCancelled else { return }
        rows = newRows
        matchCount = result.matchCount
        matchedBytes = result.matchedBytes
        notes = result.notes
        isRunning = false
    }

    // MARK: Chip use, measured locally

    /// How often each chip is switched on, kept in this Mac's preferences
    /// only (nothing leaves the machine). `defaults read <bundle id>
    /// FindChipUses` shows the counts.
    static let chipUseKey = "FindChipUses"

    static func recordChipUse(_ id: String, defaults: UserDefaults = .standard) {
        var counts = defaults.dictionary(forKey: chipUseKey) as? [String: Int] ?? [:]
        counts[id, default: 0] += 1
        defaults.set(counts, forKey: chipUseKey)
    }
}

/// An example query: mono query, then what it means.
private struct ExampleRow: View {
    var query: String
    var meaning: String
    var action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: DiskMapSpace.md) {
                Text(query)
                    .font(DiskMapType.figure)
                    .foregroundStyle(DiskMapTheme.ink)
                    .frame(width: 230, alignment: .leading)
                Text(meaning)
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.ink2)
                    .lineLimit(1)
                Spacer(minLength: 0)
                Image(systemName: "arrow.right")
                    .font(.system(size: DiskMapType.scaled(10), weight: .semibold))
                    .foregroundStyle(hovering ? DiskMapTheme.accent : .clear)
            }
            .padding(.horizontal, 16)
            .frame(height: 46)
            .background(hovering ? DiskMapTheme.soft.opacity(0.6) : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityLabel("\(meaning): \(query)")
    }
}

extension FileQuery.Sort {
    /// Find's sort menu.
    var findTitle: String {
        switch self {
        case .largest: return "Largest"
        case .oldest: return "Oldest"
        case .newest: return "Recently changed"
        }
    }
}

extension FileQuery.Sort: Identifiable {
    public var id: String { rawValue }
}
