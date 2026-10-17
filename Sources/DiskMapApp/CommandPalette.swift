import AppKit
import SwiftUI
import DiskMapCore

struct CommandPalette: View {
    @ObservedObject var model: ScanModel
    @Binding var isPresented: Bool
    @State private var query: String
    @State private var searchHits: [SearchHit] = []
    @State private var isSearching = false
    @State private var queryMatchCount = 0
    @State private var queryMatchedBytes: Int64 = 0
    /// Keyboard highlight: ↑/↓ move it, Return runs it.
    @State private var highlighted = 0
    @FocusState private var fieldFocused: Bool
    var onReviewCleanup: () -> Void
    var onExplain: () -> Void

    init(model: ScanModel, isPresented: Binding<Bool>, initialQuery: String = "", onReviewCleanup: @escaping () -> Void, onExplain: @escaping () -> Void) {
        self.model = model
        self._isPresented = isPresented
        self._query = State(initialValue: initialQuery)
        self.onReviewCleanup = onReviewCleanup
        self.onExplain = onExplain
    }

    private enum Category: String, CaseIterable { case folders = "Folders", applications = "Applications", files = "Files", actions = "Actions" }

    private struct SearchHit: Identifiable, Sendable {
        var id: Int32 { nodeID }
        var nodeID: Int32
        var parentID: Int32
        var name: String
        var path: String
        var isDirectory: Bool
        var isApplication: Bool
        var bytes: Int64
    }

    /// TASK-059: the palette speaks the Find query language. Anything with a
    /// `key:` or `size>`-style token is a query, not a command search.
    private var parsedQuery: FileQuery.Parsed? {
        guard let root = model.rootURL else { return nil }
        return FileQuery.parse(query, home: NSHomeDirectory(), root: root.path)
    }

    private var isStructuredQuery: Bool { parsedQuery?.query.isStructured ?? false }

    private func openInFind() {
        model.findQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        model.destination = .find
    }

    private struct Command: Identifiable {
        var id: String { title + subtitle }
        var title: String
        var subtitle: String
        var symbol: String
        var category: Category = .actions
        /// Right-hand figure or shortcut ("80.2 GB", "⌘R").
        var trailing: String? = nil
        var trailingAccent = false
        /// File hits: the node, for Show in Finder and Add to basket.
        var hit: SearchHit? = nil
        var kindTag: String? = nil
        var run: () -> Void
    }

    private var commands: [Command] {
        let safe = model.safeReclaimableBytes
        let dev = model.cachedDeveloper.summary.totalBytes
        var list: [Command] = [
            Command(title: safe > 0 ? "Clean up \(ByteFormat.hero(safe))" : "Clean up", subtitle: "Caches and build files that rebuild by themselves",
                    symbol: "trash", trailing: CommandPalette.shortcut(for: .cleanSafe), trailingAccent: true) { model.destination = .cleanSafe },
            Command(title: "Open Developer", subtitle: "Xcode, simulators, Docker, node_modules", symbol: "chevron.left.forwardslash.chevron.right",
                    trailing: dev > 0 ? ByteFormat.hero(dev) : CommandPalette.shortcut(for: .developerStorage)) { model.destination = .developerStorage },
            Command(title: "Open Explore", subtitle: "Every folder drawn to its size", symbol: "square.grid.2x2") {
                model.destination = .visualize
            },
            Command(title: "Biggest files", subtitle: "Explore · List", symbol: "doc") { model.destination = .biggestFiles },
            Command(title: "Find with a query", subtitle: "ext:mp4 size>500MB age>1y in:downloads", symbol: "magnifyingglass") {
                model.destination = .find
            },
            Command(title: "Duplicates", subtitle: "Files with exactly the same contents", symbol: "doc.on.doc") { model.destination = .duplicates },
            Command(title: "Forgotten files", subtitle: "Not opened in over a year", symbol: "clock") { model.destination = .forgottenFiles },
            Command(title: "What changed", subtitle: "Compare with a saved scan", symbol: "chart.line.uptrend.xyaxis") { model.destination = .snapshots },
            Command(title: "History & Put Back", subtitle: "Every cleanup, and the way back", symbol: "clock.arrow.circlepath") {
                model.destination = .history
            },
            Command(title: "Review basket", subtitle: "Opens the review — never deletes directly", symbol: "basket") { onReviewCleanup() },
            Command(title: "Explain my storage", subtitle: "A summary from this scan", symbol: "sparkles") { onExplain() },
            Command(title: "Rescan " + (model.rootURL.map { CanonicalPath.displayPath(absolutePath: $0.path) } ?? ""),
                    subtitle: model.lastScanFinishedAt.map { "Last scan " + RelativeTime.string(since: $0) } ?? "Re-read what changed",
                    symbol: "arrow.clockwise", trailing: "⌘R") {
                if let root = model.rootURL { Task { await model.scan(root) } }
            },
            Command(title: "Full rescan", subtitle: "Walk every folder again, ignoring the last scan", symbol: "arrow.clockwise.circle") {
                if let root = model.rootURL { Task { await model.scan(root, mode: .full) } }
            },
        ]
        // TASK-081: saved searches are commands too.
        for search in model.savedSearches {
            let total = model.savedSearchTotals[search.id]
            list.append(Command(title: search.name,
                                subtitle: "Saved search · " + (total.map { "\($0.count.formatted()) · \(ByteFormat.string($0.bytes))" } ?? search.query),
                                symbol: "magnifyingglass.circle") {
                model.openSavedSearch(search)
            })
        }
        if !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            for hit in searchHits {
                let kind = hit.isDirectory ? FileKind.other : FileKind.classify(fileName: hit.name, path: hit.path)
                list.append(Command(
                    title: hit.name,
                    subtitle: CommandPalette.parentPath(hit.path),
                    symbol: hit.isApplication ? "square.grid.2x2" : (hit.isDirectory ? "folder" : "doc"),
                    category: hit.isApplication ? .applications : (hit.isDirectory ? .folders : .files),
                    trailing: ByteFormat.hero(hit.bytes), hit: hit,
                    kindTag: kind == .other ? nil : BiggestFilesView.kindName(kind)
                ) {
                    model.destination = .visualize
                    model.selectedNode = hit.nodeID
                    model.currentNode = hit.isDirectory ? hit.nodeID : max(0, hit.parentID)
                })
            }
        }
        return list
    }

    private var filtered: [Command] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !q.isEmpty else { return commands.filter { $0.category == .actions } }
        if isStructuredQuery {
            let meaning = parsedQuery?.query.describe(home: NSHomeDirectory()).joined(separator: " · ") ?? ""
            let summary = isSearching ? "Searching…"
                : "\(queryMatchCount.formatted()) match\(queryMatchCount == 1 ? "" : "es") · \(ByteFormat.string(queryMatchedBytes))"
            let findAll = Command(title: "Show all in Find — \(summary)", subtitle: meaning, symbol: "magnifyingglass") {
                openInFind()
            }
            return [findAll] + commands.filter { $0.category != .actions }
        }
        // File hits were already matched word by word; only actions are
        // matched against the typed text here.
        return commands.filter {
            $0.category != .actions || $0.title.lowercased().contains(q) || $0.subtitle.lowercased().contains(q)
        }
    }

    /// Sections in display order, flattened for keyboard movement.
    private var ordered: [Command] {
        let list = filtered
        return Category.allCases.flatMap { category in list.filter { $0.category == category } }
    }

    private func run(_ cmd: Command) {
        cmd.run()
        isPresented = false
    }

    var body: some View {
        let items = ordered
        let current = items.isEmpty ? nil : items[min(highlighted, items.count - 1)]
        let resultCount = items.filter { $0.category != .actions }.count
        return VStack(spacing: 0) {
            HStack(spacing: 12) {
                Image(systemName: "magnifyingglass")
                    .font(.system(size: DiskMapType.scaled(17), weight: .medium))
                    .foregroundStyle(DiskMapTheme.ink)
                    .accessibilityHidden(true)
                TextField("", text: $query)
                    .textFieldStyle(.plain)
                    .font(.system(size: DiskMapType.scaled(20), weight: .medium))
                    .focused($fieldFocused)
                    .background(alignment: .leading) {
                        if query.isEmpty {
                            Text("Find a folder, a file, or type a command")
                                .font(.system(size: DiskMapType.scaled(20)))
                                .foregroundStyle(DiskMapTheme.ink3)
                                .allowsHitTesting(false)
                        }
                    }
                    .onSubmit { if let current { run(current) } }
                    .onKeyPress(.downArrow) {
                        highlighted = min(max(0, items.count - 1), highlighted + 1)
                        return .handled
                    }
                    .onKeyPress(.upArrow) {
                        highlighted = max(0, highlighted - 1)
                        return .handled
                    }
                    .onKeyPress(phases: .down) { press in
                        guard press.modifiers.contains(.command), let hit = current?.hit else { return .ignored }
                        if press.key == .return { reveal(hit); return .handled }
                        if press.characters.lowercased() == "b" { addToBasket(hit); return .handled }
                        return .ignored
                    }
                    .accessibilityLabel("Find anything")
                if !query.trimmingCharacters(in: .whitespaces).isEmpty {
                    Text(isSearching ? "Searching…" : "\(resultCount) result\(resultCount == 1 ? "" : "s")")
                        .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                }
                Button { isPresented = false } label: { Kbd("esc") }
                    .buttonStyle(.plain)
                    .keyboardShortcut(.cancelAction)
                    .accessibilityLabel("Close")
            }
            .padding(.horizontal, 22)
            .frame(height: 64)
            if isStructuredQuery, let problems = parsedQuery?.problems, !problems.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(problems, id: \.token) { problem in
                        Text("Ignoring \(problem.token) — \(problem.message)")
                            .font(DiskMapType.secondary)
                            .foregroundStyle(DiskMapTheme.review)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 22)
                .padding(.bottom, 8)
            }
            Rectangle().fill(DiskMapTheme.line).frame(height: 1)
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(Category.allCases, id: \.self) { category in
                            let section = items.filter { $0.category == category }
                            if !section.isEmpty {
                                HStack {
                                    Text(category.rawValue.uppercased())
                                    Spacer()
                                    if category != .actions { Text("BY SIZE") }
                                }
                                .font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced))
                                .foregroundStyle(DiskMapTheme.ink3)
                                .padding(.horizontal, 14)
                                .padding(.top, 14)
                                .padding(.bottom, 6)
                                ForEach(section) { cmd in row(cmd, on: cmd.id == current?.id, items: items) }
                                if category != Category.allCases.last {
                                    Rectangle().fill(DiskMapTheme.line).frame(height: 1).padding(.top, 8).padding(.horizontal, -10)
                                }
                            }
                        }
                        if isSearching && items.allSatisfy({ $0.category == .actions }) {
                            ProgressView("Searching this scan…").controlSize(.small).padding(14)
                        }
                    }
                    .padding(.horizontal, 10)
                    .padding(.bottom, 10)
                }
                .onChange(of: highlighted) { _, index in
                    guard items.indices.contains(index) else { return }
                    withAnimation(.easeOut(duration: 0.1)) { proxy.scrollTo(items[index].id) }
                }
            }
            Rectangle().fill(DiskMapTheme.line).frame(height: 1)
            HStack(spacing: 8) {
                Kbd("↑"); Kbd("↓"); hint("move")
                Kbd("↩"); hint("open")
                if current?.hit != nil {
                    Kbd("⌘"); Kbd("↩"); hint("show in Finder")
                    Kbd("⌘"); Kbd("B"); hint("add to basket")
                }
                Spacer()
                Kbd("esc"); hint("close")
            }
            .padding(.horizontal, 22)
            .frame(height: 46)
        }
        .frame(width: 760, height: 560)
        .background(DiskMapTheme.card)
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(DiskMapTheme.ink.opacity(0.85), lineWidth: 1))
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.black).offset(x: 6, y: 6))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Find anything")
        .task(id: query) { await updateSearch() }
        .onChange(of: query) { _, _ in highlighted = 0 }
        .onAppear {
            fieldFocused = true
            // Opened by typing in the top bar: keep the caret after what was
            // typed, so the next key continues the word instead of replacing it.
            DispatchQueue.main.async {
                if let editor = NSApp.keyWindow?.firstResponder as? NSTextView {
                    editor.setSelectedRange(NSRange(location: (editor.string as NSString).length, length: 0))
                }
            }
        }
    }

    private func hint(_ text: String) -> some View {
        Text(text).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).padding(.trailing, 10)
    }

    private func row(_ cmd: Command, on: Bool, items: [Command]) -> some View {
        Button { run(cmd) } label: {
            HStack(spacing: 14) {
                Image(systemName: cmd.symbol)
                    .font(.system(size: DiskMapType.scaled(13)))
                    .foregroundStyle(DiskMapTheme.ink)
                    .frame(width: 32, height: 32)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .accessibilityHidden(true)
                Text(Self.highlight(cmd.title, query: cmd.category == .actions ? "" : query))
                    .font(.system(size: DiskMapType.scaled(15), weight: .medium))
                    .foregroundStyle(DiskMapTheme.ink)
                    .lineLimit(1)
                    .layoutPriority(1)
                if let tag = cmd.kindTag { BoardTag(text: tag) }
                Text(cmd.subtitle)
                    .font(.system(size: DiskMapType.scaled(12), design: .monospaced))
                    .foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 8)
                if let trailing = cmd.trailing {
                    Text(trailing).font(.system(size: DiskMapType.scaled(13), weight: .medium, design: .monospaced))
                        .foregroundStyle(cmd.trailingAccent ? DiskMapTheme.accentDeep : DiskMapTheme.ink)
                        .fixedSize()
                }
                if on {
                    HStack(spacing: 6) {
                        Kbd("↩")
                        Text(cmd.category == .actions ? "RUN" : "OPEN").font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
                            .foregroundStyle(DiskMapTheme.accentDeep)
                    }
                    .fixedSize()
                }
            }
            .padding(.horizontal, 12)
            .frame(height: 52)
            .background(on ? DiskMapTheme.accentWash : Color.clear)
            .overlay(alignment: .leading) { if on { Rectangle().fill(DiskMapTheme.accent).frame(width: 2) } }
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .id(cmd.id)
        .onHover { if $0, let index = items.firstIndex(where: { $0.id == cmd.id }) { highlighted = index } }
        .accessibilityLabel("\(cmd.title). \(cmd.subtitle)\(cmd.trailing.map { ". " + $0 } ?? "")")
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func reveal(_ hit: SearchHit) {
        guard let tree = model.tree, let root = model.rootURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([tree.path(of: hit.nodeID, root: root)])
    }

    /// Stages one item through the same single-item checks as everywhere else.
    private func addToBasket(_ hit: SearchHit) {
        guard let tree = model.tree, let root = model.rootURL else { return }
        model.stageRow(path: tree.path(of: hit.nodeID, root: root).path, size: hit.bytes, reason: "Find: " + hit.name)
    }

    /// "⌘2": the Go menu's shortcut for a page, if it has one.
    static func shortcut(for destination: AppDestination) -> String? {
        KeyboardCommands.numberedDestinations.firstIndex(of: destination).map { "⌘\($0 + 1)" }
    }

    /// The folder a hit sits in ("~/Library/Containers").
    static func parentPath(_ displayPath: String) -> String {
        let parent = (displayPath as NSString).deletingLastPathComponent
        return parent.isEmpty ? displayPath : parent
    }

    /// The name with every typed word marked.
    static func highlight(_ text: String, query: String) -> AttributedString {
        var result = AttributedString(text)
        let words = query.lowercased().split(whereSeparator: \.isWhitespace).map(String.init).filter { !$0.contains(":") && !$0.contains(">") && !$0.contains("<") }
        let lower = text.lowercased()
        for word in words where !word.isEmpty {
            var search = lower.startIndex
            while let range = lower.range(of: word, range: search..<lower.endIndex) {
                if let low = AttributedString.Index(range.lowerBound, within: result),
                   let high = AttributedString.Index(range.upperBound, within: result) {
                    result[low..<high].backgroundColor = DiskMapTheme.accentWash
                    result[low..<high].foregroundColor = DiskMapTheme.accentDeep
                }
                search = range.upperBound
            }
        }
        return result
    }

    @MainActor
    private func updateSearch() async {
        let normalized = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty, let tree = model.tree, let root = model.rootURL,
              model.selectedTotals.count == tree.count else {
            searchHits = []
            isSearching = false
            return
        }
        isSearching = true
        try? await Task.sleep(for: .milliseconds(150))
        guard !Task.isCancelled else { return }
        // Plain words search names only, largest first — the old search built
        // a full path for every node on every keystroke.
        let parsed = FileQuery.parse(normalized, home: NSHomeDirectory(), root: root.path)
        let limit = parsed.query.isStructured ? 8 : 100
        let totals = model.selectedTotals
        let duplicates: Set<Int32>? = model.duplicateDidRun ? Set(model.duplicateGroups.flatMap(\.fileIDs)) : nil
        let context = FileQuery.Context(home: NSHomeDirectory(), duplicateFileIDs: duplicates)
        let work = Task.detached(priority: .userInitiated) { () -> (FileQuery.Result, [SearchHit]) in
            let result = parsed.query.run(tree: tree, root: root, totals: totals, context: context,
                                          limit: limit, isCancelled: { Task.isCancelled })
            let hits = result.ids.map { id -> SearchHit in
                let index = Int(id)
                let name = tree.name(of: id)
                let directory = tree.isDirectory[index]
                return SearchHit(
                    nodeID: id,
                    parentID: tree.parent[index],
                    name: name,
                    path: CanonicalPath.displayPath(absolutePath: tree.path(of: id, root: root).path),
                    isDirectory: directory,
                    isApplication: directory && name.lowercased().hasSuffix(".app"),
                    bytes: totals[index]
                )
            }
            return (result, hits)
        }
        let (result, hits) = await withTaskCancellationHandler { await work.value } onCancel: { work.cancel() }
        guard !Task.isCancelled, !result.wasCancelled else { return }
        searchHits = hits
        queryMatchCount = result.matchCount
        queryMatchedBytes = result.matchedBytes
        isSearching = false
    }
}
