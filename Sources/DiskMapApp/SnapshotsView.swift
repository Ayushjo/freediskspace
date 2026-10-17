import AppKit
import DiskMapCore
import SwiftUI

/// What changed (redesign board 08): freedisk.space's own saved scans as a
/// chart, today against one of them, and the folders that grew and shrank.
struct SnapshotsView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.widthClass) private var widthClass

    @State private var records: [SnapshotRecord] = []
    @State private var beforeID: String?
    @State private var afterID: String?
    @State private var comparison: SnapshotComparison?
    @State private var hotspots: [SnapshotComparison.Entry] = []
    @State private var comparedIDs: (before: String, after: String)?
    @State private var compareTask: Task<Void, Never>?
    @State private var isComparing = false
    @State private var showSave = false
    @State private var saveName = ""
    @State private var saveNote = ""
    @State private var statusMessage: String?
    @State private var confirmDelete: SnapshotRecord?
    @State private var showAllGrew = false

    /// Saved scans live here; a snapshot-harness run can point at a scratch folder.
    private var storeDirectory: URL { SnapshotHarness.snapshotStore ?? SnapshotStore.defaultDirectory() }

    private var allRecords: [SnapshotRecord] {
        var list = records
        if let current = currentRecord { list.insert(current, at: 0) }
        return list
    }

    private var currentRecord: SnapshotRecord? {
        guard let root = model.rootURL, model.tree != nil else { return nil }
        let vol = VolumeStats.forPath(root.path)
        let meta = SnapshotMeta(
            name: "Today", note: "", favorite: false,
            volumeName: vol?.volumeName, totalBytes: vol?.totalBytes, freeBytes: vol?.freeBytes, usedBytes: vol?.usedBytes,
            scannedBytes: model.selectedTotals.first, fileCount: model.descendantFileCounts.first,
            folderCount: model.descendantFolderCounts.first, scanSeconds: model.lastScanSeconds
        )
        return SnapshotRecord(url: URL(fileURLWithPath: "/tmp/diskmap-current-scan"),
                              header: SnapshotHeader(rootPath: root.path, capturedAt: Date()), meta: meta, isCurrent: true)
    }

    private func record(_ id: String?) -> SnapshotRecord? {
        guard let id else { return nil }
        return allRecords.first { $0.id == id }
    }

    /// The chart measures what the scan saw, the same for saved and live scans.
    private func scanned(_ rec: SnapshotRecord) -> Int64 { rec.meta.scannedBytes ?? rec.usedBytes }

    var body: some View {
        Group {
            if model.tree == nil && records.isEmpty {
                DiskMapEmptyState(symbol: "chart.bar", title: "Scan first, then save scans",
                                  message: "Saved scans record sizes over time so you can see what grew. They don’t copy your files.")
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: widthClass.gap) {
                        header
                        chartCard
                        changeCards
                        if let statusMessage {
                            Text(statusMessage).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                        }
                    }
                    .padding(.horizontal, widthClass.pagePadding)
                    .padding(.top, widthClass == .compact ? 14 : 20)
                    .padding(.bottom, widthClass.pagePadding)
                }
            }
        }
        .background(DiskMapTheme.page)
        .task { reload() }
        .onChange(of: beforeID) { _, _ in scheduleCompare() }
        .onChange(of: afterID) { _, _ in scheduleCompare() }
        .sheet(isPresented: $showSave) { saveSheet }
        .confirmationDialog(
            "Add this saved scan to the basket?",
            isPresented: Binding(get: { confirmDelete != nil }, set: { if !$0 { confirmDelete = nil } }),
            titleVisibility: .visible
        ) {
            Button("Add to basket") { if let rec = confirmDelete { Task { await stageSnapshot(rec) } } }
            Button("Cancel", role: .cancel) { confirmDelete = nil }
        } message: {
            Text("Only the saved scan and its notes go in the basket; you review them before anything moves to the Trash. Your files are not affected.")
        }
    }

    // MARK: Header

    private var delta: Int64? {
        guard let before = record(beforeID), let after = record(afterID) else { return nil }
        return scanned(after) - scanned(before)
    }

    private var header: some View {
        let stacked = widthClass == .compact
        let layout = stacked ? AnyLayout(VStackLayout(alignment: .leading, spacing: 12)) : AnyLayout(HStackLayout(alignment: .top, spacing: 16))
        return layout {
            VStack(alignment: .leading, spacing: 6) {
                headline
                    .font(DiskMapType.pageTitle)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Text("Comparing your saved scans. These are freedisk.space's own scans, kept on this Mac. Not Time Machine.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(2)
            }
            if !stacked { Spacer(minLength: 8) }
            HStack(spacing: 10) {
                comparePicker
                Button("Save this scan") { prepareSave() }
                    .buttonStyle(PrimaryButtonStyle())
                    .disabled(model.tree == nil)
                savedMenu
            }
        }
    }

    private var headline: Text {
        let title = Text("What changed").foregroundColor(DiskMapTheme.ink)
        guard let delta, let before = record(beforeID) else {
            return title + Text(" · nothing saved yet").foregroundColor(DiskMapTheme.ink3)
        }
        let since = Text(" " + Self.sinceText(before.header.capturedAt, now: record(afterID)?.header.capturedAt ?? Date())).foregroundColor(DiskMapTheme.ink)
        return title + Text(" · ").foregroundColor(DiskMapTheme.ink3)
            + Text(Self.signed(delta)).foregroundColor(delta > 0 ? DiskMapTheme.accent : DiskMapTheme.ink) + since
    }

    private var comparePicker: some View {
        HStack(spacing: 0) {
            Text("COMPARE").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                .padding(.horizontal, 10).frame(maxHeight: .infinity).background(DiskMapTheme.soft)
            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
            recordMenu(selection: $afterID, title: record(afterID).map(shortName) ?? "Today")
            Text("vs").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
            recordMenu(selection: $beforeID, title: record(beforeID).map(shortName) ?? "Pick a scan")
        }
        .frame(height: DiskMapMetric.controlHeight)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .fixedSize()
    }

    private func recordMenu(selection: Binding<String?>, title: String) -> some View {
        Menu {
            ForEach(allRecords) { rec in
                Button { selection.wrappedValue = rec.id } label: {
                    let label = rec.isCurrent ? "Today (this scan)" : rec.pickerLabel
                    if selection.wrappedValue == rec.id { Label(label, systemImage: "checkmark") } else { Text(label) }
                }
            }
        } label: {
            HStack(spacing: 6) {
                Text(title).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold)).foregroundStyle(DiskMapTheme.ink3)
            }
            .padding(.horizontal, 10)
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
    }

    /// Favourite or remove saved scans.
    private var savedMenu: some View {
        Menu {
            if records.isEmpty { Text("No saved scans yet") }
            ForEach(records.prefix(12)) { rec in
                Menu(rec.pickerLabel) {
                    Button("Compare with today") { beforeID = rec.id; afterID = currentRecord?.id ?? afterID }
                    Button(rec.meta.favorite ? "Unfavourite" : "Favourite") { toggleFavorite(rec) }
                    Divider()
                    Button("Add to basket…") { confirmDelete = rec }
                }
            }
        } label: {
            Image(systemName: "ellipsis").frame(width: 28, height: 28).contentShape(Rectangle())
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .foregroundStyle(DiskMapTheme.ink2)
        .help("Saved scans")
        .accessibilityLabel("Saved scans")
    }

    private func shortName(_ rec: SnapshotRecord) -> String {
        rec.isCurrent ? "Today" : Self.shortDate(rec.header.capturedAt)
    }

    // MARK: Chart

    private var chartPoints: [UsageChart.Point] {
        let saved = records.sorted { $0.header.capturedAt < $1.header.capturedAt }.suffix(widthClass == .compact ? 5 : 7)
        var points = saved.map { rec in
            UsageChart.Point(id: rec.id, label: Self.shortDate(rec.header.capturedAt).uppercased(), bytes: scanned(rec),
                             role: rec.id == beforeID ? .before : .past, note: rec.meta.note.isEmpty ? nil : rec.meta.note)
        }
        if let current = currentRecord {
            points.append(.init(id: current.id, label: "TODAY", bytes: scanned(current), role: current.id == afterID ? .after : .past, note: nil))
        }
        return points
    }

    private var chartCard: some View {
        let points = chartPoints
        let (grew, shrank) = Self.split(hotspots)
        return VStack(spacing: 0) {
            HStack(spacing: 14) {
                Text("Scan size").font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
                Text("last \(points.count) scan\(points.count == 1 ? "" : "s")").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                Spacer(minLength: 8)
                if comparison != nil, widthClass != .compact {
                    stat("Grew", Self.signed(grew.reduce(0) { $0 + $1.delta }), accent: true)
                    stat("Shrank", Self.signed(shrank.reduce(0) { $0 + $1.delta }), accent: false)
                    stat("Folders changed", "\(grew.count + shrank.count)", accent: false)
                }
            }
            .padding(.horizontal, 20)
            .frame(height: 50)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            UsageChart(points: points) { id in
                if id != afterID { beforeID = id }
            }
            .frame(height: widthClass == .compact ? 230 : widthClass == .regular ? 300 : 360)
            .padding(.horizontal, 20)
            .padding(.top, 14)
            .padding(.bottom, 12)
            if records.isEmpty {
                Text("Save this scan now. Next time, this chart shows how your disk moved and the lists below say which folders did it.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 20).padding(.bottom, 16)
            }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
    }

    private func stat(_ label: String, _ value: String, accent: Bool) -> some View {
        HStack(spacing: 6) {
            Text(label.uppercased()).foregroundStyle(DiskMapTheme.ink3)
            Text(value).foregroundStyle(accent ? DiskMapTheme.accentDeep : DiskMapTheme.ink)
        }
        .font(.system(size: DiskMapType.scaled(11.5), weight: .medium, design: .monospaced))
    }

    // MARK: Grew and shrank

    @ViewBuilder
    private var changeCards: some View {
        if isComparing {
            DiskMapLoadingState(title: "Comparing", detail: "Reading both scans.").frame(minHeight: 160)
        } else if comparison != nil {
            let (grew, shrank) = Self.split(hotspots)
            let layout = widthClass == .compact ? AnyLayout(VStackLayout(spacing: widthClass.gap)) : AnyLayout(HStackLayout(alignment: .top, spacing: widthClass.gap))
            layout {
                changeCard(title: "Grew", entries: grew, total: grew.reduce(0) { $0 + $1.delta }, accent: true,
                           footer: "Click a folder to open it in Explore.",
                           link: grew.count > 7 ? (showAllGrew ? "Show fewer" : "See all \(grew.count) →") : nil) { showAllGrew.toggle() }
                    .layoutPriority(1)
                changeCard(title: "Shrank", entries: shrank, total: shrank.reduce(0) { $0 + $1.delta }, accent: false,
                           footer: cleanupFooter, link: model.lastCleanup != nil ? "Open History →" : nil) { model.destination = .history }
                    .frame(maxWidth: widthClass == .compact ? .infinity : 520)
            }
        }
    }

    private var cleanupFooter: String {
        guard let cleanup = model.lastCleanup, let before = record(beforeID), cleanup.date > before.header.capturedAt else {
            return "Folders that got smaller since then."
        }
        return "Part of this is your cleanup " + RelativeTime.string(since: cleanup.date) + "."
    }

    private func changeCard(title: String, entries: [SnapshotComparison.Entry], total: Int64, accent: Bool,
                            footer: String, link: String?, linkAction: @escaping () -> Void) -> some View {
        let shown = title == "Grew" && !showAllGrew ? Array(entries.prefix(7)) : Array(entries.prefix(25))
        return VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text(title).font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
                Text("\(entries.count) folder\(entries.count == 1 ? "" : "s")").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                Spacer()
                Text(Self.signed(total)).font(.system(size: DiskMapType.scaled(14), weight: .medium, design: .monospaced))
                    .foregroundStyle(accent ? DiskMapTheme.accent : DiskMapTheme.ink)
            }
            .padding(.horizontal, 20)
            .frame(height: 50)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            if shown.isEmpty {
                Text(title == "Grew" ? "Nothing grew noticeably." : "Nothing shrank noticeably.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3)
                    .frame(maxWidth: .infinity, minHeight: 80)
            }
            ForEach(shown) { entry in
                ChangeRow(name: entry.name.isEmpty ? "Everything" : entry.name,
                          path: displayPath(entry), before: entry.before, after: entry.after,
                          accent: accent, maxDelta: abs(entries.first?.delta ?? 1), compact: widthClass == .compact) { open(entry) }
            }
            Spacer(minLength: 0)
            HStack {
                Text(footer).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                Spacer(minLength: 8)
                if let link {
                    Button(link, action: linkAction).buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
                }
            }
            .padding(.horizontal, 20)
            .frame(height: 46)
            .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func displayPath(_ entry: SnapshotComparison.Entry) -> String {
        let abs = comparison?.absolutePath(of: entry) ?? entry.path
        let display = CanonicalPath.displayPath(absolutePath: abs)
        if display.hasPrefix("~") { return display }
        return DuplicatesView.place(abs, root: model.rootURL?.path ?? "")
    }

    /// Opens a changed folder in Explore, if it is still there.
    private func open(_ entry: SnapshotComparison.Entry) {
        let path = comparison?.absolutePath(of: entry) ?? entry.path
        guard let tree = model.tree, let root = model.rootURL,
              case .found(let id) = FileQuery.node(atPath: path, tree: tree, rootPath: root.path) else {
            model.showToast("This isn't on disk any more")
            return
        }
        model.currentNode = tree.isDirectory[Int(id)] ? id : max(0, tree.parent[Int(id)])
        model.selectedNode = id
        model.destination = .visualize
    }

    // MARK: Words

    /// "+18.6 GB", "−4.3 GB".
    static func signed(_ bytes: Int64) -> String {
        bytes == 0 ? "±0" : (bytes > 0 ? "+" : "−") + ByteFormat.hero(abs(bytes))
    }

    /// "since yesterday", "since Thursday", "since last week", "since 10 Oct".
    static func sinceText(_ date: Date, now: Date = Date()) -> String {
        let calendar = Calendar(identifier: .gregorian)
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: date), to: calendar.startOfDay(for: now)).day ?? 0
        switch days {
        case ..<1: return "since earlier today"
        case 1: return "since yesterday"
        case 2...6:
            let f = DateFormatter()
            f.locale = Locale(identifier: "en_GB")
            f.dateFormat = "EEEE"
            return "since " + f.string(from: date)
        case 7...13: return "since last week"
        default: return "since " + shortDate(date)
        }
    }

    /// "10 Oct".
    static func shortDate(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_GB")
        f.dateFormat = "d MMM"
        return f.string(from: date)
    }

    /// Hotspots into what grew (biggest first) and what shrank (biggest drop first).
    static func split(_ entries: [SnapshotComparison.Entry]) -> (grew: [SnapshotComparison.Entry], shrank: [SnapshotComparison.Entry]) {
        (entries.filter { $0.delta > 0 }.sorted { $0.delta > $1.delta },
         entries.filter { $0.delta < 0 }.sorted { $0.delta < $1.delta })
    }

    // MARK: Save sheet

    private var saveSheet: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Save this scan").font(DiskMapType.heading)
            Text("Keeps the sizes from this scan so you can see what changed next time. It doesn't copy or back up your files.")
                .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
            Text("Name").font(DiskMapType.secondary.weight(.semibold)).foregroundStyle(DiskMapTheme.ink2)
            TextField("Name", text: $saveName).textFieldStyle(.roundedBorder)
            Text("Optional note").font(DiskMapType.secondary.weight(.semibold)).foregroundStyle(DiskMapTheme.ink2)
            TextField("e.g. Before cleaning Docker", text: $saveNote, axis: .vertical)
                .lineLimit(3...5).textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("Cancel") { showSave = false }.buttonStyle(SecondaryButtonStyle()).keyboardShortcut(.cancelAction)
                Button("Save") { saveSnapshot() }.buttonStyle(PrimaryButtonStyle()).keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 420)
        .background(DiskMapTheme.raised)
    }

    // MARK: Actions

    private func reload() {
        guard let root = model.rootURL else {
            records = []
            return
        }
        records = SnapshotStore.records(in: storeDirectory, rootPath: root.path)
        // Today against the most recent saved scan.
        if afterID == nil { afterID = currentRecord?.id ?? records.first?.id }
        if beforeID == nil || record(beforeID) == nil { beforeID = records.first { $0.id != afterID }?.id }
        scheduleCompare()
    }

    private func prepareSave() {
        saveName = SnapshotMeta.defaultName(for: Date())
        saveNote = ""
        showSave = true
    }

    private func saveSnapshot() {
        guard let tree = model.tree, let root = model.rootURL else { return }
        let vol = VolumeStats.forPath(root.path)
        let name = saveName.trimmingCharacters(in: .whitespacesAndNewlines)
        let meta = SnapshotMeta(
            name: name.isEmpty ? SnapshotMeta.defaultName(for: Date()) : name,
            note: saveNote.trimmingCharacters(in: .whitespacesAndNewlines),
            volumeName: vol?.volumeName, totalBytes: vol?.totalBytes, freeBytes: vol?.freeBytes, usedBytes: vol?.usedBytes,
            scannedBytes: model.selectedTotals.first, fileCount: model.descendantFileCounts.first,
            folderCount: model.descendantFolderCounts.first, scanSeconds: model.lastScanSeconds,
            diskMapVersion: "freedisk.space"
        )
        do {
            _ = try SnapshotStore.save(DiskSnapshot(rootPath: root.path, capturedAt: Date(), tree: tree), meta: meta, in: storeDirectory)
            showSave = false
            reload()
            model.showToast("Scan saved")
        } catch {
            statusMessage = "Could not save this scan"
        }
    }

    private func stageSnapshot(_ rec: SnapshotRecord) async {
        guard !rec.isCurrent else { return }
        let urls = [rec.url, SnapshotStore.metaURL(for: rec.url)].filter { FileManager.default.fileExists(atPath: $0.path) }
        let requests = urls.map { url in
            let values = try? url.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .fileSizeKey])
            return CleanupStageRequest(url: url, size: Int64(values?.totalFileAllocatedSize ?? values?.fileSize ?? 0),
                                       reason: "Saved scan: \(rec.displayName)")
        }
        let result = await model.stageForCleanup(requests)
        confirmDelete = nil
        model.showToast(result.added > 0 ? "Saved scan added to the basket" : "That saved scan is already in the basket")
    }

    private func toggleFavorite(_ rec: SnapshotRecord) {
        var meta = rec.meta
        meta.favorite.toggle()
        try? SnapshotStore.saveMeta(meta, for: rec.url)
        reload()
    }

    private var canCompare: Bool {
        guard let beforeID, let afterID, beforeID != afterID else { return false }
        return record(beforeID) != nil && record(afterID) != nil
    }

    /// Selection changes land here; a newer choice cancels an older compare.
    private func scheduleCompare() {
        compareTask?.cancel()
        guard canCompare else {
            comparison = nil
            hotspots = []
            return
        }
        compareTask = Task { await runCompare() }
    }

    /// Loading two trees and rolling both up takes seconds on a large home,
    /// so it runs off the main thread (it used to freeze the window).
    private func runCompare() async {
        guard canCompare, let beforeID, let afterID,
              let beforeRec = record(beforeID), let afterRec = record(afterID) else { return }
        if let done = comparedIDs, done.before == beforeID, done.after == afterID, comparison != nil { return }
        isComparing = true
        statusMessage = nil
        let basis = model.sizeBasis
        let current = model.tree.flatMap { tree in model.rootURL.map { DiskSnapshot(rootPath: $0.path, capturedAt: Date(), tree: tree) } }
        let work = Task.detached(priority: .userInitiated) { () -> (SnapshotComparison, [SnapshotComparison.Entry])? in
            func load(_ rec: SnapshotRecord) -> DiskSnapshot? { rec.isCurrent ? current : try? SnapshotStore.load(from: rec.url) }
            guard let before = load(beforeRec), !Task.isCancelled, let after = load(afterRec), !Task.isCancelled else { return nil }
            let built = SnapshotComparison(before: before, after: after, basis: basis)
            return (built, built.hotspots(minimumChange: built.defaultMinimumChange, limit: 60))
        }
        let result = await withTaskCancellationHandler { await work.value } onCancel: { work.cancel() }
        guard !Task.isCancelled else { return }
        isComparing = false
        guard let (built, spots) = result else {
            comparison = nil
            statusMessage = "Could not read those scans"
            return
        }
        comparison = built
        hotspots = spots
        comparedIDs = (beforeID, afterID)
    }
}

// MARK: - Chart

/// Bars for each saved scan, today in ink with what it grew hatched on top.
struct UsageChart: View {
    enum Role { case past, before, after }
    struct Point: Identifiable, Equatable {
        var id: String
        var label: String
        var bytes: Int64
        var role: Role
        var note: String?
    }
    var points: [Point]
    var pick: (String) -> Void

    /// Axis bounds and step, in the chart's unit, for a few round grid lines.
    static func axis(_ values: [Double]) -> (low: Double, high: Double, step: Double) {
        guard let lo = values.min(), let hi = values.max() else { return (0, 1, 1) }
        let span = max(hi - lo, hi * 0.08, 1e-9)
        let raw = span / 4
        let magnitude = pow(10, floor(log10(raw)))
        let step = [1.0, 2, 5, 10].map { $0 * magnitude }.first { $0 >= raw } ?? magnitude * 10
        let low = max(0, floor((lo - span * 0.35) / step) * step)
        let high = ceil((hi + span * 0.15) / step) * step
        return (low, max(high, low + step), step)
    }

    /// GB when anything is a gigabyte or more, otherwise MB.
    static func unit(_ points: [Point]) -> (divisor: Double, name: String) {
        (points.map(\.bytes).max() ?? 0) >= 1_000_000_000 ? (1e9, "GB") : (1e6, "MB")
    }

    static func figure(_ value: Double) -> String {
        value >= 100 ? String(Int(value.rounded())) : String(format: "%.1f", value)
    }

    var body: some View {
        let unit = Self.unit(points)
        let values = points.map { Double($0.bytes) / unit.divisor }
        let axis = Self.axis(values)
        GeometryReader { geo in
            let left: CGFloat = 44, bottom: CGFloat = 26, top: CGFloat = 26
            let plot = CGRect(x: left, y: top, width: max(10, geo.size.width - left), height: max(10, geo.size.height - top - bottom))
            let slot = plot.width / CGFloat(max(points.count, 1))
            let barWidth = min(86, slot * 0.46)
            let y: (Double) -> CGFloat = { v in plot.maxY - CGFloat((v - axis.low) / (axis.high - axis.low)) * plot.height }
            let before = points.first { $0.role == .before }.map { Double($0.bytes) / unit.divisor }
            ZStack(alignment: .topLeading) {
                Canvas { ctx, _ in
                    var grid = Path()
                    var v = axis.low
                    while v <= axis.high + axis.step / 2 {
                        grid.move(to: CGPoint(x: plot.minX, y: y(v)))
                        grid.addLine(to: CGPoint(x: plot.maxX, y: y(v)))
                        ctx.draw(Text(Self.figure(v)).font(.system(size: 11, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3),
                                 at: CGPoint(x: plot.minX - 8, y: y(v)), anchor: .trailing)
                        v += axis.step
                    }
                    ctx.stroke(grid, with: .color(DiskMapTheme.line), lineWidth: 1)
                    var base = Path()
                    base.move(to: CGPoint(x: plot.minX, y: plot.maxY))
                    base.addLine(to: CGPoint(x: plot.maxX, y: plot.maxY))
                    ctx.stroke(base, with: .color(DiskMapTheme.ink.opacity(0.8)), lineWidth: 1)
                    ctx.draw(Text(unit.name + " SCANNED").font(.system(size: 10.5, weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3),
                             at: CGPoint(x: plot.minX, y: 6), anchor: .leading)

                    for (i, point) in points.enumerated() {
                        let value = values[i]
                        let cx = plot.minX + slot * (CGFloat(i) + 0.5)
                        let topY = y(value)
                        let fill: Color = point.role == .after ? DiskMapTheme.ink : point.role == .before ? DiskMapTheme.ink.opacity(0.5) : DiskMapTheme.ink.opacity(0.17)
                        if point.role == .after, let before, value > before {
                            let solid = CGRect(x: cx - barWidth / 2, y: y(before), width: barWidth, height: plot.maxY - y(before))
                            ctx.fill(Path(solid), with: .color(fill))
                            let grown = CGRect(x: cx - barWidth / 2, y: topY, width: barWidth, height: y(before) - topY)
                            HatchFill.draw(Path(grown), in: grown, context: &ctx)
                            ctx.stroke(Path(grown), with: .color(DiskMapTheme.accent), lineWidth: 1)
                            ctx.draw(Text("+" + Self.figure(value - before)).font(.system(size: 12, weight: .semibold, design: .monospaced)).foregroundStyle(DiskMapTheme.accentDeep),
                                     at: CGPoint(x: cx - barWidth / 2 - 8, y: y(before) - 10), anchor: .trailing)
                        } else {
                            ctx.fill(Path(CGRect(x: cx - barWidth / 2, y: topY, width: barWidth, height: plot.maxY - topY)), with: .color(fill))
                        }
                        ctx.draw(Text(Self.figure(value)).font(.system(size: 12, weight: point.role == .after ? .semibold : .regular, design: .monospaced))
                                    .foregroundStyle(point.role == .past ? DiskMapTheme.ink2 : DiskMapTheme.ink),
                                 at: CGPoint(x: cx, y: topY - 10), anchor: .center)
                        if let note = point.note {
                            ctx.draw(Text(note.lowercased()).font(.system(size: 11)).foregroundStyle(DiskMapTheme.ink3),
                                     at: CGPoint(x: cx, y: topY - 28), anchor: .center)
                        }
                        ctx.draw(Text(point.label).font(.system(size: 11, weight: point.role == .past ? .regular : .semibold, design: .monospaced))
                                    .foregroundStyle(point.role == .past ? DiskMapTheme.ink3 : DiskMapTheme.ink),
                                 at: CGPoint(x: cx, y: plot.maxY + 14), anchor: .center)
                    }
                    // A dashed line at the comparison height, from that bar to today.
                    if let before, let bi = points.firstIndex(where: { $0.role == .before }), let ai = points.firstIndex(where: { $0.role == .after }), ai > bi {
                        var dash = Path()
                        dash.move(to: CGPoint(x: plot.minX + slot * (CGFloat(bi) + 0.5) + barWidth / 2, y: y(before)))
                        dash.addLine(to: CGPoint(x: plot.minX + slot * (CGFloat(ai) + 0.5) - barWidth / 2, y: y(before)))
                        ctx.stroke(dash, with: .color(DiskMapTheme.accentDeep.opacity(0.8)), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    }
                }
                // Click a bar to compare today with it.
                HStack(spacing: 0) {
                    ForEach(points) { point in
                        Color.clear.contentShape(Rectangle())
                            .onTapGesture { pick(point.id) }
                            .help(point.role == .after ? "Today" : "Compare today with this scan")
                            .accessibilityElement()
                            .accessibilityLabel("\(point.label), \(ByteFormat.string(point.bytes))")
                            .accessibilityAddTraits(.isButton)
                    }
                }
                .frame(width: plot.width, height: geo.size.height)
                .offset(x: plot.minX)
            }
        }
    }
}

/// "CoreSimulator  ~/Library/…   22.8 → 31.2 GB   +8.4 GB  ›".
private struct ChangeRow: View {
    var name: String
    var path: String
    var before: Int64
    var after: Int64
    var accent: Bool
    var maxDelta: Int64
    var compact: Bool
    var open: () -> Void
    @State private var hovering = false

    var body: some View {
        let delta = after - before
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 2) {
                Text(name).font(.system(size: DiskMapType.scaled(13.5), weight: .medium)).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                Text(path).font(.system(size: DiskMapType.scaled(11), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1).truncationMode(.middle)
            }
            .padding(.vertical, 6)
            .padding(.horizontal, 10)
            .background(alignment: .leading) {
                // How big the change is, against the biggest in this list.
                GeometryReader { geo in
                    Rectangle().fill(accent ? DiskMapTheme.accentWash : DiskMapTheme.soft)
                        .frame(width: geo.size.width * CGFloat(min(1, Double(abs(delta)) / Double(max(1, maxDelta)))))
                }
            }
            .frame(minWidth: 120, maxWidth: .infinity, alignment: .leading)
            .layoutPriority(1)
            if !compact {
                ZStack(alignment: .trailing) {
                    Text(SnapshotsView.beforeAfter(before, after)).opacity(hovering ? 0 : 1)
                    Text("Open in Explore →").foregroundStyle(DiskMapTheme.accentDeep).opacity(hovering ? 1 : 0)
                }
                .font(.system(size: DiskMapType.scaled(11.5), design: .monospaced))
                .foregroundStyle(DiskMapTheme.ink3).lineLimit(1).fixedSize()
            }
            Text(SnapshotsView.signed(delta)).font(.system(size: DiskMapType.scaled(13), weight: .medium, design: .monospaced))
                .foregroundStyle(accent ? DiskMapTheme.accentDeep : DiskMapTheme.ink).lineLimit(1).fixedSize()
                .frame(minWidth: 76, alignment: .trailing)
            Image(systemName: "chevron.right").font(.system(size: 10, weight: .medium)).foregroundStyle(DiskMapTheme.ink3)
                .frame(width: 14, alignment: .trailing)
        }
        .padding(.leading, 10)
        .padding(.trailing, 20)
        .frame(height: 58)
        .background(hovering ? DiskMapTheme.accentWash.opacity(0.4) : Color.clear)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture(perform: open)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(name), \(SnapshotsView.signed(delta)), from \(ByteFormat.string(before)) to \(ByteFormat.string(after))")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: "Open in Explore", open)
    }
}

extension SnapshotsView {
    /// "22.8 → 31.2 GB", both in the larger figure's unit.
    static func beforeAfter(_ before: Int64, _ after: Int64) -> String {
        let big = max(before, after)
        let (div, unit): (Double, String) = big >= 1_000_000_000 ? (1e9, "GB") : big >= 1_000_000 ? (1e6, "MB") : (1e3, "KB")
        let f = { (v: Int64) in String(format: "%.1f", Double(v) / div) }
        return "\(f(before)) → \(f(after)) \(unit)"
    }
}
