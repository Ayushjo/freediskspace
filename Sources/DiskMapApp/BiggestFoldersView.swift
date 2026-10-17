import AppKit
import DiskMapCore
import SwiftUI

/// Explore → List → Folders (redesign board 05): what's inside the current
/// folder, biggest first, with what can be freed in each. Double-click opens.
struct BiggestFoldersView: View {
    @ObservedObject var model: ScanModel
    let tree: FileTree
    let rootURL: URL
    var onOpenCleanup: () -> Void = {}

    @State private var query = ""
    @State private var showTechnical = false
    @State private var selectedID: Int32?
    @Environment(\.widthClass) private var widthClass
    @FocusState private var searchFocused: Bool

    private var totals: [Int64] { model.selectedTotals }
    private var parsed: ListQuery { ListQuery.parse(query) }

    private var current: Int32 {
        model.currentNode >= 0 && Int(model.currentNode) < tree.count ? model.currentNode : 0
    }

    private var parentTotal: Int64 {
        Int(current) < totals.count ? max(totals[Int(current)], 1) : 1
    }

    private var rawRows: [(id: Int32, size: Int64)] {
        guard tree.count > 0, totals.count == tree.count else { return [] }
        return tree.children(of: current, totals: totals).sorted { $0.size > $1.size }
    }

    private static let hiddenTechnical: Set<String> = [
        ".vol", ".file", "cores", "dev", "bin", "sbin", "Network", "automount",
        "home", "net",
    ]

    private var atScannedVolumeRoot: Bool {
        current == 0 && (rootURL.path == "/" || rootURL.standardizedFileURL.path == "/")
    }

    private var rows: [(id: Int32, size: Int64)] {
        let q = parsed
        let today = AgeMap.today()
        let hideTechnical = atScannedVolumeRoot && !showTechnical
        return rawRows.filter { row in
            let name = tree.name(of: row.id)
            if hideTechnical, row.size <= 0 || Self.hiddenTechnical.contains(name) { return false }
            if q.isEmpty { return true }
            let abs = tree.path(of: row.id, root: rootURL).path
            let display = CanonicalPath.displayPath(absolutePath: abs).lowercased()
            let day = tree.modifiedDay[Int(row.id)]
            return q.matches(searchText: name.lowercased() + "\n" + display, folderPath: display, size: row.size,
                             kind: tree.isDirectory[Int(row.id)] ? .other : FileKind.classify(fileName: name, path: abs),
                             ageDays: day > 0 ? Int(today - day) : nil)
        }
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
            let shown = rows
            if shown.isEmpty {
                DiskMapEmptyState(
                    symbol: "folder",
                    title: rawRows.isEmpty ? "Nothing with a size in this folder" : "No folders match",
                    message: rawRows.isEmpty ? "Try another folder, or go back up." : "Clear the search or show technical folders."
                )
            } else {
                VStack(spacing: 0) {
                    columnHeader
                    list(shown)
                    footer(shown)
                }
                .padding(.horizontal, widthClass.pagePadding)
            }
            NodeSelectionToolbar(model: model)
        }
        .background(DiskMapTheme.page)
        .task(id: model.catalogGeneration) { await model.ensureCatalog(.reviewables) }
    }

    // MARK: Header and filters

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("Biggest folders").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink).fixedSize()
            Text("\(tree.count > 0 ? tree.name(of: current) : "") · \(ByteFormat.hero(parentTotal))")
                .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                .lineLimit(1)
            if widthClass != .compact {
                Text("What's inside, biggest first. Double-click a folder to open it.")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1).truncationMode(.tail)
            }
            Spacer(minLength: 8)
            ExploreViewSwitch(model: model)
        }
    }

    private var filterRow: some View {
        HStack(spacing: 8) {
            SegmentSwitch(items: [
                .init(title: "Folders", isOn: true) {},
                .init(title: "Files", isOn: false) { model.destination = .biggestFiles },
            ])
            .padding(.trailing, 4)
            Button(action: goBack) { Label("Up", systemImage: "chevron.left") }
                .buttonStyle(IconButtonStyle())
                .disabled(!canGoBack)
                .help("Up to the enclosing folder")
                .accessibilityLabel("Up to the enclosing folder")
            BreadcrumbBar(tree: tree, currentNode: current) { id in open(id) }
            Spacer(minLength: 8)
            if atScannedVolumeRoot {
                ListChip(title: "Show technical", count: nil, isOn: showTechnical) { showTechnical.toggle() }
                    .help("Show zero-size and technical system stubs at the volume root")
            }
        }
    }

    private var searchRow: some View {
        HStack(spacing: 12) {
            ListSearchField(text: $query, tokens: parsed.tokens, matchCount: parsed.isEmpty ? nil : rows.count,
                            placeholder: "Search here, or try size>1GB  changed>1y",
                            focused: $searchFocused)
                .frame(maxWidth: widthClass == .compact ? .infinity : 760)
            Spacer(minLength: 0)
        }
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

    private func list(_ shown: [(id: Int32, size: Int64)]) -> some View {
        let ordered = shown.map(\.id)
        let multi = model.multiSelection
        let index = model.freeableIndex
        let staged = Set(model.stagedItems.map { $0.url.standardizedFileURL.path })
        let today = AgeMap.today()
        return ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(shown, id: \.id) { row in
                    let i = Int(row.id)
                    let isDir = tree.isDirectory[i]
                    let name = tree.name(of: row.id)
                    let abs = tree.path(of: row.id, root: rootURL).path
                    let verdict = StorageClassifier.classify(path: abs, isDirectory: isDir)
                    let contained = index.contains(row.id, tree: tree)
                    let freeBytes = index.bytes(under: row.id, tree: tree, totals: totals)
                    let protected = SafetyClassifier.assess(path: abs, name: name, isDirectory: isDir).level == .protected
                    let kind = isDir ? FileKind.other : FileKind.classify(fileName: name, path: abs)
                    ListFileRow(
                        name: name, place: isDir ? contents(i) : Self.place(abs, rootURL: rootURL),
                        badge: isDir ? "DIR" : BiggestFilesView.badge(name, kind: kind),
                        kind: isDir ? Self.folderKind(verdict.storageClass) : BiggestFilesView.kindText(kind, verdict.storageClass),
                        fraction: Double(row.size) / Double(parentTotal), bytes: row.size,
                        changed: VisualizeView.lastUsed(tree.modifiedDay[i], today: today),
                        note: Self.folderNote(contained: contained, freeBytes: freeBytes, size: row.size, verdict: verdict),
                        accent: contained || (row.size > 0 && Double(freeBytes) / Double(row.size) >= 0.6),
                        staged: staged.contains(URL(fileURLWithPath: abs).standardizedFileURL.path),
                        selected: row.id == selectedID || multi.contains(row.id), columns: columns,
                        select: {
                            selectedID = row.id
                            model.select(row.id, ordered: ordered)
                        },
                        add: protected ? nil : { model.stageRow(path: abs, size: row.size, reason: "Biggest folder: " + name) },
                        open: isDir ? { open(row.id) } : nil
                    )
                    .accessibilityLabel("\(name), \(ByteFormat.string(row.size))")
                    .rowActions(path: abs, stage: protected ? nil : { model.stageRow(path: abs, size: row.size, reason: "Biggest folder: " + name) })
                }
            }
        }
        .listKeyboard(
            ids: ordered, selection: $selectedID,
            path: { tree.path(of: $0, root: rootURL).path },
            stage: { id in
                model.stageRow(path: tree.path(of: id, root: rootURL).path, size: totals[Int(id)],
                               reason: "Biggest folder: " + tree.name(of: id))
            },
            selectAll: { model.multiSelection = Set(ordered) },
            clearSelection: { model.clearMultiSelection() }
        )
        .onChange(of: selectedID) { _, id in if let id { model.selectedNode = id } }
    }

    private func footer(_ shown: [(id: Int32, size: Int64)]) -> some View {
        let index = model.freeableIndex
        let freeBytes = index.bytes(under: current, tree: tree, totals: totals)
        let folders = shown.filter { tree.isDirectory[Int($0.id)] }.count
        return HStack(spacing: 14) {
            (Text("\(Self.footerCount(folders: folders, files: shown.count - folders)) · \(ByteFormat.hero(parentTotal))")
             + Text(freeBytes > 0 ? "  ·  " : "")
             + Text(freeBytes > 0 ? "\(ByteFormat.hero(freeBytes)) can be freed here" : "").foregroundColor(DiskMapTheme.accentDeep))
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer(minLength: 8)
            if freeBytes > 0 {
                Button("Add \(ByteFormat.hero(freeBytes)) to basket") { Task { await stageFreeable() } }
                    .buttonStyle(SecondaryButtonStyle())
            }
        }
        .font(DiskMapType.figure)
        .foregroundStyle(DiskMapTheme.ink2)
        .padding(.vertical, 10)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }

    /// Everything that rebuilds by itself under the current folder, through the bulk-add sheet.
    private func stageFreeable() async {
        let id = current
        let requests: [CleanupStageRequest] = model.cachedReviewables.filter(\.isGenerallySafe).flatMap { t in
            t.nodeIDs.filter { $0 == id || FreeableIndex.isAncestor(id, of: $0, tree: tree) }.map { node in
                CleanupStageRequest(url: tree.path(of: node, root: rootURL), size: Int(node) < totals.count ? totals[Int(node)] : 0,
                                    reason: "Explore: " + t.displayName)
            }
        }
        _ = await model.confirmStageMany(requests, title: "Things that rebuild by themselves in \(tree.name(of: id))")
    }

    // MARK: Words

    private func contents(_ i: Int) -> String {
        let files = model.descendantFileCounts.indices.contains(i) ? model.descendantFileCounts[i] : 0
        let folders = model.descendantFolderCounts.indices.contains(i) ? model.descendantFolderCounts[i] : 0
        return Self.contents(files: files, folders: folders)
    }

    /// "1,204 files · 31 folders".
    static func contents(files: Int, folders: Int) -> String {
        var parts = ["\(files.formatted()) file\(files == 1 ? "" : "s")"]
        if folders > 0 { parts.append("\(folders.formatted()) folder\(folders == 1 ? "" : "s")") }
        return parts.joined(separator: " · ")
    }

    /// "8 folders", "3 folders, 1 file", "2 files".
    static func footerCount(folders: Int, files: Int) -> String {
        var parts: [String] = []
        if folders > 0 { parts.append("\(folders.formatted()) folder\(folders == 1 ? "" : "s")") }
        if files > 0 || folders == 0 { parts.append("\(files.formatted()) file\(files == 1 ? "" : "s")") }
        return parts.joined(separator: ", ")
    }

    static func folderKind(_ storageClass: StorageClass) -> String {
        storageClass.id == "other" ? "Folder" : storageClass.title
    }

    /// The "Can be freed" column for a folder: all of it, part of it, look first, or nothing.
    static func folderNote(contained: Bool, freeBytes: Int64, size: Int64, verdict: StorageVerdict) -> BiggestFilesView.FreeNote {
        if contained { return .accent("\(ByteFormat.hero(size)) rebuilds by itself") }
        if freeBytes > 0 { return .accent("\(ByteFormat.hero(freeBytes)) can be freed") }
        if verdict.advice.isRisky { return .review(verdict.instead) }
        return .none
    }

    private static func place(_ abs: String, rootURL: URL) -> String {
        BiggestFilesView.trimmedPlace(relativeParent(of: abs, root: rootURL))
    }

    // MARK: Navigation

    private var canGoBack: Bool { current > 0 && tree.parent[Int(current)] >= 0 }

    private func goBack() {
        guard canGoBack else { return }
        open(tree.parent[Int(current)])
    }

    private func open(_ id: Int32) {
        guard tree.isDirectory[Int(id)] else { return }
        model.currentNode = id
        selectedID = nil
        model.selectedNode = id
        query = ""
    }
}
