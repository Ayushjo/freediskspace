import AppKit
import CoreServices
import DiskMapCore
import SwiftUI

/// Applications (redesign board 09): every app with its own size and the data
/// it keeps in your Library, and Uninstall that puts the app and the
/// leftovers you tick in the basket.
struct AppsView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.widthClass) private var widthClass

    @State private var apps: [ApplicationEntry] = []
    @State private var isLoading = true
    @State private var selectedID: String?
    @State private var sortKey: SortKey = .size
    @State private var onlyStale = false
    @State private var query = ""
    @State private var loadTask: Task<Void, Never>?
    /// Leftover paths the person unticked or ticked, per app, over the defaults.
    @State private var leftoverChoice: [String: Bool] = [:]

    enum SortKey: String, CaseIterable, Identifiable {
        case size, lastOpened, name
        var id: String { rawValue }
        var title: String {
            switch self {
            case .size: return "Size"
            case .lastOpened: return "Last opened"
            case .name: return "Name"
            }
        }
        /// Last opened puts the longest-unused first: that is what you look for.
        var catalogSort: ApplicationsCatalog.Sort {
            switch self {
            case .size: return .sizeDesc
            case .lastOpened: return .lastUsedAsc
            case .name: return .nameAsc
            }
        }
    }

    private var summary: ApplicationSummary { ApplicationsCatalog.summarize(apps) }

    private var visible: [ApplicationEntry] {
        var list = onlyStale ? apps.filter(\.isNotRecentlyUsed) : apps
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !q.isEmpty {
            list = list.filter {
                $0.name.lowercased().contains(q) || ($0.publisher?.lowercased().contains(q) ?? false)
                    || ($0.bundleID?.lowercased().contains(q) ?? false)
            }
        }
        return ApplicationsCatalog.sorted(list, by: sortKey.catalogSort)
    }

    private var active: ApplicationEntry? {
        if let selectedID, let hit = apps.first(where: { $0.id == selectedID }) { return hit }
        return visible.first
    }

    var body: some View {
        HStack(spacing: 0) {
            mainColumn
            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
            Group {
                if let app = active { inspector(app) } else {
                    DiskMapEmptyState(symbol: "app.dashed", title: "Select an app", message: "See its data and what uninstalling frees.")
                }
            }
            .frame(width: widthClass == .compact ? 300 : widthClass == .regular ? 330 : 370)
            .background(DiskMapTheme.sidebar)
        }
        .background(DiskMapTheme.page)
        .task { await reloadCatalog() }
        .onDisappear { loadTask?.cancel() }
    }

    // MARK: Main column

    private var mainColumn: some View {
        let bundles = apps.reduce(Int64(0)) { $0 + $1.bundleBytes }
        let data = apps.reduce(Int64(0)) { $0 + $1.relatedBytes }
        let stale = apps.filter(\.isNotRecentlyUsed)
        return VStack(alignment: .leading, spacing: widthClass.gap) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Applications").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink)
                Text(isLoading && apps.isEmpty ? "Finding your apps…"
                     : "\(apps.count) apps use \(ByteFormat.hero(bundles)), and their data in your Library uses another \(ByteFormat.hero(data)).")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
            }
            HStack(spacing: 12) {
                Text("SORT").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                SegmentSwitch(items: SortKey.allCases.map { key in .init(title: key.title, isOn: sortKey == key) { sortKey = key } })
                if widthClass != .compact {
                    Text("SHOW").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                        .padding(.leading, 8)
                }
                Button { onlyStale.toggle() } label: {
                    HStack(spacing: 8) {
                        TickBox(state: onlyStale ? .on : .off) { onlyStale.toggle() }
                        Text("Not opened in 6 months").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                        Text(stale.isEmpty ? "none" : "\(stale.count) · \(ByteFormat.hero(stale.reduce(0) { $0 + $1.totalBytes }))")
                            .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                    }
                    .padding(.horizontal, 10)
                    .frame(height: 30)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(onlyStale ? DiskMapTheme.ink : DiskMapTheme.line, lineWidth: 1))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .fixedSize()
                .accessibilityAddTraits(onlyStale ? .isSelected : [])
                Spacer(minLength: 8)
                DiskMapSearchField(placeholder: "Find an app", text: $query)
                    .frame(maxWidth: widthClass == .compact ? 160 : 220)
            }
            if isLoading && apps.isEmpty {
                DiskMapLoadingState(title: "Finding applications", detail: "Reading /Applications and ~/Applications.")
            } else if visible.isEmpty {
                DiskMapEmptyState(symbol: "app.dashed", title: apps.isEmpty ? "No applications found" : "No apps match",
                                  message: apps.isEmpty ? "freedisk.space couldn’t find installed applications in the usual places."
                                                        : "Try another name, or show every app.")
            } else {
                table
                if widthClass == .wide {
                    Text("Uninstall puts the app and the leftovers you tick in the basket. Nothing moves until you review it there.")
                        .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                }
            }
        }
        .padding(.horizontal, widthClass.pagePadding)
        .padding(.top, widthClass == .compact ? 14 : 20)
        .padding(.bottom, widthClass.pagePadding)
        .frame(maxWidth: .infinity)
    }

    private var table: some View {
        let items = visible
        let activeID = active?.id
        let showData = widthClass != .compact
        return VStack(spacing: 0) {
            HStack(spacing: 0) {
                Text("App").frame(maxWidth: .infinity, alignment: .leading)
                Text("App").frame(width: 84, alignment: .trailing)
                if showData { Text("Data").frame(width: 84, alignment: .trailing) }
                Text(sortKey == .size ? "Total ↓" : "Total").foregroundStyle(sortKey == .size ? DiskMapTheme.ink : DiskMapTheme.ink3)
                    .frame(width: 90, alignment: .trailing)
                Text(sortKey == .lastOpened ? "Last opened ↑" : "Last opened").foregroundStyle(sortKey == .lastOpened ? DiskMapTheme.ink : DiskMapTheme.ink3)
                    .frame(width: widthClass == .compact ? 110 : 190, alignment: .leading).padding(.leading, 24)
                Color.clear.frame(width: 96)
            }
            .font(DiskMapType.secondary)
            .foregroundStyle(DiskMapTheme.ink3)
            .padding(.horizontal, 16)
            .frame(height: 40)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink.opacity(0.85)).frame(height: 1) }
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(items) { app in appRow(app, selected: app.id == activeID, showData: showData) }
                }
            }
            .listKeyboard(
                ids: items.map(\.id), selection: $selectedID,
                path: { id in items.first { $0.id == id }?.bundlePath },
                stage: { id in if let app = items.first(where: { $0.id == id }) { Task { await uninstall(app) } } },
                selectAll: {}, clearSelection: {}
            )
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func appRow(_ app: ApplicationEntry, selected: Bool, showData: Bool) -> some View {
        let staged = model.isStaged(URL(fileURLWithPath: app.bundlePath))
        return HStack(spacing: 0) {
            HStack(spacing: 12) {
                AppIconView(path: app.bundlePath, size: 28)
                Text(app.name).font(.system(size: DiskMapType.scaled(13.5), weight: .medium)).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                if app.status == .system { BoardTag(text: "macOS") }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(app.sizePending ? "…" : ByteFormat.hero(app.bundleBytes)).frame(width: 84, alignment: .trailing)
                .foregroundStyle(DiskMapTheme.ink)
            if showData {
                Text(app.sizePending ? "…" : app.relatedBytes > 0 ? ByteFormat.hero(app.relatedBytes) : "—").frame(width: 84, alignment: .trailing)
                    .foregroundStyle(app.relatedBytes >= app.bundleBytes ? DiskMapTheme.ink : DiskMapTheme.ink3)
            }
            Text(app.sizePending ? "…" : ByteFormat.hero(app.totalBytes)).fontWeight(.semibold).frame(width: 90, alignment: .trailing)
                .foregroundStyle(DiskMapTheme.ink)
            HStack(spacing: 8) {
                Text(Self.lastOpened(app.lastUsed)).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                if app.isNotRecentlyUsed && widthClass != .compact { BoardTag(text: "6 mo+", tone: .accent) }
            }
            .frame(width: widthClass == .compact ? 110 : 190, alignment: .leading).padding(.leading, 24)
            Group {
                if !app.canStageForCleanup {
                    Color.clear
                } else if staged {
                    BoardTag(text: "In basket", tone: .accent)
                } else if selected {
                    Button("Uninstall") { Task { await uninstall(app) } }.buttonStyle(AccentButtonStyle())
                } else {
                    Button("Uninstall") { Task { await uninstall(app) } }.buttonStyle(SecondaryButtonStyle())
                }
            }
            .frame(width: 96, alignment: .trailing)
        }
        .font(DiskMapType.figure)
        .padding(.horizontal, 16)
        .frame(height: 50)
        .background(selected ? DiskMapTheme.accentWash : Color.clear)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture { selectedID = app.id }
        .simultaneousGesture(TapGesture(count: 2).onEnded { open(app) })
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(app.name), \(app.sizePending ? "size pending" : ByteFormat.string(app.totalBytes)), opened \(Self.lastOpened(app.lastUsed))")
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
        .rowActions(path: app.bundlePath, stage: app.canStageForCleanup ? { Task { await uninstall(app) } } : nil)
    }

    // MARK: Inspector

    private func isTicked(_ item: ApplicationRelatedItem) -> Bool {
        leftoverChoice[item.path] ?? Self.tickedByDefault(item.kind)
    }

    /// Ticked for you: what the app rebuilds. Unticked: places that can hold your own files.
    static func tickedByDefault(_ kind: ApplicationRelatedKind) -> Bool {
        switch kind {
        case .caches, .preferences, .derivedData: return true
        case .applicationSupport, .containers, .simulators, .archives, .other: return false
        }
    }

    /// What Uninstall frees: the app plus the ticked leftovers.
    static func uninstallBytes(_ app: ApplicationEntry, ticked: (ApplicationRelatedItem) -> Bool) -> Int64 {
        app.bundleBytes + app.related.filter(ticked).reduce(0) { $0 + $1.bytes }
    }

    private func inspector(_ app: ApplicationEntry) -> some View {
        let frees = Self.uninstallBytes(app, ticked: isTicked)
        let ticked = app.related.filter(isTicked)
        let unticked = app.related.filter { !isTicked($0) }
        let staged = model.isStaged(URL(fileURLWithPath: app.bundlePath))
        return VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    HStack(alignment: .top, spacing: 14) {
                        AppIconView(path: app.bundlePath, size: 56)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(app.name).font(.system(size: DiskMapType.scaled(20), weight: .semibold)).foregroundStyle(DiskMapTheme.ink).lineLimit(2)
                            Text([Self.publisherName(app.publisher), app.version.map { "version \($0)" }].compactMap { $0 }.joined(separator: " · "))
                                .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                            Text(CanonicalPath.displayPath(absolutePath: app.bundlePath)).font(.system(size: DiskMapType.scaled(11), design: .monospaced))
                                .foregroundStyle(DiskMapTheme.ink3).lineLimit(1).truncationMode(.middle)
                        }
                    }
                    VStack(alignment: .leading, spacing: 4) {
                        Text("UNINSTALL FREES").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                        Text(app.sizePending ? "Measuring…" : ByteFormat.hero(frees)).font(DiskMapType.hero(44)).foregroundStyle(DiskMapTheme.ink)
                            .lineLimit(1).minimumScaleFactor(0.6)
                    }
                    UninstallBar(app: app.bundleBytes, ticked: ticked.reduce(0) { $0 + $1.bytes }, kept: unticked.reduce(0) { $0 + $1.bytes })
                        .frame(height: 12)
                    VStack(spacing: 8) {
                        legend(swatch: AnyView(Rectangle().fill(DiskMapTheme.ink)), title: "The app", bytes: app.bundleBytes, accent: false)
                        legend(swatch: AnyView(Hatch(spacing: 4, width: 1.5).overlay(Rectangle().stroke(DiskMapTheme.accent, lineWidth: 1))),
                               title: "Leftovers ticked", bytes: ticked.reduce(0) { $0 + $1.bytes }, accent: true)
                        if !unticked.isEmpty {
                            legend(swatch: AnyView(Rectangle().fill(DiskMapTheme.soft).overlay(Rectangle().stroke(DiskMapTheme.line, lineWidth: 1))),
                                   title: "Kept", bytes: unticked.reduce(0) { $0 + $1.bytes }, accent: false)
                        }
                    }
                    if !app.related.isEmpty { leftovers(app) }
                    else if app.sizePending { Text("Looking for its data…").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3) }
                    if unticked.contains(where: { $0.kind == .applicationSupport || $0.kind == .containers }) {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Your files may be in here").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                            Text("Application Support and Containers can hold documents, saves or libraries you made in \(app.name). We left them unticked; tick them to remove them too.")
                                .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                        }
                        .padding(14)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
                        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                        .overlay(alignment: .leading) {
                            UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10).fill(DiskMapTheme.accent).frame(width: 3)
                        }
                    }
                    VStack(spacing: 8) {
                        fact("Last opened", Self.lastOpened(app.lastUsed))
                        if let installed = app.installed { fact("Installed", installed.formatted(.dateTime.month(.abbreviated).year())) }
                        fact("From", app.source.title)
                    }
                    if !app.canStageForCleanup {
                        Text(app.removalGuidance).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(20)
            }
            VStack(spacing: 10) {
                HStack(spacing: 8) {
                    Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: app.bundlePath)]) }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                    Button("Open \(app.name)") { open(app) }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                }
                if app.canStageForCleanup {
                    Button(staged ? "In the basket" : "Uninstall · add \(ByteFormat.hero(frees)) to basket") {
                        if staged { model.isCleanupQueuePresented = true } else { Task { await uninstall(app) } }
                    }
                    .buttonStyle(AccentButtonStyle(large: true, fullWidth: true, hard: !staged))
                    .disabled(app.sizePending)
                }
            }
            .padding(20)
        }
    }

    private func legend(swatch: AnyView, title: String, bytes: Int64, accent: Bool) -> some View {
        HStack(spacing: 10) {
            swatch.frame(width: 12, height: 12).clipShape(RoundedRectangle(cornerRadius: 2))
            Text(title).font(DiskMapType.body).foregroundStyle(accent ? DiskMapTheme.accentDeep : DiskMapTheme.ink)
            Spacer()
            Text(ByteFormat.hero(bytes)).font(DiskMapType.figure).foregroundStyle(accent ? DiskMapTheme.accentDeep : DiskMapTheme.ink)
        }
        .accessibilityElement(children: .combine)
    }

    private func leftovers(_ app: ApplicationEntry) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("LEFTOVERS · \(app.related.count) PLACE\(app.related.count == 1 ? "" : "S")")
                .font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
            VStack(spacing: 0) {
                ForEach(Array(app.related.sorted { $0.bytes > $1.bytes }.prefix(12).enumerated()), id: \.element.id) { index, item in
                    let on = isTicked(item)
                    HStack(alignment: .top, spacing: 10) {
                        TickBox(state: on ? .on : .off) { leftoverChoice[item.path] = !on }
                            .padding(.top, 2)
                        VStack(alignment: .leading, spacing: 2) {
                            HStack {
                                Text(item.kind.title).font(DiskMapType.bodyEmphasis).foregroundStyle(on ? DiskMapTheme.ink : DiskMapTheme.ink2)
                                Spacer(minLength: 6)
                                Text(ByteFormat.hero(item.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                            }
                            Text(CanonicalPath.displayPath(absolutePath: item.path)).font(.system(size: DiskMapType.scaled(10.5), design: .monospaced))
                                .foregroundStyle(DiskMapTheme.ink3).lineLimit(1).truncationMode(.middle)
                        }
                    }
                    .padding(.horizontal, 12).padding(.vertical, 10)
                    .overlay(alignment: .top) { if index > 0 { Rectangle().fill(DiskMapTheme.line).frame(height: 1) } }
                    .contentShape(Rectangle())
                    .onTapGesture { leftoverChoice[item.path] = !on }
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("\(item.kind.title), \(ByteFormat.string(item.bytes)), \(on ? "will be removed" : "kept")")
                    .accessibilityAddTraits(.isButton)
                }
            }
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            if app.related.contains(where: { $0.kind == .derivedData || $0.kind == .simulators || $0.kind == .archives }) {
                Button("More in Developer →") { model.destination = .developerStorage }
                    .buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
            }
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

    /// Bundles carry a copyright line, not a publisher: "© 2026 Docker Inc.
    /// All rights reserved." → "Docker Inc."
    static func publisherName(_ raw: String?) -> String? {
        guard var text = raw?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return nil }
        for marker in ["all rights reserved", "alle rechte vorbehalten", "tous droits réservés"] {
            if let range = text.range(of: marker, options: .caseInsensitive) { text = String(text[..<range.lowerBound]) }
        }
        text = text.replacingOccurrences(of: "copyright", with: "", options: .caseInsensitive)
        let junk = CharacterSet(charactersIn: "©()–—-,.;: ").union(.decimalDigits).union(.whitespaces)
        text = String(text.drop { $0.unicodeScalars.allSatisfy(junk.contains) })
        text = text.trimmingCharacters(in: CharacterSet(charactersIn: " ,;:–—-"))
        // Keep the full stop of "Inc." / "Ltd." / "Co.", drop any other.
        if text.hasSuffix("."), !["Inc.", "Ltd.", "Co.", "Corp."].contains(where: { text.hasSuffix($0) }) {
            text.removeLast()
        }
        return text.isEmpty ? nil : text
    }

    // MARK: - Load / enrich

    private func reloadCatalog() async {
        isLoading = true
        let urls = AppLeftoverFinder.applicationBundles(in: AppLeftoverFinder.defaultApplicationDirectories())
        var stubs: [ApplicationEntry] = []
        stubs.reserveCapacity(urls.count)
        for url in urls {
            let meta = AppMetadata.read(url)
            let leftovers = AppLeftovers(
                bundleID: meta.bundleID,
                appName: meta.name,
                bundlePath: url,
                bundleSize: 0,
                leftoverPaths: [],
                leftoverSize: 0
            )
            var entry = ApplicationsCatalog.makeEntry(
                leftovers: leftovers,
                publisher: meta.publisher,
                version: meta.version,
                lastUsed: meta.lastUsed,
                installed: meta.installed,
                sizePending: true
            )
            // Re-classify source with path even before sizing
            entry = ApplicationEntry(
                name: entry.name,
                publisher: entry.publisher,
                version: entry.version,
                bundleID: entry.bundleID,
                bundlePath: entry.bundlePath,
                bundleBytes: 0,
                relatedBytes: 0,
                source: ApplicationsCatalog.classifySource(bundlePath: url.path, bundleID: meta.bundleID),
                status: ApplicationsCatalog.classifyStatus(
                    source: ApplicationsCatalog.classifySource(bundlePath: url.path, bundleID: meta.bundleID),
                    totalBytes: 0,
                    lastUsed: meta.lastUsed,
                    bundleID: meta.bundleID
                ),
                lastUsed: entry.lastUsed,
                installed: entry.installed,
                related: [],
                blurb: entry.blurb,
                whyLarge: entry.whyLarge,
                removalGuidance: entry.removalGuidance,
                sizePending: true
            )
            stubs.append(entry)
        }
        apps = ApplicationsCatalog.sorted(stubs, by: sortKey.catalogSort)
        if let selectedID, !apps.contains(where: { $0.id == selectedID }) {
            self.selectedID = nil
        }
        isLoading = false

        loadTask?.cancel()
        // Every installed app's ID, so one app's uninstall never claims a
        // sibling's data (Chrome vs Chrome Canary).
        let installedIDs = Set(urls.compactMap { Bundle(url: $0)?.bundleIdentifier })
        loadTask = Task {
            await withTaskGroup(of: ApplicationEntry?.self) { group in
                var inflight = 0
                var iterator = urls.makeIterator()
                func enqueue() {
                    while inflight < 3, let url = iterator.next() {
                        inflight += 1
                        group.addTask {
                            if Task.isCancelled { return nil }
                            let leftovers = AppLeftoverFinder.findLeftovers(for: url, measureRelated: true, otherInstalledIDs: installedIDs)
                            let meta = AppMetadata.read(url)
                            return ApplicationsCatalog.makeEntry(
                                leftovers: leftovers,
                                publisher: meta.publisher,
                                version: meta.version,
                                lastUsed: meta.lastUsed,
                                installed: meta.installed,
                                sizePending: false
                            )
                        }
                    }
                }
                enqueue()
                for await result in group {
                    inflight -= 1
                    if let entry = result {
                        await MainActor.run {
                            if let idx = apps.firstIndex(where: { $0.id == entry.id }) {
                                apps[idx] = entry
                            } else {
                                apps.append(entry)
                            }
                            apps = ApplicationsCatalog.sorted(apps, by: sortKey.catalogSort)
                        }
                    }
                    enqueue()
                }
            }
        }
    }

    // MARK: - Actions

    private func open(_ app: ApplicationEntry) {
        NSWorkspace.shared.open(URL(fileURLWithPath: app.bundlePath))
    }

    /// Apps (and Xcode, filed under dev tools) are what this screen is for.
    static let appCategories: Set<String> = ["apps", "devtools"]

    /// The app and its ticked leftovers, through the one confirm sheet.
    private func uninstall(_ app: ApplicationEntry) async {
        guard app.canStageForCleanup else { return }
        var requests = [CleanupStageRequest(url: URL(fileURLWithPath: app.bundlePath), size: app.bundleBytes, reason: "Application: \(app.name)")]
        requests += app.related.filter(isTicked).map {
            CleanupStageRequest(url: URL(fileURLWithPath: $0.path), size: $0.bytes, reason: "Application: \(app.name)")
        }
        // This screen removes apps on purpose: an app isn't a surprise here.
        _ = await model.confirmStageMany(requests, title: "Uninstall \(app.name)", allowing: Self.appCategories)
    }

    /// "Today", "Yesterday", "4 months ago"; "—" when Spotlight has no launch date (unknown, not never).
    static func lastOpened(_ date: Date?, now: Date = Date()) -> String {
        guard let date else { return "—" }
        let days = Calendar.current.dateComponents([.day], from: date, to: now).day ?? 0
        if days <= 0 { return "Today" }
        if days == 1 { return "Yesterday" }
        if days < 7 { return "\(days) days ago" }
        if days < 14 { return "Last week" }
        if days < 60 { return "\(days / 7) weeks ago" }
        if days < 365 { return "\(days / 30) months ago" }
        let years = days / 365
        return years == 1 ? "1 year ago" : "\(years) years ago"
    }
}

/// The inspector's bar: the app in ink, ticked leftovers hatched, kept data pale.
private struct UninstallBar: View {
    var app: Int64
    var ticked: Int64
    var kept: Int64
    var body: some View {
        GeometryReader { geo in
            let total = Double(max(1, app + ticked + kept))
            HStack(spacing: 2) {
                Rectangle().fill(DiskMapTheme.ink).frame(width: max(3, geo.size.width * Double(app) / total))
                if ticked > 0 { Hatch(spacing: 5, width: 2).frame(width: geo.size.width * Double(ticked) / total) }
                if kept > 0 { Rectangle().fill(DiskMapTheme.soft).frame(width: geo.size.width * Double(kept) / total) }
            }
            .clipShape(RoundedRectangle(cornerRadius: 3))
        }
        .accessibilityHidden(true)
    }
}

// MARK: - Icon

private struct AppIconView: View {
    let path: String
    var size: CGFloat = 36

    var body: some View {
        Image(nsImage: NSWorkspace.shared.icon(forFile: path))
            .resizable()
            .interpolation(.high)
            .frame(width: size, height: size)
            .clipShape(RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
    }
}

// MARK: - Metadata

private enum AppMetadata {
    struct Info {
        var name: String
        var bundleID: String?
        var publisher: String?
        var version: String?
        var lastUsed: Date?
        var installed: Date?
    }

    static func read(_ url: URL) -> Info {
        let bundle = Bundle(url: url)
        let name = (bundle?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
            ?? (bundle?.object(forInfoDictionaryKey: "CFBundleName") as? String)
            ?? url.deletingPathExtension().lastPathComponent
        let shortPublisher: String? = {
            if let team = bundle?.object(forInfoDictionaryKey: "NSHumanReadableCopyright") as? String {
                let cleaned = cleanPublisher(team)
                if let cleaned, !cleaned.isEmpty { return cleaned }
            }
            return nil
        }()
        let version = bundle?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        var lastUsed: Date?
        var installed: Date?
        if let values = try? url.resourceValues(forKeys: [.creationDateKey]) {
            installed = values.creationDate
        }
        // Spotlight's last-used date only. The bundle folder's access date is
        // not a launch: on a real Mac it showed iMovie, never opened, as
        // "used 1 y ago" (its install date). Unknown stays unknown.
        if let item = MDItemCreate(nil, url.path as CFString),
           let spot = MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date {
            lastUsed = spot
        }
        return Info(
            name: name,
            bundleID: bundle?.bundleIdentifier,
            publisher: shortPublisher,
            version: version,
            lastUsed: lastUsed,
            installed: installed
        )
    }

    private static func cleanPublisher(_ raw: String) -> String? {
        var s = raw
        // Strip "Copyright © 2024 Company." boilerplate
        if let r = try? NSRegularExpression(pattern: #"(?i)copyright\s*(©|\(c\))?\s*\d{4}\s*"#) {
            s = r.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: "")
        }
        s = s.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        if s.count > 48 { s = String(s.prefix(45)) + "…" }
        return s.isEmpty ? nil : s
    }
}
