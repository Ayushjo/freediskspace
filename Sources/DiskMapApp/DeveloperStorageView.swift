import AppKit
import DiskMapCore
import SwiftUI

/// Developer (redesign board 06): build files, dependencies and tool caches —
/// by tool, where they live, by project and by item — and what rebuilds by itself.
struct DeveloperStorageView: View {
    @ObservedObject var model: ScanModel
    var pickFolder: () -> Void

    @State private var tableTab: TableTab = .whereItLives
    @State private var selection: Selection?
    /// "Where it lives": the folder being shown; nil = where it opens by itself.
    @State private var whereFolder: Int32?
    @State private var query = ""
    /// Set from a tool tile: the Items tab shows only that tool.
    @State private var ecosystemFilter: DeveloperEcosystem?
    /// The By tool tab (TASK-002 quick wins), built when first opened.
    @State private var toolHits: [QuickWins.Hit]?
    @Environment(\.widthClass) private var widthClass

    private let toolCategories = QuickWins.bundledCategories()

    enum TableTab: String, CaseIterable, Identifiable {
        case whereItLives, projects, byTool, allItems, opportunities
        var id: String { rawValue }
        var title: String {
            switch self {
            case .whereItLives: return "Where it lives"
            case .projects: return "Projects"
            case .byTool: return "By tool"
            case .allItems: return "Every item"
            case .opportunities: return "Biggest wins"
            }
        }
        static let primary: [TableTab] = [.whereItLives, .projects, .byTool]
    }

    /// What the inspector shows.
    enum Selection: Equatable {
        case folder(Int32)
        case project(String)
        case item(String)
        case tool(Int32)
    }

    private var catalog: DeveloperCatalogResult { model.cachedDeveloper }
    private var summary: DeveloperSummary { catalog.summary }
    private var totals: [Int64] { model.selectedTotals }

    var body: some View {
        Group {
            if model.tree == nil {
                DiskMapEmptyState(symbol: "chevron.left.forwardslash.chevron.right", title: "Scan to see developer storage",
                                  message: "Dependencies, caches and build output, after a scan.",
                                  primaryTitle: "Choose Folder…", primaryAction: pickFolder)
            } else {
                HStack(spacing: 0) {
                    mainColumn
                    if widthClass != .compact || selection != nil {
                        Rectangle().fill(DiskMapTheme.line).frame(width: 1)
                        inspector
                            .frame(width: widthClass == .compact ? 300 : widthClass == .regular ? 320 : 360)
                            .background(DiskMapTheme.sidebar)
                    }
                }
            }
        }
        .background(DiskMapTheme.page)
        .catalogGate(.developer, model: model, title: "Sorting developer storage…")
        .onChange(of: model.scanID) { _, _ in whereFolder = nil; selection = nil }
        .onAppear { if let tab = SnapshotHarness.requestedTab.flatMap(TableTab.init(rawValue:)) { tableTab = tab } }
        .task(id: tableTab == .byTool ? model.scanID : nil) {
            guard tableTab == .byTool, let tree = model.tree, let root = model.rootURL else { return }
            toolHits = nil
            let categories = toolCategories
            let found = await Task.detached(priority: .userInitiated) {
                QuickWins.findCategorized(in: tree, root: root, categories: categories)
            }.value
            guard !Task.isCancelled else { return }
            toolHits = found
        }
    }

    private var mainColumn: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: widthClass.gap) {
                header
                notice
                toolTiles
                tabRow
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.top, widthClass == .compact ? 14 : 20)
            .padding(.bottom, 12)
            Group {
                switch tableTab {
                case .whereItLives: whereList
                case .projects: projectsList
                case .allItems: itemsList(filteredItems, title: ecosystemFilter.map { "\($0.title) items" } ?? "Every item")
                case .opportunities: itemsList(catalog.opportunities, title: "Biggest wins")
                case .byTool: toolsList
                }
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.bottom, widthClass.pagePadding)
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: Header

    private var header: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                (Text("Developer").foregroundColor(DiskMapTheme.ink)
                 + Text(" · ").foregroundColor(DiskMapTheme.ink3)
                 + Text(ByteFormat.hero(summary.totalBytes)).foregroundColor(DiskMapTheme.ink))
                    .font(DiskMapType.pageTitle)
                    .lineLimit(1)
                (Text("Build files, dependencies and tool caches. ")
                 + Text("\(ByteFormat.hero(summary.reclaimableBytes)) rebuilds by itself").foregroundColor(DiskMapTheme.accentDeep)
                 + Text(" with one build or install."))
                    .font(DiskMapType.body)
                    .foregroundStyle(DiskMapTheme.ink2)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if widthClass != .compact {
                HStack(spacing: 14) {
                    HStack(spacing: 6) { Hatch(spacing: 4, width: 1.5).frame(width: 12, height: 12).overlay(Rectangle().stroke(DiskMapTheme.accent, lineWidth: 1)); Text("Rebuilds") }
                    HStack(spacing: 6) { Rectangle().fill(DiskMapTheme.ink).frame(width: 12, height: 12); Text("Keep or review") }
                }
                .font(DiskMapType.secondary)
                .foregroundStyle(DiskMapTheme.ink2)
                .padding(.top, 8)
            }
        }
    }

    /// One notice: stale projects, unpinned dependencies, or both.
    @ViewBuilder
    private var notice: some View {
        if summary.staleProjectCount > 0 {
            BoardBanner(
                title: "\(countLabel(summary.staleProjectCount, "project")) untouched for 6+ months "
                    + "\(summary.staleProjectCount == 1 ? "holds" : "hold") \(ByteFormat.hero(summary.staleReclaimableBytes))",
                detail: "Measured from each project’s newest source file. Check Git before removing a whole project."
            )
        } else if summary.unpinnedBytes > 0 {
            BoardBanner(title: "\(ByteFormat.hero(summary.unpinnedBytes)) of dependencies have no lockfile",
                        detail: RebuildCost.networkedUnpinned.explanation)
        }
    }

    // MARK: Tool tiles

    struct Tile: Identifiable, Equatable {
        var ecosystem: DeveloperEcosystem?
        var title: String
        var bytes: Int64
        var rebuildBytes: Int64
        var id: String { ecosystem?.id ?? "more" }
    }

    /// The tools, biggest first; past `limit` they fold into one "N more" tile.
    static func tiles(_ items: [DeveloperItem], limit: Int) -> [Tile] {
        var by: [DeveloperEcosystem: (Int64, Int64)] = [:]
        for item in items {
            var row = by[item.ecosystem] ?? (0, 0)
            row.0 += item.bytes
            if item.reclaimability == .reclaimable { row.1 += item.bytes }
            by[item.ecosystem] = row
        }
        var sorted: [Tile] = []
        for (ecosystem, row) in by where row.0 > 0 {
            sorted.append(Tile(ecosystem: ecosystem, title: ecosystem.title, bytes: row.0, rebuildBytes: row.1))
        }
        sorted.sort { a, b in a.bytes != b.bytes ? a.bytes > b.bytes : a.title < b.title }
        guard sorted.count > limit, limit > 1 else { return sorted }
        let rest = Array(sorted.dropFirst(limit - 1))
        var restBytes: Int64 = 0
        var restRebuild: Int64 = 0
        for tile in rest {
            restBytes += tile.bytes
            restRebuild += tile.rebuildBytes
        }
        let more = Tile(ecosystem: nil, title: "\(rest.count) more", bytes: restBytes, rebuildBytes: restRebuild)
        return Array(sorted.prefix(limit - 1)) + [more]
    }

    private var toolTiles: some View {
        let tiles = Self.tiles(catalog.items, limit: widthClass == .compact ? 5 : widthClass == .regular ? 7 : 9)
        let largest = max(1, tiles.first?.bytes ?? 1)
        return HStack(spacing: 0) {
            ForEach(Array(tiles.enumerated()), id: \.element.id) { index, tile in
                if index > 0 { Rectangle().fill(DiskMapTheme.line).frame(width: 1) }
                ToolTileView(tile: tile, largest: largest, isOn: isTileOn(tile)) { toggleTile(tile) }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func isTileOn(_ tile: Tile) -> Bool {
        tile.ecosystem != nil && ecosystemFilter == tile.ecosystem && tableTab == .allItems
    }

    private func toggleTile(_ tile: Tile) {
        if isTileOn(tile) {
            ecosystemFilter = nil
            tableTab = .whereItLives
        } else {
            ecosystemFilter = tile.ecosystem
            tableTab = .allItems
        }
    }

    // MARK: Tabs

    private var tabRow: some View {
        HStack(spacing: 12) {
            SegmentSwitch(items: TableTab.primary.map { tab in
                .init(title: tab.title, isOn: tableTab == tab) { tableTab = tab; ecosystemFilter = nil }
            })
            Menu {
                ForEach([TableTab.allItems, .opportunities]) { tab in
                    Button { tableTab = tab; if tab == .opportunities { ecosystemFilter = nil } } label: {
                        if tableTab == tab { Label(tab.title, systemImage: "checkmark") } else { Text(tab.title) }
                    }
                }
            } label: {
                Text(TableTab.primary.contains(tableTab) ? "More" : tableTab.title).font(DiskMapType.bodyEmphasis)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            if tableTab == .whereItLives { whereBreadcrumb }
            Spacer(minLength: 8)
            if tableTab != .whereItLives && tableTab != .byTool {
                DiskMapSearchField(placeholder: "Filter by name, path or tool", text: $query)
                    .frame(maxWidth: widthClass == .compact ? 180 : 260)
            }
        }
    }

    // MARK: Where it lives (MAC-FIXES-FROM-WINDOWS §4.3)

    private var developerFolders: DeveloperFolders {
        guard let tree = model.tree else { return .empty }
        return DeveloperCatalog.folderRollup(catalog, tree: tree) { canStage($0) && $0.reclaimability != .keep }
    }

    private func currentWhere(_ folders: DeveloperFolders) -> Int32 {
        whereFolder.flatMap { folders.folders[$0] != nil ? $0 : nil } ?? folders.autoStart()
    }

    @ViewBuilder
    private var whereBreadcrumb: some View {
        let folders = developerFolders
        if let tree = model.tree, let root = model.rootURL, !folders.folders.isEmpty {
            let currentID = currentWhere(folders)
            let chain = tree.ancestorIDs(of: currentID).filter { folders.folders[$0] != nil }
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(Array(chain.enumerated()), id: \.element) { index, id in
                        if index > 0 { Text("›").foregroundStyle(DiskMapTheme.ink3) }
                        Button(id == 0 ? (root.lastPathComponent.isEmpty ? root.path : root.lastPathComponent) : tree.name(of: id)) {
                            whereFolder = id
                            selection = nil
                        }
                        .buttonStyle(.plain)
                        .font(.system(size: DiskMapType.scaled(12.5), weight: id == currentID ? .semibold : .regular, design: .monospaced))
                        .foregroundStyle(id == currentID ? DiskMapTheme.ink : DiskMapTheme.ink2)
                    }
                    if let current = folders.folders[currentID] {
                        Text(ByteFormat.hero(current.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink3)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var whereList: some View {
        let folders = developerFolders
        if let tree = model.tree, !folders.folders.isEmpty, let current = folders.folders[currentWhere(folders)] {
            let currentID = current.id
            let children = current.children.compactMap { folders.folders[$0] }.sorted { $0.removableBytes != $1.removableBytes ? $0.removableBytes > $1.removableBytes : $0.bytes > $1.bytes }
            let outside = catalog.items
                .filter { $0.nodeID != currentID && !FreeableIndex.isAncestor(currentID, of: $0.nodeID, tree: tree) }
                .sorted { $0.bytes > $1.bytes }
            ScrollView {
                VStack(spacing: widthClass.gap) {
                    DevCard {
                        DevSectionHeader(title: "In " + displayName(currentID),
                                         detail: "\(countLabel(current.itemCount, "place")) · \(ByteFormat.hero(current.removableBytes)) removable",
                                         trailing: widthClass == .compact ? "Removable" : "Places · removable") {
                            if current.removableBytes > 0 {
                                Button("Clean all \(ByteFormat.hero(current.removableBytes))") { stageFolder(current) }
                                    .buttonStyle(SecondaryButtonStyle())
                                    .help("Add every removable folder under here to the basket — you'll see the list first")
                            }
                        }
                        ForEach(children.prefix(25), id: \.id) { child in whereRow(child, tree: tree) }
                        if children.count > 25 {
                            DevMoreRow(text: "+ \(countLabel(children.count - 25, "smaller folder"))",
                                       bytes: children.dropFirst(25).reduce(0) { $0 + $1.removableBytes })
                        }
                    }
                    if currentID != 0, !outside.isEmpty {
                        DevCard {
                            DevSectionHeader(title: "Outside " + tree.name(of: currentID), detail: "Shared tool data",
                                             trailing: widthClass == .compact ? "" : "Places · removable") { EmptyView() }
                            ForEach(outside.prefix(8)) { item in itemRow(item) }
                            if outside.count > 8 {
                                DevMoreRow(text: "+ \(countLabel(outside.count - 8, "more item"))", bytes: outside.dropFirst(8).reduce(0) { $0 + $1.bytes }) {
                                    tableTab = .allItems
                                }
                            }
                        }
                    }
                }
            }
        } else {
            DiskMapEmptyState(symbol: "folder", title: "No developer storage found",
                              message: "No dependencies, build output or tool caches in this scan.", dusty: .curious)
        }
    }

    private func whereRow(_ folder: DeveloperFolders.Folder, tree: FileTree) -> some View {
        let item = folder.itemID.flatMap { id in catalog.items.first { $0.id == id } }
        if let item { return AnyView(itemRow(item)) }
        let removable = folder.removableItemIDs.compactMap { id in catalog.items.first { $0.id == id } }.sorted { $0.bytes > $1.bytes }
        let day = Int(folder.id) < tree.count ? tree.modifiedDay[Int(folder.id)] : 0
        return AnyView(DevRow(
            symbol: "folder", name: tree.name(of: folder.id), path: displayPath(folder.id),
            chips: removable.prefix(widthClass == .compact ? 2 : 3).map { DevChip.Model(text: $0.displayName, bytes: $0.bytes, accent: true) },
            note: removable.count > 3 ? "+\(removable.count - 3) more" : nil,
            places: countLabel(folder.itemCount, "place"),
            figure: folder.removableBytes > 0 ? ByteFormat.hero(folder.removableBytes) : ByteFormat.hero(folder.bytes),
            figureAccent: folder.removableBytes > 0,
            subline: "changed " + VisualizeView.lastUsed(day, today: AgeMap.today()).lowercased(),
            action: folder.removableBytes > 0 ? ("Clean", { stageFolder(folder) }) : nil,
            selected: selection == .folder(folder.id), compact: widthClass == .compact,
            select: { selection = .folder(folder.id) },
            open: { whereFolder = folder.id; selection = nil }
        ))
    }

    private func stageFolder(_ folder: DeveloperFolders.Folder) {
        let ids = Set(folder.removableItemIDs)
        stage(catalog.items.filter { ids.contains($0.id) })
    }

    // MARK: Projects

    private var filteredProjects: [DeveloperProject] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !q.isEmpty else { return catalog.projects }
        return catalog.projects.filter {
            $0.name.lowercased().contains(q) || $0.displayPath.lowercased().contains(q) || $0.ecosystem.title.lowercased().contains(q)
        }
    }

    @ViewBuilder
    private var projectsList: some View {
        let projects = Array(filteredProjects.sorted { $0.reclaimableBytes > $1.reclaimableBytes }.prefix(60))
        if projects.isEmpty {
            DiskMapEmptyState(symbol: "folder", title: "No projects found",
                              message: "No project folders with node_modules, Pods, .venv or build output in this scan.", dusty: .curious)
        } else {
            ScrollView {
                DevCard {
                    DevSectionHeader(title: "Projects", detail: "\(countLabel(catalog.projects.count, "project")) · \(ByteFormat.hero(catalog.projects.reduce(0) { $0 + $1.reclaimableBytes })) removable",
                                     trailing: widthClass == .compact ? "Removable" : "Places · removable") { EmptyView() }
                    ForEach(projects) { proj in
                        let items = projectItems(proj)
                        DevRow(
                            symbol: "folder", name: proj.name, path: proj.displayPath,
                            chips: items.prefix(widthClass == .compact ? 2 : 3).map { DevChip.Model(text: $0.displayName, bytes: $0.bytes, accent: $0.reclaimability == .reclaimable) },
                            note: DeveloperLabels.gitShort(proj.git),
                            places: countLabel(proj.itemCount, "place"),
                            figure: ByteFormat.hero(proj.reclaimableBytes > 0 ? proj.reclaimableBytes : proj.bytes),
                            figureAccent: proj.reclaimableBytes > 0,
                            subline: "changed " + DeveloperLabels.lastChange(proj.lastSourceDay).lowercased(),
                            action: proj.reclaimableBytes > 0 ? ("Clean", { stageProject(proj) }) : nil,
                            selected: selection == .project(proj.id), compact: widthClass == .compact,
                            select: { selection = .project(proj.id) }, open: nil
                        )
                    }
                }
            }
        }
    }

    private func projectItems(_ proj: DeveloperProject) -> [DeveloperItem] {
        catalog.items.filter { proj.nodeIDs.contains($0.nodeID) }.sorted { $0.bytes > $1.bytes }
    }

    /// Everything in a project that can go, in one confirmed add.
    private func stageProject(_ proj: DeveloperProject) {
        stage(projectItems(proj).filter { $0.reclaimability != .keep })
    }

    // MARK: Items

    private var filteredItems: [DeveloperItem] {
        var list = catalog.items
        if let ecosystemFilter { list = list.filter { $0.ecosystem == ecosystemFilter } }
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !q.isEmpty {
            list = list.filter {
                $0.displayName.lowercased().contains(q) || $0.displayPath.lowercased().contains(q) || $0.ecosystem.title.lowercased().contains(q)
            }
        }
        return list
    }

    @ViewBuilder
    private func itemsList(_ items: [DeveloperItem], title: String) -> some View {
        let shown = Array(items.prefix(120))
        if shown.isEmpty {
            DiskMapEmptyState(symbol: "shippingbox", title: "Nothing here", message: "No matching developer items.")
        } else {
            let removable = shown.filter { canStage($0) && $0.reclaimability == .reclaimable }
            ScrollView {
                DevCard {
                    DevSectionHeader(title: title,
                                     detail: "\(countLabel(items.count, "item")) · \(ByteFormat.hero(items.reduce(0) { $0 + $1.bytes }))",
                                     trailing: "") {
                        if !removable.isEmpty {
                            Button("Clean what rebuilds · \(ByteFormat.hero(removable.reduce(0) { $0 + $1.bytes }))") { stage(removable) }
                                .buttonStyle(SecondaryButtonStyle())
                        }
                        if ecosystemFilter != nil, tableTab == .allItems {
                            Button("Show every tool") { ecosystemFilter = nil }.buttonStyle(QuietButtonStyle())
                        }
                    }
                    ForEach(shown) { item in itemRow(item) }
                    if items.count > shown.count {
                        DevMoreRow(text: "Largest 120 of \(items.count.formatted()) shown", bytes: nil)
                    }
                }
            }
        }
    }

    private func canStage(_ item: DeveloperItem) -> Bool {
        !item.isProtected && item.recipe?.trashIsUnsafe != true
    }

    private func itemRow(_ item: DeveloperItem) -> some View {
        let unsafe = item.recipe?.trashIsUnsafe == true
        let rebuilds = item.reclaimability == .reclaimable
        let tag: DevChip.Model = rebuilds
            ? .init(text: item.category.shortTitle, bytes: item.bytes, accent: true)
            : .init(text: item.reclaimability == .keep ? "Keep" : "Review", bytes: nil, accent: false)
        return DevRow(
            symbol: item.ecosystem.symbolName, name: item.displayName, path: item.displayPath,
            chips: [tag],
            note: Self.itemNote(item),
            places: "1 place",
            figure: ByteFormat.hero(item.bytes),
            figureAccent: rebuilds && canStage(item),
            subline: rebuilds ? "rebuilds" : item.reclaimability == .keep ? "keep" : "not ticked",
            action: unsafe || item.reclaimability != .reclaimable || item.isProtected
                ? (item.isProtected ? nil : ("Review", { selection = .item(item.id) }))
                : ("Clean", { stage([item]) }),
            selected: selection == .item(item.id), compact: widthClass == .compact,
            select: { selection = .item(item.id) }, open: nil
        )
        .rowActions(path: item.absolutePath, stage: canStage(item) ? { stage([item]) } : nil)
    }

    /// The line after an item's tag: the tool's own way when Trash is wrong, else what rebuilding costs.
    static func itemNote(_ item: DeveloperItem) -> String {
        if let recipe = item.recipe, recipe.trashIsUnsafe { return "Use \(ListFileRow.plain(recipe.command)) instead." }
        return "\(item.ecosystem.title) · \(DeveloperLabels.rebuildShort(item.rebuildCost))"
    }

    // MARK: By tool (former Regenerable Data)

    @ViewBuilder
    private var toolsList: some View {
        if let hits = toolHits {
            if hits.isEmpty {
                DiskMapEmptyState(symbol: "leaf", title: "Nothing regenerable found", message: "No dependency folders, build output or tool caches in this scan.")
            } else {
                ScrollView {
                    VStack(spacing: widthClass.gap) {
                        ForEach(toolCategories) { category in
                            let group = hits.filter { $0.categoryID == category.id }.sorted { toolSize($0.id) > toolSize($1.id) }
                            if !group.isEmpty {
                                DevCard {
                                    DevSectionHeader(title: category.title,
                                                     detail: "\(countLabel(group.count, "place")) · \(ByteFormat.hero(group.reduce(0) { $0 + toolSize($1.id) }))",
                                                     trailing: "") {
                                        Button("Clean all") { stageTools(group.map(\.id), category: category.id) }
                                            .buttonStyle(SecondaryButtonStyle())
                                    }
                                    if !category.note.isEmpty {
                                        Text(category.note).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                                            .fixedSize(horizontal: false, vertical: true)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                            .padding(.horizontal, 20).padding(.vertical, 10)
                                            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
                                    }
                                    ForEach(group.prefix(30)) { hit in
                                        DevRow(symbol: "folder", name: hit.name, path: path(of: hit.id).map { CanonicalPath.displayPath(absolutePath: $0) } ?? "",
                                               chips: [], note: nil, places: "",
                                               figure: ByteFormat.hero(toolSize(hit.id)), figureAccent: true, subline: "rebuilds",
                                               action: ("Clean", { stageTools([hit.id], category: category.id) }),
                                               selected: selection == .tool(hit.id), compact: widthClass == .compact,
                                               select: { selection = .tool(hit.id) }, open: nil)
                                            .rowActions(path: path(of: hit.id) ?? hit.name, stage: { stageTools([hit.id], category: category.id) })
                                    }
                                    if group.count > 30 {
                                        DevMoreRow(text: "+ \(countLabel(group.count - 30, "smaller folder"))", bytes: group.dropFirst(30).reduce(0) { $0 + toolSize($1.id) })
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } else {
            DiskMapLoadingState(title: "Grouping by tool", detail: "Matching folders against the known patterns.")
        }
    }

    private func toolSize(_ id: Int32) -> Int64 { Int(id) < totals.count ? totals[Int(id)] : 0 }

    // MARK: Inspector

    @ViewBuilder
    private var inspector: some View {
        let sel = selection ?? defaultSelection
        switch sel {
        case .folder(let id):
            if let folder = developerFolders.folders[id] { folderPanel(folder) } else { emptyInspector }
        case .project(let id):
            if let proj = catalog.projects.first(where: { $0.id == id }) { projectPanel(proj) } else { emptyInspector }
        case .item(let id):
            if let item = catalog.items.first(where: { $0.id == id }) {
                DeveloperInspector(model: model, item: item, project: catalog.projects.first { $0.id == item.projectKey }) { stage([item]) }
            } else { emptyInspector }
        case .tool(let id):
            if let tree = model.tree, let root = model.rootURL {
                FolderInspector(model: model, tree: tree, rootURL: root, id: id, reason: "Developer: " + tree.name(of: id))
            } else { emptyInspector }
        case nil:
            emptyInspector
        }
    }

    /// Before a click: the biggest thing in the current tab.
    private var defaultSelection: Selection? {
        switch tableTab {
        case .whereItLives:
            let folders = developerFolders
            guard let current = folders.folders[currentWhere(folders)] else { return nil }
            let first = current.children.compactMap { folders.folders[$0] }.max { $0.removableBytes < $1.removableBytes }
            if let first { return first.itemID.map(Selection.item) ?? .folder(first.id) }
            return nil
        case .projects: return catalog.projects.max { $0.reclaimableBytes < $1.reclaimableBytes }.map { .project($0.id) }
        case .allItems: return filteredItems.first.map { .item($0.id) }
        case .opportunities: return catalog.opportunities.first.map { .item($0.id) }
        case .byTool:
            // The top row on screen: the first section's biggest folder.
            guard let hits = toolHits,
                  let first = toolCategories.first(where: { c in hits.contains { $0.categoryID == c.id } }) else { return nil }
            return hits.filter { $0.categoryID == first.id }.max { toolSize($0.id) < toolSize($1.id) }.map { .tool($0.id) }
        }
    }

    private var emptyInspector: some View {
        DiskMapEmptyState(symbol: "shippingbox", title: "Select something", message: "See what it holds and what removing it costs.")
    }

    private func folderPanel(_ folder: DeveloperFolders.Folder) -> some View {
        let items = folder.itemIDs.compactMap { id in catalog.items.first { $0.id == id } }.sorted { $0.bytes > $1.bytes }
        let removable = items.filter { folder.removableItemIDs.contains($0.id) }
        let day = model.tree.map { Int(folder.id) < $0.count ? $0.modifiedDay[Int(folder.id)] : 0 } ?? 0
        let abs = path(of: folder.id)
        return DevPanel(
            label: "Selected folder", name: displayName(folder.id), path: displayPath(folder.id),
            hero: folder.removableBytes > 0 ? folder.removableBytes : folder.bytes, of: folder.removableBytes > 0 ? folder.bytes : nil,
            facts: [("Last changed", DeveloperLabels.lastChange(day)), ("Places", "\(folder.itemCount)")],
            rebuilds: removable.prefix(6).map { ($0.displayName, DeveloperLabels.rebuildShort($0.rebuildCost), $0.bytes) },
            check: removable.isEmpty ? nil : Self.reinstallLine(removable),
            note: Self.folderNote(stale: day > 0 && AgeMap.today() - day > 180),
            revealPath: abs, terminalPath: abs,
            addTitle: folder.removableBytes > 0 ? "Add \(ByteFormat.hero(folder.removableBytes)) to basket" : nil,
            add: { stageFolder(folder) }
        )
    }

    private func projectPanel(_ proj: DeveloperProject) -> some View {
        let items = projectItems(proj)
        let removable = items.filter { $0.reclaimability != .keep && canStage($0) }
        return DevPanel(
            label: "Selected project", name: proj.name, path: proj.displayPath,
            hero: proj.reclaimableBytes > 0 ? proj.reclaimableBytes : proj.bytes, of: proj.reclaimableBytes > 0 ? proj.bytes : nil,
            facts: [("Last changed", DeveloperLabels.lastChange(proj.lastSourceDay)),
                    ("Lockfile", proj.lockfile ?? "none found"),
                    ("Git", proj.git.title)],
            rebuilds: removable.prefix(6).map { ($0.displayName, $0.lockfile ?? DeveloperLabels.rebuildShort($0.rebuildCost), $0.bytes) },
            check: removable.isEmpty ? nil : Self.reinstallLine(removable),
            note: Self.folderNote(stale: proj.lastSourceDay > 0 && AgeMap.today() - proj.lastSourceDay > 180),
            revealPath: proj.absolutePath, terminalPath: proj.absolutePath,
            addTitle: removable.isEmpty ? nil : "Add \(ByteFormat.hero(removable.reduce(0) { $0 + $1.bytes })) to basket",
            add: { stageProject(proj) }
        )
    }

    /// The tick line under "Rebuilds by itself": safe when every dependency is pinned.
    static func reinstallLine(_ items: [DeveloperItem]) -> (title: String, detail: String, ok: Bool) {
        let unpinned = items.filter { $0.rebuildCost == .networkedUnpinned }
        if unpinned.isEmpty {
            let locks = Set(items.compactMap(\.lockfile)).sorted()
            return ("Reinstall is safe.", locks.isEmpty ? "These are build output and caches; the next build makes them again."
                        : "\(locks.joined(separator: " and ")) \(locks.count == 1 ? "is" : "are") here, so the same versions come back.", true)
        }
        return ("Check before removing.", "\(countLabel(unpinned.count, "folder")) \(unpinned.count == 1 ? "has" : "have") no lockfile, so a reinstall may pull newer versions.", false)
    }

    static func folderNote(stale: Bool) -> (title: String, body: String) {
        stale
            ? ("Untouched for over six months", "If you don't work on this any more, removing these costs nothing until you open it again.")
            : ("You worked on this recently", "The next build will take a few minutes longer while the tools put these back.")
    }

    // MARK: Helpers

    private func displayName(_ id: Int32) -> String {
        guard let tree = model.tree, let root = model.rootURL else { return "" }
        if id != 0 { return tree.name(of: id) }
        let display = CanonicalPath.displayPath(absolutePath: root.path)
        return display == "~" || display == "/" ? display : root.lastPathComponent
    }

    private func displayPath(_ id: Int32) -> String {
        path(of: id).map { CanonicalPath.displayPath(absolutePath: $0) } ?? ""
    }

    private func path(of id: Int32) -> String? {
        guard let tree = model.tree, let root = model.rootURL else { return nil }
        return tree.path(of: id, root: root).path
    }

    /// Items whose recipe says Trash would damage the tool are never staged.
    private func stage(_ items: [DeveloperItem]) {
        let allowed = items.filter(canStage)
        if allowed.isEmpty, let recipe = items.first?.recipe, recipe.trashIsUnsafe {
            model.showToast("Use the tool instead: \(recipe.command)")
            return
        }
        Task {
            _ = await model.confirmStageMany(allowed.map {
                CleanupStageRequest(url: URL(fileURLWithPath: $0.absolutePath), size: $0.bytes, reason: "Developer: \($0.displayName)")
            }, title: "Developer")
        }
    }

    private func stageTools(_ ids: [Int32], category: String?) {
        guard let tree = model.tree, let root = model.rootURL else { return }
        Task {
            // Same reason prefix as the other tabs, so the basket groups them together.
            _ = await model.confirmStageMany(ids.map { id in
                let cat = category ?? toolHits?.first { $0.id == id }?.categoryID ?? "dev"
                return CleanupStageRequest(url: tree.path(of: id, root: root), size: toolSize(id), reason: "Developer: \(cat)")
            }, title: "Developer")
        }
    }
}

// MARK: - Pieces

/// One tool in the strip: name, size, and how much of it rebuilds.
private struct ToolTileView: View {
    var tile: DeveloperStorageView.Tile
    var largest: Int64
    var isOn: Bool
    var action: () -> Void
    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 4) {
                Text(tile.title).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                Text(ByteFormat.hero(tile.bytes))
                    .font(.system(size: DiskMapType.scaled(14), weight: .medium, design: .monospaced))
                    .foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                ToolBar(fraction: Double(tile.bytes) / Double(largest),
                        rebuild: tile.bytes > 0 ? Double(tile.rebuildBytes) / Double(tile.bytes) : 0)
                    .frame(height: 6)
                    .padding(.top, 4)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(isOn ? DiskMapTheme.soft : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(tile.ecosystem == nil ? "Smaller tools, together" : "Show only \(tile.title)")
        .accessibilityLabel("\(tile.title), \(ByteFormat.string(tile.bytes)), \(ByteFormat.string(tile.rebuildBytes)) rebuilds by itself")
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}

/// A tool tile's bar: hatched for what rebuilds, ink for the rest.
private struct ToolBar: View {
    var fraction: Double
    var rebuild: Double
    var body: some View {
        GeometryReader { geo in
            let w = max(3, geo.size.width * min(1, fraction))
            ZStack(alignment: .leading) {
                Rectangle().fill(DiskMapTheme.soft)
                HStack(spacing: 0) {
                    Hatch(spacing: 5, width: 2).frame(width: w * rebuild)
                    Rectangle().fill(DiskMapTheme.ink).frame(width: w * (1 - rebuild))
                }
            }
        }
        .accessibilityHidden(true)
    }
}

private struct DevCard<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        VStack(spacing: 0) { content }
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// "IN ~/PROJECTS  14 PROJECTS · 21.4 GB REMOVABLE        PLACES · REMOVABLE".
private struct DevSectionHeader<Actions: View>: View {
    var title: String
    var detail: String
    var trailing: String
    @ViewBuilder var actions: Actions
    var body: some View {
        HStack(spacing: 12) {
            Text(title.uppercased()).foregroundStyle(DiskMapTheme.ink).lineLimit(1).truncationMode(.middle)
            Text(detail.uppercased()).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
            Spacer(minLength: 8)
            actions
            if !trailing.isEmpty { Text(trailing.uppercased()).foregroundStyle(DiskMapTheme.ink3).lineLimit(1) }
        }
        .font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
        .tracking(0.3)
        .padding(.horizontal, 20)
        .frame(height: 44)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
    }
}

/// "DerivedData 4.2" — violet when it rebuilds by itself.
struct DevChip: View {
    struct Model: Hashable {
        var text: String
        var bytes: Int64?
        var accent: Bool
    }
    var model: Model
    var body: some View {
        HStack(spacing: 6) {
            Text(model.text)
            if let bytes = model.bytes { Text(DevChip.short(bytes)) }
        }
        .font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
        .lineLimit(1)
        .foregroundStyle(model.accent ? DiskMapTheme.accentDeep : DiskMapTheme.ink2)
        .padding(.horizontal, 8)
        .frame(height: 22)
        .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(model.accent ? DiskMapTheme.accentWash : Color.clear))
        .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).stroke(model.accent ? DiskMapTheme.accent.opacity(0.35) : DiskMapTheme.line, lineWidth: 1))
        .fixedSize()
    }

    /// "4.2" in GB, "350M" under a gigabyte.
    static func short(_ bytes: Int64) -> String {
        if bytes >= 1_000_000_000 { return String(format: "%.1f", Double(bytes) / 1e9) }
        if bytes >= 1_000_000 { return "\(Int((Double(bytes) / 1e6).rounded()))M" }
        return "\(max(1, Int((Double(bytes) / 1e3).rounded())))K"
    }
}

private struct DevRow: View {
    var symbol: String
    var name: String
    var path: String
    var chips: [DevChip.Model]
    var note: String?
    var places: String
    var figure: String
    var figureAccent: Bool
    var subline: String
    var action: (title: String, run: () -> Void)?
    var selected: Bool
    var compact: Bool
    var select: () -> Void
    var open: (() -> Void)?

    var body: some View {
        HStack(spacing: 14) {
            IconTile(systemName: symbol, size: 36)
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(name).font(.system(size: DiskMapType.scaled(14), weight: .semibold)).foregroundStyle(DiskMapTheme.ink)
                        .lineLimit(1).layoutPriority(1)
                    Text(path).font(.system(size: DiskMapType.scaled(11), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                        .lineLimit(1).truncationMode(.middle)
                }
                if !chips.isEmpty || note != nil {
                    HStack(spacing: 6) {
                        ForEach(chips, id: \.self) { DevChip(model: $0) }
                        if let note { Text(note).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1).truncationMode(.tail) }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if !compact, !places.isEmpty {
                Text(places).font(.system(size: DiskMapType.scaled(11.5), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1).fixedSize()
            }
            VStack(alignment: .trailing, spacing: 2) {
                Text(figure).font(.system(size: DiskMapType.scaled(13), weight: .medium, design: .monospaced))
                    .foregroundStyle(figureAccent ? DiskMapTheme.accentDeep : DiskMapTheme.ink)
                Text(subline).font(.system(size: DiskMapType.scaled(10.5), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
            }
            .lineLimit(1)
            .fixedSize()
            .frame(minWidth: compact ? 70 : 96, alignment: .trailing)
            Group {
                if let action {
                    Button(action.title, action: action.run).buttonStyle(SecondaryButtonStyle())
                } else {
                    Color.clear
                }
            }
            .frame(width: 76, alignment: .trailing)
        }
        .padding(.leading, 18)
        .padding(.trailing, 18)
        .padding(.vertical, 12)
        .background(selected ? DiskMapTheme.accentWash : Color.clear)
        .overlay(alignment: .leading) { if selected { Rectangle().fill(DiskMapTheme.accent).frame(width: 3) } }
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture(perform: select)
        .simultaneousGesture(TapGesture(count: 2).onEnded { open?() })
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(name), \(figure)")
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
        .accessibilityAction(named: "Open") { open?() }
    }
}

private struct DevMoreRow: View {
    var text: String
    var bytes: Int64?
    var action: (() -> Void)? = nil
    var body: some View {
        HStack {
            if let action {
                Button(text, action: action).buttonStyle(.plain).foregroundStyle(DiskMapTheme.ink2)
            } else {
                Text(text).foregroundStyle(DiskMapTheme.ink2)
            }
            Spacer()
            if let bytes, bytes > 0 {
                Text(ByteFormat.hero(bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.accentDeep)
            }
        }
        .font(DiskMapType.body)
        .padding(.leading, 68)
        .padding(.trailing, 18)
        .frame(height: 44)
    }
}

/// The board's "Selected project" panel.
private struct DevPanel: View {
    var label: String
    var name: String
    var path: String
    var hero: Int64
    var of: Int64?
    var facts: [(String, String)]
    var rebuilds: [(name: String, hint: String, bytes: Int64)]
    var check: (title: String, detail: String, ok: Bool)?
    var note: (title: String, body: String)
    var revealPath: String?
    var terminalPath: String?
    var addTitle: String?
    var add: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(label).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                        Text(name).font(.system(size: DiskMapType.scaled(22), weight: .semibold)).foregroundStyle(DiskMapTheme.ink)
                            .lineLimit(2)
                        Text(path).font(.system(size: DiskMapType.scaled(11), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                            .lineLimit(2).truncationMode(.middle).textSelection(.enabled)
                    }
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(ByteFormat.hero(hero)).font(DiskMapType.hero(44))
                            .foregroundStyle(of != nil ? DiskMapTheme.accent : DiskMapTheme.ink)
                            .accessibilityLabel(of.map { "\(ByteFormat.string(hero)) can be freed of \(ByteFormat.string($0))" } ?? ByteFormat.string(hero))
                            .lineLimit(1).minimumScaleFactor(0.6)
                        if let of, of > hero { Text("of \(ByteFormat.hero(of))").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3) }
                    }
                    VStack(spacing: 8) {
                        ForEach(facts, id: \.0) { fact in
                            HStack {
                                Text(fact.0).foregroundStyle(DiskMapTheme.ink2)
                                Spacer(minLength: 8)
                                Text(fact.1).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                            }
                            .font(DiskMapType.body)
                        }
                    }
                    if !rebuilds.isEmpty {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("Rebuilds by itself").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                            VStack(spacing: 0) {
                                ForEach(Array(rebuilds.enumerated()), id: \.offset) { index, row in
                                    HStack(spacing: 8) {
                                        Text(row.name).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                                        Text(row.hint).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
                                        Spacer(minLength: 6)
                                        Text(ByteFormat.hero(row.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                                    }
                                    .padding(.horizontal, 12)
                                    .frame(height: 38)
                                    .overlay(alignment: .top) { if index > 0 { Rectangle().fill(DiskMapTheme.line).frame(height: 1) } }
                                }
                            }
                            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
                            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                        }
                    }
                    if let check {
                        HStack(alignment: .top, spacing: 10) {
                            Image(systemName: check.ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                                .font(.system(size: 16)).foregroundStyle(check.ok ? DiskMapTheme.accent : DiskMapTheme.review)
                            (Text(check.title).fontWeight(.semibold).foregroundColor(DiskMapTheme.ink) + Text(" " + check.detail).foregroundColor(DiskMapTheme.ink2))
                                .font(DiskMapType.body)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text(note.title).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                        Text(note.body).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .overlay(alignment: .leading) {
                        UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10).fill(DiskMapTheme.accent).frame(width: 3)
                    }
                }
                .padding(20)
            }
            VStack(spacing: 10) {
                HStack(spacing: 8) {
                    Button("Reveal") { if let revealPath { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: revealPath)]) } }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                        .disabled(revealPath == nil)
                    Button("Open in Terminal") { if let terminalPath { Self.openTerminal(at: terminalPath) } }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                        .disabled(terminalPath == nil)
                }
                if let addTitle {
                    Button(addTitle, action: add).buttonStyle(AccentButtonStyle(large: true, fullWidth: true, hard: true))
                }
            }
            .padding(20)
        }
    }

    /// Opens Terminal in the folder. Nothing runs; the person types the command.
    static func openTerminal(at path: String) {
        let terminal = URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")
        NSWorkspace.shared.open([URL(fileURLWithPath: path, isDirectory: true)], withApplicationAt: terminal,
                                configuration: NSWorkspace.OpenConfiguration())
    }
}

/// Inspector for one developer item: facts, the tool's own command when
/// Trash is unsafe, why, what removing costs, safety, actions.
private struct DeveloperInspector: View {
    @ObservedObject var model: ScanModel
    let item: DeveloperItem
    let project: DeveloperProject?
    var onStage: () -> Void

    var body: some View {
        let staged = model.isStaged(URL(fileURLWithPath: item.absolutePath))
        let trashUnsafe = item.recipe?.trashIsUnsafe == true
        let rebuilds = item.reclaimability == .reclaimable
        VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Selected item").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                        Text(item.displayName).font(.system(size: DiskMapType.scaled(22), weight: .semibold)).foregroundStyle(DiskMapTheme.ink).lineLimit(2)
                        Text(item.displayPath).font(.system(size: DiskMapType.scaled(11), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                            .lineLimit(2).truncationMode(.middle).textSelection(.enabled)
                    }
                    Text(ByteFormat.hero(item.bytes)).font(DiskMapType.hero(44))
                        .foregroundStyle(rebuilds && !trashUnsafe ? DiskMapTheme.accent : DiskMapTheme.ink)
                        .lineLimit(1).minimumScaleFactor(0.6)
                    VStack(spacing: 8) {
                        fact("Tool", item.ecosystem.title)
                        fact("Rebuild", item.rebuildCost.title)
                        if let lock = item.lockfile { fact("Lockfile", lock) }
                        if let project {
                            fact("Project", project.name)
                            fact("Git", project.git.title)
                            fact("Last source change", DeveloperLabels.lastChange(project.lastSourceDay))
                        } else {
                            fact("Last changed", DeveloperLabels.lastChange(item.modifiedDay))
                        }
                    }
                    if let recipe = item.recipe {
                        VStack(alignment: .leading, spacing: 8) {
                            BoardTag(text: recipe.trashIsUnsafe ? "Don’t move to the Trash" : recipe.title, tone: recipe.trashIsUnsafe ? .ink : .neutral)
                            Text(recipe.why).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                            HStack(spacing: 6) {
                                Text(recipe.command).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).textSelection(.enabled).lineLimit(3)
                                Spacer(minLength: 0)
                                Button {
                                    NSPasteboard.general.clearContents()
                                    NSPasteboard.general.setString(recipe.command, forType: .string)
                                    model.showToast("Command copied — freedisk.space never runs it for you")
                                } label: { Label("Copy command", systemImage: "doc.on.doc") }
                                    .buttonStyle(IconButtonStyle(size: 24))
                                    .accessibilityLabel("Copy command \(recipe.command)")
                            }
                            .padding(10)
                            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.soft))
                        }
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text("What is this?").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                        Text(item.whyLarge + " " + item.rebuildCost.explanation)
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .overlay(alignment: .leading) {
                        UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10).fill(DiskMapTheme.accent).frame(width: 3)
                    }
                    SafetyLine(assessment: item.safety)
                }
                .padding(20)
            }
            VStack(spacing: 10) {
                HStack(spacing: 8) {
                    Button("Reveal") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.absolutePath)]) }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                    Button("Show in Explore") {
                        model.currentNode = item.nodeID
                        model.selectedNode = item.nodeID
                        model.destination = .visualize
                    }
                    .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                }
                if !trashUnsafe && !item.isProtected {
                    Button(staged ? "In the basket" : "Add \(ByteFormat.hero(item.bytes)) to basket") {
                        if staged { model.isCleanupQueuePresented = true } else { onStage() }
                    }
                    .buttonStyle(AccentButtonStyle(large: true, fullWidth: true, hard: !staged))
                }
            }
            .padding(20)
        }
    }

    private func fact(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label).foregroundStyle(DiskMapTheme.ink2)
            Spacer(minLength: 8)
            Text(value).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
        }
        .font(DiskMapType.body)
    }
}

/// Short labels and tints shared by the Developer table and inspector.
enum DeveloperLabels {
    static func rebuildShort(_ cost: RebuildCost) -> String {
        switch cost {
        case .free: return "Free"
        case .cheap: return "Offline build"
        case .networked: return "Re-download"
        case .networkedUnpinned: return "No lockfile"
        }
    }

    static func rebuildTint(_ cost: RebuildCost) -> Color {
        switch cost {
        case .free, .cheap: return DiskMapTheme.safe
        case .networked: return DiskMapTheme.ink2
        case .networkedUnpinned: return DiskMapTheme.review
        }
    }

    static func lastChange(_ day: Int32) -> String {
        guard day > 0 else { return "Unknown" }
        let age = max(0, AgeMap.today() - day)
        if age == 0 { return "Today" }
        if age == 1 { return "Yesterday" }
        return ForgottenAgeFormat.string(age)
    }

    static func gitShort(_ state: GitState) -> String {
        switch state {
        case .inSync: return "Pushed"
        case .noRemote: return "No remote"
        case .differs: return "Unpushed"
        case .notARepository: return "No git"
        case .unknown: return "Unknown"
        }
    }

    static func gitTint(_ state: GitState) -> Color {
        switch state {
        case .inSync: return DiskMapTheme.safe
        case .noRemote, .differs: return DiskMapTheme.review
        case .notARepository, .unknown: return DiskMapTheme.ink2
        }
    }
}
