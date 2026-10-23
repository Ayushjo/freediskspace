import AppKit
import DiskMapCore
import SwiftUI

/// The chart canvas Visualize shows: one view per mode, sharing selection.
struct ExploreCanvas: View {
    @ObservedObject var model: ScanModel
    let tree: FileTree
    let totals: [Int64]
    let rootURL: URL
    /// When true, Treemap omits its internal breadcrumb (VisualizeView owns chrome).
    var hideTreemapChrome: Bool = false

    private var otherFraction: Double {
        // Map depth slider 1…12 onto a collapse fraction (shallower → more Other).
        let t = (12 - model.depthLevel) / 11
        return max(0.001, 0.002 + t * 0.04)
    }

    /// Space in a chart (TASK-085).
    private func quickLook(_ id: Int32) {
        guard id >= 0, Int(id) < tree.count else { return }
        DiskMapQuickLook.shared.show(tree.path(of: id, root: rootURL))
    }

    /// Clicks in any chart go through `select`, which reads ⌘ and ⇧.
    private var chartSelection: Binding<Int32> {
        Binding(get: { model.selectedNode }, set: { model.select($0) })
    }

    var body: some View {
        Group {
            switch model.exploreMode {
            case .treemap:
                ExploreTreemapView(
                    tree: tree,
                    totals: totals,
                    currentNode: $model.currentNode,
                    selectedNode: chartSelection,
                    colorMode: model.colorMode,
                    categories: model.fileTypeCategories,
                    freeable: model.freeableIndex,
                    showInlineChrome: !hideTreemapChrome,
                    onQuickLook: quickLook,
                    onAddToSelection: { model.select($0, modifiers: .command) }
                )
                .onChange(of: model.currentNode) { _, v in model.selectedNode = v }
            case .sunburst:
                LayoutChartView(kind: .sunburst, tree: tree, totals: totals, currentNode: $model.currentNode, selectedNode: chartSelection, otherFraction: otherFraction, colorMode: model.colorMode, categories: model.fileTypeCategories, freeable: model.freeableIndex, onQuickLook: quickLook, onAddToSelection: { model.select($0, modifiers: .command) })
                    .onChange(of: model.currentNode) { _, v in model.selectedNode = v }
            case .flame:
                LayoutChartView(kind: .flame, tree: tree, totals: totals, currentNode: $model.currentNode, selectedNode: chartSelection, otherFraction: otherFraction, colorMode: model.colorMode, categories: model.fileTypeCategories, freeable: model.freeableIndex, onQuickLook: quickLook, onAddToSelection: { model.select($0, modifiers: .command) })
                    .onChange(of: model.currentNode) { _, v in model.selectedNode = v }
            case .bubbles:
                LayoutChartView(kind: .bubbles, tree: tree, totals: totals, currentNode: $model.currentNode, selectedNode: chartSelection, otherFraction: otherFraction, colorMode: model.colorMode, categories: model.fileTypeCategories, freeable: model.freeableIndex, onQuickLook: quickLook, onAddToSelection: { model.select($0, modifiers: .command) })
                    .onChange(of: model.currentNode) { _, v in model.selectedNode = v }
            case .mindMap:
                LayoutChartView(kind: .mindMap, tree: tree, totals: totals, currentNode: $model.currentNode, selectedNode: chartSelection, otherFraction: otherFraction, colorMode: model.colorMode, categories: model.fileTypeCategories, freeable: model.freeableIndex, onQuickLook: quickLook, onAddToSelection: { model.select($0, modifiers: .command) })
                    .onChange(of: model.currentNode) { _, v in model.selectedNode = v }
            case .ageMap:
                AgeMapView(model: model, tree: tree, totals: totals, rootURL: rootURL)
            }
        }
        .environment(\.multiSelection, model.multiSelection)
    }
}


/// Treemap canvas with selection. "Can be freed" draws the redesign's
/// outlined two-level map; the other modes keep their coloured tiles.
struct ExploreTreemapView: View {
    let tree: FileTree
    let totals: [Int64]
    @Binding var currentNode: Int32
    @Binding var selectedNode: Int32
    var colorMode: ExploreColorMode
    var categories: [FileTypeCategory]
    var freeable: FreeableIndex = .empty
    /// When false, breadcrumbs/size chrome are provided by VisualizeView.
    var showInlineChrome: Bool = true
    var onQuickLook: (Int32) -> Void = { _ in }
    var onAddToSelection: (Int32) -> Void = { _ in }

    @Environment(\.multiSelection) private var multi
    @State private var layoutRects: [TreemapRect] = []
    /// Two-level map: each big tile's own children, and its "+ more" remainder.
    @State private var childRects: [Int32: [TreemapRect]] = [:]
    @State private var moreRects: [Int32: (rect: CGRect, bytes: Int64)] = [:]
    @State private var canvasSize: CGSize = .zero
    @State private var layoutTask: Task<Void, Never>?
    @State private var isPreparingLayout = false
    @State private var hoveredID: Int32?
    /// "By type" colours for the folders on screen, worked out with the layout.
    @State private var folderTypeHex: [Int32: String] = [:]

    private var blueprint: Bool { colorMode == .freeable }

    /// Header strip and padding inside a big tile that holds its children.
    static let headerHeight: CGFloat = 24
    static let innerPadding: CGFloat = 6

    var body: some View {
        VStack(spacing: 0) {
            if showInlineChrome {
                HStack {
                    BreadcrumbBar(tree: tree, currentNode: currentNode) { id in
                        currentNode = id
                        selectedNode = id
                        cacheLayout()
                    }
                    Spacer()
                    Text(ByteCountFormatter.string(fromByteCount: currentSize, countStyle: .file))
                        .foregroundStyle(DiskMapTheme.ink2)
                }
                .padding(8)
            }

            Canvas { context, size in
                if blueprint { drawBlueprint(&context) } else { drawTiles(&context) }
            }
            .onContinuousHover { phase in
                switch phase {
                case .active(let point): hoveredID = hit(at: point)
                case .ended: hoveredID = nil
                }
            }
            .help(hoveredID.map { "\(tree.name(of: $0)) — \(ByteFormat.string(totals[Int($0)]))\(freeable.contains($0, tree: tree) ? " · can be freed" : "")" } ?? "")
            .overlay {
                if isPreparingLayout && layoutRects.isEmpty {
                    ProgressView("Preparing map…")
                        .controlSize(.small)
                        .foregroundStyle(DiskMapTheme.ink2)
                }
            }
            .background {
                GeometryReader { proxy in
                    Color.clear
                        .onAppear { cacheLayout(in: proxy.size) }
                        .onChange(of: proxy.size) { _, s in cacheLayout(in: s) }
                }
            }
            .gesture(
                SpatialTapGesture(count: 2).onEnded { event in
                    guard let hit = hit(at: event.location) else { return }
                    if tree.isDirectory[Int(hit)] {
                        currentNode = hit   // the shell selects the opened folder
                        cacheLayout()
                    } else {
                        selectedNode = hit
                    }
                }
            )
            .simultaneousGesture(
                SpatialTapGesture().onEnded { event in
                    guard let hit = hit(at: event.location) else { return }
                    selectedNode = hit
                }
            )
            .chartAccessibility(
                "Treemap of \(currentNode >= 0 && Int(currentNode) < tree.count ? tree.name(of: currentNode) : "this folder")",
                entries: ChartAccessibility.entries(
                    layoutRects.map { .init(id: $0.id, name: tree.name(of: $0.id), size: totals[Int($0.id)],
                                            drillable: tree.isDirectory[Int($0.id)]) },
                    total: currentSize, format: ByteFormat.string),
                frame: { id in layoutRects.first { $0.id == id }?.rect },
                select: { selectedNode = $0 },
                open: { id in
                    currentNode = id
                    cacheLayout()
                }
            )
            .chartKeyboard(
                move: { direction in
                    let tiles = layoutRects.map { (id: $0.id, rect: $0.rect) }
                    let from: Int32? = tiles.contains { $0.id == selectedNode } ? selectedNode : nil
                    let next = ChartNavigation.neighbor(of: from, toward: direction, in: tiles)
                    if let next { selectedNode = next }
                },
                open: {
                    guard layoutRects.contains(where: { $0.id == selectedNode }), tree.isDirectory[Int(selectedNode)] else { return }
                    currentNode = selectedNode
                    cacheLayout()
                },
                enclosing: {
                    guard currentNode > 0 else { return }
                    currentNode = tree.parent[Int(currentNode)]
                    cacheLayout()
                },
                quickLook: { onQuickLook(selectedNode) },
                addToSelection: { onAddToSelection(selectedNode) }
            )
        }
        .onChange(of: currentNode) { _, _ in cacheLayout() }
        .onChange(of: colorMode) { _, _ in cacheLayout() }
        .onChange(of: totals.count) { _, _ in cacheLayout() }
        .onDisappear { layoutTask?.cancel() }
    }

    // MARK: Drawing

    /// The coloured tiles of By folder / By type / By age.
    private func drawTiles(_ context: inout GraphicsContext) {
        for r in layoutRects {
            let inset = r.rect.insetBy(dx: 1.5, dy: 1.5)
            guard inset.width > 0.5, inset.height > 0.5 else { continue }
            let path = Path(roundedRect: inset, cornerRadius: min(6, min(inset.width, inset.height) / 3), style: .continuous)
            let selected = r.id == selectedNode || multi.contains(r.id)
            let hovered = r.id == hoveredID
            context.fill(path, with: .color(colorFor(id: r.id)))
            context.fill(path, with: .linearGradient(
                Gradient(colors: [.white.opacity(hovered ? 0.28 : 0.14), .white.opacity(hovered ? 0.12 : 0)]),
                startPoint: CGPoint(x: inset.midX, y: inset.minY),
                endPoint: CGPoint(x: inset.midX, y: inset.maxY)))
            if selected {
                context.stroke(path, with: .color(DiskMapTheme.accent), lineWidth: 2)
            }
            if inset.width > 52 && inset.height > 22 {
                context.draw(
                    Text(String(tree.name(of: r.id).prefix(max(4, Int(inset.width / 7) - 3))))
                        .font(.system(size: DiskMapType.scaled(12), weight: .medium))
                        .foregroundStyle(DiskMapTheme.tileLabel.opacity(0.85)),
                    at: CGPoint(x: inset.minX + 8, y: inset.minY + 7),
                    anchor: .topLeading
                )
            }
            if inset.width > 80 && inset.height > 46 {
                context.draw(Text(ByteFormat.string(totals[Int(r.id)]))
                    .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.tileLabel.opacity(0.6)),
                    at: CGPoint(x: inset.minX + 8, y: inset.minY + 25), anchor: .topLeading)
            }
        }
    }

    /// Outlined boxes drawn to size, each holding its biggest children;
    /// violet stripes for what can be freed (redesign board 03).
    private func drawBlueprint(_ context: inout GraphicsContext) {
        let outline = DiskMapTheme.ink.opacity(0.88)
        let innerLine = DiskMapTheme.ink3.opacity(0.75)
        for r in layoutRects {
            let box = r.rect.insetBy(dx: 2, dy: 2)
            guard box.width > 1, box.height > 1 else { continue }
            let path = Path(box)
            let free = mostlyFreeable(r.id)
            if free { HatchFill.draw(path, in: box, context: &context) }
            else { context.fill(path, with: .color(r.id == hoveredID && childRects[r.id] == nil ? DiskMapTheme.accentWash : DiskMapTheme.card)) }
            context.stroke(path, with: .color(free ? DiskMapTheme.accent : outline), lineWidth: 1)
            label(&context, name: tree.name(of: r.id), bytes: totals[Int(r.id)], in: box, strong: true, tint: free ? DiskMapTheme.accentDeep : DiskMapTheme.ink, onHatch: free)
            for c in childRects[r.id] ?? [] {
                let cell = c.rect.insetBy(dx: 2, dy: 2)
                guard cell.width > 1, cell.height > 1 else { continue }
                let cpath = Path(cell)
                let cfree = free || mostlyFreeable(c.id)
                if cfree { HatchFill.draw(cpath, in: cell, context: &context) }
                else { context.fill(cpath, with: .color(c.id == hoveredID ? DiskMapTheme.accentWash : DiskMapTheme.card)) }
                context.stroke(cpath, with: .color(cfree ? DiskMapTheme.accent : innerLine), lineWidth: 1)
                if cell.width > 58 && cell.height > 22 {
                    label(&context, name: tree.name(of: c.id), bytes: totals[Int(c.id)], in: cell, strong: false, tint: cfree ? DiskMapTheme.accentDeep : DiskMapTheme.ink, onHatch: cfree)
                }
                if c.id == selectedNode || multi.contains(c.id) {
                    context.stroke(Path(cell.insetBy(dx: 0.5, dy: 0.5)), with: .color(DiskMapTheme.accent), lineWidth: 2.5)
                }
            }
            if let more = moreRects[r.id] {
                let cell = more.rect.insetBy(dx: 2, dy: 2)
                if cell.width > 1, cell.height > 1 {
                    context.fill(Path(cell), with: .color(DiskMapTheme.card))
                    context.stroke(Path(cell), with: .color(innerLine), lineWidth: 1)
                    if cell.width > 58 && cell.height > 22 {
                        label(&context, name: "+ more", bytes: more.bytes, in: cell, strong: false, tint: DiskMapTheme.ink3)
                    }
                }
            }
            if r.id == selectedNode || multi.contains(r.id) {
                context.stroke(Path(box.insetBy(dx: 0.5, dy: 0.5)), with: .color(DiskMapTheme.accent), lineWidth: 2.5)
            }
        }
    }

    /// Striped when it is inside a Clean up item, or most of it can be freed.
    private func mostlyFreeable(_ id: Int32) -> Bool { freeable.isMostly(id, tree: tree, totals: totals) }

    /// "Name 12.4 GB" in the top-left corner, cut to fit.
    private func label(_ context: inout GraphicsContext, name: String, bytes: Int64, in rect: CGRect, strong: Bool, tint: Color, onHatch: Bool = false) {
        guard rect.width > 40, rect.height > 18 else { return }
        let size = ByteFormat.string(bytes)
        let available = rect.width - 14
        let nameFont = Font.system(size: DiskMapType.scaled(12), weight: strong ? .semibold : .medium)
        let sizeFont = Font.system(size: DiskMapType.scaled(11), design: .monospaced)
        let showSize = available > CGFloat(name.count + size.count) * 6.6 + 10
        let maxChars = max(3, Int(available / 6.9))
        let shown = name.count > maxChars ? String(name.prefix(maxChars - 1)) + "…" : name
        var text = Text(shown).font(nameFont).foregroundColor(tint)
        if showSize { text = text + Text("  " + size).font(sizeFont).foregroundColor(onHatch ? DiskMapTheme.accentDeep.opacity(0.8) : DiskMapTheme.ink3) }
        let resolved = context.resolve(text)
        if onHatch {
            // A solid pill keeps the label readable over the stripes.
            let measured = resolved.measure(in: CGSize(width: available, height: 40))
            let pill = CGRect(x: rect.minX + 4, y: rect.minY + 3, width: min(available + 6, measured.width + 6), height: measured.height + 4)
            context.fill(Path(roundedRect: pill, cornerRadius: 3), with: .color(DiskMapTheme.card))
        }
        context.draw(resolved, at: CGPoint(x: rect.minX + 7, y: rect.minY + 5), anchor: .topLeading)
    }

    /// The deepest tile under the point: a child when the map shows one.
    private func hit(at point: CGPoint) -> Int32? {
        guard let top = SquarifiedTreemap.hitTest(layoutRects, at: point) else { return nil }
        if blueprint, let kids = childRects[top], let child = SquarifiedTreemap.hitTest(kids, at: point) { return child }
        return top
    }

    private var currentSize: Int64 {
        guard currentNode >= 0, Int(currentNode) < totals.count else { return 0 }
        return totals[Int(currentNode)]
    }

    // MARK: Layout

    private func cacheLayout(in size: CGSize? = nil) {
        if let size, size.width > 0, size.height > 0 { canvasSize = size }
        guard canvasSize.width > 0, canvasSize.height > 0 else {
            layoutRects = []
            return
        }
        let node = currentNode
        let targetSize = canvasSize
        let sourceTree = tree
        let sourceTotals = totals
        layoutTask?.cancel()
        isPreparingLayout = true
        let byType = colorMode == .type
        let nested = colorMode == .freeable
        let sourceCategories = categories
        layoutTask = Task.detached(priority: .userInitiated) {
            let items = sourceTree.children(of: node, totals: sourceTotals).filter { $0.size > 0 }
            guard !Task.isCancelled else { return }
            let rects = SquarifiedTreemap.layout(
                items: items,
                in: CGRect(origin: .zero, size: targetSize)
            )
            guard !Task.isCancelled else { return }
            let typeHex = byType
                ? ExploreColoring.dominantTypeHex(for: rects.map(\.id), in: sourceTree, totals: sourceTotals, categories: sourceCategories)
                : [:]
            let inner = nested ? Self.nestedLayout(rects, tree: sourceTree, totals: sourceTotals) : ([:], [:])
            guard !Task.isCancelled else { return }
            await MainActor.run {
                guard currentNode == node, canvasSize == targetSize else { return }
                layoutRects = rects
                childRects = inner.0
                moreRects = inner.1
                folderTypeHex = typeHex
                isPreparingLayout = false
            }
        }
    }

    /// Children inside each big enough folder tile: the biggest few, and the
    /// rest as one "+ more" cell, so slivers never fill the map.
    nonisolated static func nestedLayout(_ rects: [TreemapRect], tree: FileTree, totals: [Int64])
        -> ([Int32: [TreemapRect]], [Int32: (rect: CGRect, bytes: Int64)]) {
        var kids: [Int32: [TreemapRect]] = [:]
        var more: [Int32: (rect: CGRect, bytes: Int64)] = [:]
        for r in rects where tree.isDirectory[Int(r.id)] {
            let box = r.rect.insetBy(dx: 2, dy: 2)
            let inner = CGRect(x: box.minX + innerPadding, y: box.minY + headerHeight,
                               width: box.width - innerPadding * 2, height: box.height - headerHeight - innerPadding)
            guard inner.width >= 60, inner.height >= 40 else { continue }
            let children = tree.children(of: r.id, totals: totals).filter { $0.size > 0 }.sorted { $0.size > $1.size }
            guard !children.isEmpty else { continue }
            let total = max(1, children.reduce(Int64(0)) { $0 + $1.size })
            var shown: [(id: Int32, size: Int64)] = []
            var running: Int64 = 0
            for c in children {
                if shown.count >= 9 || (shown.count >= 2 && Double(running) / Double(total) > 0.9) { break }
                shown.append(c)
                running += c.size
            }
            let rest = total - running
            let items = shown + (rest > 0 ? [(id: Int32(-1), size: rest)] : [])
            let laid = SquarifiedTreemap.layout(items: items, in: inner)
            kids[r.id] = laid.filter { $0.id >= 0 }
            if let m = laid.first(where: { $0.id == -1 }) { more[r.id] = (m.rect, rest) }
        }
        return (kids, more)
    }

    private func colorFor(id: Int32) -> Color {
        ExploreColoring.color(for: id, in: tree, mode: colorMode, categories: categories, folderTypeHex: folderTypeHex)
    }
}
