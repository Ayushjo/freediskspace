import AppKit
import DiskMapCore
import SwiftUI

/// Explore → Visualize: the chart is the page. Modes and colour on one row,
/// the path and folder total on the next, then the canvas.
struct VisualizeView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.diskMapContentWidth) private var contentWidth
    var pickFolder: () -> Void

    @State private var showInspector = true
    @State private var showsLargestItems = false
    @State private var history: [Int32] = [0]
    @State private var historyIndex: Int = 0
    @State private var isMovingThroughHistory = false

    private var totals: [Int64] { model.selectedTotals }
    private var currentID: Int32 { model.currentNode }
    private var canGoBack: Bool { historyIndex > 0 }
    private var canGoForward: Bool { historyIndex + 1 < history.count }

    private var folderBytes: Int64 {
        let i = Int(currentID)
        guard i >= 0, i < totals.count else { return 0 }
        return totals[i]
    }

    private var itemCount: Int {
        let i = Int(currentID)
        let files = model.descendantFileCounts.indices.contains(i) ? model.descendantFileCounts[i] : 0
        let folders = model.descendantFolderCounts.indices.contains(i) ? model.descendantFolderCounts[i] : 0
        return files + folders
    }

    private var largestRows: [(id: Int32, size: Int64)] {
        guard let tree = model.tree, totals.count == tree.count else { return [] }
        return tree.children(of: currentID, totals: totals).filter { $0.size > 0 }.sorted { $0.size > $1.size }
    }

    var body: some View {
        Group {
            if model.tree == nil {
                FirstScanHero(model: model, pickFolder: pickFolder, onScanMac: {
                    Task { await model.scan(FileManager.default.homeDirectoryForCurrentUser) }
                })
            } else if showInspector {
                AdaptiveInspectorSplit(windowWidth: contentWidth, inspectionToken: String(model.selectedNode),
                                       main: mainColumn, inspector: inspector)
            } else {
                mainColumn
            }
        }
        .background(DiskMapTheme.page)
        .onAppear {
            syncHistory()
            if !ExploreViewMode.visualizeModes.contains(model.exploreMode) { model.exploreMode = .treemap }
        }
        .onChange(of: model.currentNode) { _, newValue in recordNavigation(newValue) }
    }

    private var mainColumn: some View {
        GeometryReader { geometry in
            VStack(alignment: .leading, spacing: 12) {
                if let tree = model.tree, let root = model.rootURL, totals.count == tree.count {
                    toolbar(tree: tree)
                    titleRow(tree: tree)
                    ZStack {
                        BoardGrid(step: 20).opacity(0.5)
                        ExploreCanvas(model: model, tree: tree, totals: totals, rootURL: root, hideTreemapChrome: true)
                            .id("\(model.sizeBasis):\(tree.count)")
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .layoutPriority(1)
                    NodeSelectionToolbar(model: model)
                    contentsTable(tree: tree, root: root)
                        .frame(height: max(150, min(geometry.size.height * 0.36, CGFloat(min(largestRows.count, 8)) * rowHeight + 34)))
                } else if model.isScanning {
                    DiskMapLoadingState(title: "Scanning…", detail: "\(model.scannedCount.formatted()) items so far")
                }
            }
            .padding(.horizontal, widthClass.pagePadding)
            .padding(.top, widthClass == .compact ? 12 : 16)
            .padding(.bottom, widthClass.pagePadding)
        }
        .task(id: model.catalogGeneration) { await model.ensureCatalog(.reviewables) }
    }

    @Environment(\.widthClass) private var widthClass
    private var rowHeight: CGFloat { widthClass == .compact ? 34 : 38 }

    private func toolbar(tree: FileTree) -> some View {
        HStack(spacing: 4) {
            Button(action: goBack) { Label("Back", systemImage: "chevron.left") }
                .disabled(!canGoBack).help("Back")
            Button(action: goForward) { Label("Forward", systemImage: "chevron.right") }
                .disabled(!canGoForward).help("Forward")
            BreadcrumbBar(tree: tree, currentNode: currentID) { id in
                model.currentNode = id
                model.selectedNode = id
            }
            .padding(.leading, 6)
            Spacer(minLength: 8)
            modePicker
            if model.exploreMode != .ageMap {
                DiskMapMenu(label: "Color", options: ExploreColorMode.allCases,
                            selection: $model.colorMode, title: { $0.rawValue })
            }
            DiskMapMenu(label: "Size", options: SizeBasis.allCases,
                        selection: $model.sizeBasis, title: { $0 == .allocated ? "On disk" : "Logical" })
            Button {
                withAnimation(.easeInOut(duration: 0.15)) { showInspector.toggle() }
            } label: { Label("Inspector", systemImage: "sidebar.right") }
                .help(showInspector ? "Hide inspector" : "Show inspector")
        }
        .buttonStyle(IconButtonStyle())
    }

    /// Map | Rings | List, with the other views one click further.
    private var modePicker: some View {
        HStack(spacing: 8) {
            HStack(spacing: 2) {
                segment("Map", on: model.exploreMode == .treemap) { model.exploreMode = .treemap }
                segment("Rings", on: model.exploreMode == .sunburst) { model.exploreMode = .sunburst }
                segment("List", on: false) { model.destination = .biggestFiles }
            }
            .padding(2)
            .fixedSize()
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.soft))
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            Menu {
                ForEach([ExploreViewMode.flame, .bubbles, .mindMap, .ageMap]) { mode in
                    Button { model.exploreMode = mode } label: {
                        if model.exploreMode == mode { Label(mode.rawValue, systemImage: "checkmark") } else { Text(mode.rawValue) }
                    }
                }
            } label: {
                Text([.flame, .bubbles, .mindMap, .ageMap].contains(model.exploreMode) ? model.exploreMode.rawValue : "More")
                    .font(DiskMapType.bodyEmphasis)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Flame, Bubbles, Mind Map and Age Map")
        }
        .padding(.trailing, 6)
    }

    private func segment(_ title: String, on: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(DiskMapType.bodyEmphasis)
                .lineLimit(1)
                .foregroundStyle(on ? DiskMapTheme.ink : DiskMapTheme.ink2)
                .padding(.horizontal, 11)
                .frame(height: 24)
                .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(on ? DiskMapTheme.segmentOn : .clear)
                    .shadow(color: .black.opacity(on ? 0.06 : 0), radius: 1, y: 1))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func titleRow(tree: FileTree) -> some View {
        let freeBytes = model.freeableIndex.bytes(under: currentID, tree: tree, totals: totals)
        let parent = currentID > 0 ? tree.parent[Int(currentID)] : -1
        let share = parent >= 0 && Int(parent) < totals.count && totals[Int(parent)] > 0
            ? Double(folderBytes) / Double(totals[Int(parent)]) : nil
        let files = model.descendantFileCounts.indices.contains(Int(currentID)) ? model.descendantFileCounts[Int(currentID)] : 0
        return HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(tree.name(of: currentID))
                .font(.system(size: DiskMapType.scaled(widthClass == .compact ? 22 : 26), weight: .semibold))
                .kerning(-0.6)
                .foregroundStyle(DiskMapTheme.ink)
                .lineLimit(1)
            Text(ByteFormat.string(folderBytes)).font(DiskMapType.figureStrong).foregroundStyle(DiskMapTheme.ink)
            if widthClass != .compact {
                Text("\(files.formatted()) files" + (share.map { " · \(Int(($0 * 100).rounded()))% of \(tree.name(of: parent))" } ?? ""))
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
            }
            Spacer(minLength: 8)
            if model.colorMode == .freeable && model.exploreMode == .treemap {
                HStack(spacing: 7) {
                    Hatch(stripe: DiskMapTheme.accent, ground: DiskMapTheme.accentWash, spacing: 4, width: 1.2)
                        .frame(width: 12, height: 12)
                        .overlay(Rectangle().stroke(DiskMapTheme.accent, lineWidth: 1))
                    Text("Can be freed").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                    Text(ByteFormat.string(freeBytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.accent)
                }
                .help("Things that rebuild by themselves, as in Clean up")
                if widthClass != .compact {
                    HStack(spacing: 7) {
                        Rectangle().fill(DiskMapTheme.card).frame(width: 12, height: 12).overlay(Rectangle().stroke(DiskMapTheme.ink, lineWidth: 1))
                        Text("Your files and app data").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                    }
                    .padding(.leading, 8)
                }
            }
        }
    }

    /// What's in this folder, biggest first: the map's legend as a table.
    private func contentsTable(tree: FileTree, root: URL) -> some View {
        let rows = largestRows
        let largest = max(1, rows.first?.size ?? 1)
        let index = model.freeableIndex
        let today = AgeMap.today()
        return VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text("Name").frame(maxWidth: .infinity, alignment: .leading)
                Text("").frame(width: widthClass == .compact ? 80 : 120)
                Text("Size ↓").frame(width: 84, alignment: .trailing)
                Text("Last used").frame(width: 92, alignment: .leading).padding(.leading, 14)
                Text("Can be freed").frame(width: 96, alignment: .leading)
                if widthClass != .compact { Text("What it is").frame(maxWidth: .infinity, alignment: .leading) }
            }
            .font(DiskMapType.secondary)
            .foregroundStyle(DiskMapTheme.ink3)
            .padding(.horizontal, 10)
            .frame(height: 30)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(rows, id: \.id) { row in
                        let free = index.bytes(under: row.id, tree: tree, totals: totals)
                        let isDir = tree.isDirectory[Int(row.id)]
                        let abs = tree.path(of: row.id, root: root).path
                        let rebuilds = index.contains(row.id, tree: tree)
                        ExploreTableRow(
                            name: tree.name(of: row.id), isDirectory: isDir, bytes: row.size,
                            fraction: Double(row.size) / Double(largest), freeFraction: row.size > 0 ? Double(free) / Double(row.size) : 0,
                            lastUsed: Self.lastUsed(tree.modifiedDay[Int(row.id)], today: today),
                            freeable: free, what: Self.whatItIs(rebuilds: rebuilds, storageClass: StorageClassifier.classify(path: abs, isDirectory: isDir).storageClass),
                            whatIsAccent: rebuilds, selected: row.id == model.selectedNode, height: rowHeight,
                            showWhat: widthClass != .compact, barWidth: widthClass == .compact ? 80 : 120
                        ) {
                            model.selectedNode = row.id
                        } open: {
                            if isDir { model.currentNode = row.id } else { model.selectedNode = row.id }
                        }
                    }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Contents of \(tree.name(of: currentID))")
    }

    /// The table's "What it is": the storage class, nothing for "Other".
    static func whatItIs(rebuilds: Bool, storageClass: StorageClass) -> String {
        if rebuilds { return "Rebuilds by itself" }
        return storageClass.id == "other" ? "—" : storageClass.title
    }

    static func lastUsed(_ day: Int32, today: Int32) -> String {
        guard day > 0 else { return "—" }
        let age = Int(today - day)
        if age <= 0 { return "Today" }
        if age == 1 { return "Yesterday" }
        if age < 7 { return "\(age) days ago" }
        if age < 60 { return "\(age / 7) week\(age / 7 == 1 ? "" : "s") ago" }
        if age < 730 { return "\(age / 30) months ago" }
        return "\(age / 365) years ago"
    }

    private var inspector: some View {
        Group {
            if let tree = model.tree, let root = model.rootURL,
               model.selectedNode >= 0, Int(model.selectedNode) < tree.count {
                let id = model.selectedNode
                if tree.isDirectory[Int(id)] {
                    FolderInspector(model: model, tree: tree, rootURL: root, id: id, reason: "Visualize: " + tree.name(of: id))
                } else {
                    FileInspector(model: model, tree: tree, rootURL: root, id: id,
                                  size: Int(id) < totals.count ? totals[Int(id)] : 0,
                                  reason: "Visualize: " + tree.name(of: id))
                }
            } else {
                DiskMapEmptyState(symbol: "square.grid.2x2", title: "Select an item", message: "Click a tile to inspect it.")
            }
        }
        .background(DiskMapTheme.page)
    }

    // MARK: History

    private func syncHistory() {
        history = [model.currentNode]
        historyIndex = 0
    }

    private func recordNavigation(_ id: Int32) {
        if isMovingThroughHistory { isMovingThroughHistory = false; return }
        if historyIndex < history.count - 1 { history = Array(history.prefix(historyIndex + 1)) }
        if history.last != id { history.append(id) }
        historyIndex = history.count - 1
    }

    private func goBack() {
        guard canGoBack else { return }
        isMovingThroughHistory = true
        historyIndex -= 1
        model.currentNode = history[historyIndex]
        model.selectedNode = history[historyIndex]
    }

    private func goForward() {
        guard canGoForward else { return }
        isMovingThroughHistory = true
        historyIndex += 1
        model.currentNode = history[historyIndex]
        model.selectedNode = history[historyIndex]
    }
}

/// One row of Explore's contents table.
private struct ExploreTableRow: View {
    var name: String
    var isDirectory: Bool
    var bytes: Int64
    var fraction: Double
    var freeFraction: Double
    var lastUsed: String
    var freeable: Int64
    var what: String
    var whatIsAccent: Bool
    var selected: Bool
    var height: CGFloat
    var showWhat: Bool
    var barWidth: CGFloat
    var select: () -> Void
    var open: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 10) {
            Text(name).font(selected ? DiskMapType.bodyEmphasis : DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                .lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
            ZStack(alignment: .leading) {
                Rectangle().fill(DiskMapTheme.soft)
                Rectangle().fill(DiskMapTheme.ink).frame(width: barWidth * CGFloat(min(1, fraction)))
                Rectangle().fill(DiskMapTheme.accent).frame(width: barWidth * CGFloat(min(1, fraction * freeFraction)))
            }
            .frame(width: barWidth, height: 5)
            Text(ByteFormat.string(bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).frame(width: 84, alignment: .trailing)
            Text(lastUsed).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).frame(width: 92, alignment: .leading).padding(.leading, 14)
            Text(freeable > 0 ? ByteFormat.string(freeable) : "—").font(DiskMapType.figure)
                .foregroundStyle(freeable > 0 ? DiskMapTheme.accent : DiskMapTheme.ink3).frame(width: 96, alignment: .leading)
            if showWhat {
                Text(what).font(DiskMapType.body).foregroundStyle(whatIsAccent ? DiskMapTheme.accentDeep : DiskMapTheme.ink2)
                    .lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(.horizontal, 10)
        .frame(height: height)
        .background(selected ? DiskMapTheme.accentWash : hovering ? DiskMapTheme.hover : Color.clear)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.6)).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture(count: 2, perform: open)
        .simultaneousGesture(TapGesture().onEnded(select))
        .onHover { hovering = $0 }
        .help(isDirectory ? "Double-click to open" : name)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(name), \(ByteFormat.string(bytes))\(freeable > 0 ? ", \(ByteFormat.string(freeable)) can be freed" : "")")
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
        .accessibilityAction(named: "Open", open)
    }
}
