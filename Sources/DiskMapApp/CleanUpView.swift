import AppKit
import DiskMapCore
import SwiftUI

/// Clean up (redesign board 02): one list, already ticked with the things that
/// rebuild by themselves, and a receipt of exactly what happens. The button
/// stages the ticked items and opens the final Move to Trash confirmation —
/// staging and committing stay two separate actions.
struct CleanUpView: View {
    @ObservedObject var model: ScanModel
    var pickFolder: () -> Void

    @Environment(\.widthClass) private var widthClass
    @State private var ticked: Set<String> = []
    @State private var tickedGeneration = -1
    @State private var expanded: Set<CleanGroup> = []
    @State private var isMoving = false

    enum CleanGroup: Int, CaseIterable, Identifiable {
        case rebuilds, reviewFirst, downloads, media
        var id: Int { rawValue }
        var title: String {
            switch self {
            case .rebuilds: return "Rebuilds by itself"
            case .reviewFirst: return "Comes back, but check first"
            case .downloads: return "Old downloads"
            case .media: return "Your files"
            }
        }
        var subtitle: String {
            switch self {
            case .rebuilds: return "Apps and tools make these again"
            case .reviewFirst: return "Simulators, dependencies and other things you can get back"
            case .downloads: return "Not opened in a long time"
            case .media: return "Never ticked for you. Look before you add them"
            }
        }
        /// How many rows show before "N more".
        var preview: Int { self == .rebuilds ? 8 : 4 }
    }

    struct CleanItem: Identifiable, Sendable {
        var id: String
        var group: CleanGroup
        var title: String
        var why: String
        var bytes: Int64
        var tag: String?
        var requests: [CleanupStageRequest]
        var revealURL: URL
    }

    var body: some View {
        Group {
            if model.tree == nil {
                FirstScanHero(model: model, pickFolder: pickFolder, onScanMac: {
                    Task { await model.scan(FileManager.default.homeDirectoryForCurrentUser) }
                })
            } else {
                content
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(DiskMapTheme.page)
        .task(id: model.catalogGeneration) {
            await model.ensureCatalog(.reviewables)
            await model.ensureCatalog(.oldDownloads)
            await model.ensureCatalog(.largeMedia)
            resetTicksIfNeeded()
        }
    }

    private var ready: Bool {
        model.isCatalogReady(.reviewables) && model.isCatalogReady(.oldDownloads) && model.isCatalogReady(.largeMedia)
    }

    private func resetTicksIfNeeded() {
        guard ready, tickedGeneration != model.catalogGeneration else { return }
        tickedGeneration = model.catalogGeneration
        ticked = Self.defaultTicks(items, isStaged: model.isStaged)
    }

    // MARK: Items

    private var items: [CleanItem] {
        Self.buildItems(reviewables: model.cachedReviewables, downloads: model.cachedOldDownloads.candidates,
                        media: model.cachedLargeMedia.candidates, totals: model.selectedTotals, rootPath: model.rootURL?.path)
    }

    /// Every Clean up row, biggest first. Protected items never appear; a file
    /// that is both an old download and large media is listed once (downloads).
    static func buildItems(reviewables: [ReviewableTarget], downloads: [OldDownloadsCandidate], media: [MediaCandidate],
                           totals: [Int64], rootPath: String?) -> [CleanItem] {
        var out: [CleanItem] = []
        for t in reviewables where !t.isProtected {
            var reqs: [CleanupStageRequest] = []
            for (i, path) in t.paths.enumerated() {
                var size = t.bytes / Int64(max(1, t.paths.count))
                if i < t.nodeIDs.count, Int(t.nodeIDs[i]) < totals.count { size = totals[Int(t.nodeIDs[i])] }
                reqs.append(CleanupStageRequest(url: URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL,
                                                size: size, reason: "Clean up: " + t.displayName))
            }
            let places = t.paths.count > 1 ? "\(t.paths.count) places · " : ""
            out.append(CleanItem(id: "r-" + t.id, group: t.isGenerallySafe ? .rebuilds : .reviewFirst,
                                 title: t.displayName, why: places + (t.consequence.isEmpty ? t.detail : t.consequence),
                                 bytes: t.bytes, tag: nil, requests: reqs,
                                 revealURL: URL(fileURLWithPath: t.primaryPath)))
        }
        for c in downloads where c.safety.level != .protected {
            out.append(CleanItem(id: "d-\(c.nodeID)", group: .downloads, title: c.name,
                                 why: "\(age(c.ageDays)) · \(place(c.absolutePath, rootPath: rootPath))", bytes: c.bytes, tag: nil,
                                 requests: [CleanupStageRequest(url: URL(fileURLWithPath: c.absolutePath), size: c.bytes, reason: "Old download: " + c.name)],
                                 revealURL: URL(fileURLWithPath: c.absolutePath)))
        }
        let downloadIDs = Set(downloads.map(\.nodeID))
        for m in media where m.safety.level != .protected && !downloadIDs.contains(m.nodeID) {
            out.append(CleanItem(id: "m-\(m.nodeID)", group: .media, title: m.name,
                                 why: "\(age(m.ageDays)) · \(place(m.absolutePath, rootPath: rootPath))", bytes: m.bytes, tag: nil,
                                 requests: [CleanupStageRequest(url: URL(fileURLWithPath: m.absolutePath), size: m.bytes, reason: "Large media: " + m.name)],
                                 revealURL: URL(fileURLWithPath: m.absolutePath)))
        }
        return out.filter { $0.bytes > 0 }.sorted { $0.bytes > $1.bytes }
    }

    /// Settings › Clean up › What to tick for you.
    static let tickKey = "CleanUp.TickRebuilds"

    /// What Clean up ticks by itself: only things that rebuild by themselves,
    /// or nothing when the person chose to tick everything themselves.
    static func defaultTicks(_ items: [CleanItem], isStaged: (URL) -> Bool,
                             tickRebuilds: Bool = UserDefaults.standard.object(forKey: tickKey) as? Bool ?? true) -> Set<String> {
        guard tickRebuilds else { return [] }
        return Set(items.filter { $0.group == .rebuilds && !isStaged($0.revealURL) }.map(\.id))
    }

    /// The folder a file sits in, relative to the scan ("Downloads/misc").
    static func place(_ absolutePath: String, rootPath: String?) -> String {
        let parent = (absolutePath as NSString).deletingLastPathComponent
        if let root = rootPath, parent == root || parent.hasPrefix(root + "/") {
            let rel = String(parent.dropFirst(root.count)).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            return rel.isEmpty ? (root as NSString).lastPathComponent : rel
        }
        return CanonicalPath.displayPath(absolutePath: parent)
    }

    static func age(_ days: Int32) -> String {
        if days < 1 { return "today" }
        if days < 14 { return "\(days) days old" }
        if days < 60 { return "\(days / 7) weeks old" }
        if days < 730 { return "\(days / 30) months old" }
        return "\(days / 365) years old"
    }

    // MARK: Layout

    private var content: some View {
        let all = items
        let tickedItems = all.filter { ticked.contains($0.id) }
        return HStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    header(all)
                    if !ready {
                        HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Sorting what's safe to free…").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2) }
                            .padding(.top, 40)
                    } else if all.isEmpty {
                        Text("Nothing to clean up in this scan. Explore shows everything that takes space.")
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).padding(.top, 32)
                    } else {
                        ForEach(CleanGroup.allCases) { group in
                            let rows = all.filter { $0.group == group }
                            if !rows.isEmpty { section(group, rows) }
                        }
                    }
                }
                .padding(.horizontal, widthClass.pagePadding + 6)
                .padding(.vertical, widthClass.pagePadding)
            }
            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
            receiptColumn(tickedItems)
                .frame(width: receiptWidth)
        }
    }

    private var receiptWidth: CGFloat {
        switch widthClass { case .compact: return 340; case .regular: return 420; case .wide: return 520 }
    }

    private func header(_ all: [CleanItem]) -> some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Choose what to free").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink)
                Text("We ticked only things that rebuild by themselves. Everything else waits for you.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
            }
            Spacer(minLength: 12)
            if ready, !all.isEmpty {
                HStack(spacing: 8) {
                    Button("Tick safe only") { ticked = Set(all.filter { $0.group == .rebuilds }.map(\.id)) }
                        .buttonStyle(SecondaryButtonStyle())
                    Button("Untick all") { ticked.removeAll() }
                        .buttonStyle(SecondaryButtonStyle())
                        .disabled(ticked.isEmpty)
                }
            }
        }
        .padding(.bottom, 6)
    }

    private func section(_ group: CleanGroup, _ rows: [CleanItem]) -> some View {
        let ids = Set(rows.map(\.id))
        let on = ids.intersection(ticked)
        let state: TickBox.State = on.isEmpty ? .off : on.count == ids.count ? .on : .mixed
        let total = rows.reduce(Int64(0)) { $0 + $1.bytes }
        let open = expanded.contains(group)
        let shown = open ? rows : Array(rows.prefix(group.preview))
        return VStack(spacing: 0) {
            HStack(spacing: 12) {
                TickBox(state: state) {
                    if state == .on { ticked.subtract(ids) } else { ticked.formUnion(ids) }
                }
                .accessibilityLabel("\(group.title): \(state == .on ? "all ticked" : state == .mixed ? "some ticked" : "none ticked")")
                Text(group.title).font(.system(size: DiskMapType.scaled(15.5), weight: .semibold)).foregroundStyle(DiskMapTheme.ink)
                Text(group.subtitle).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                Spacer(minLength: 8)
                Text(ByteFormat.string(total)).font(.system(size: DiskMapType.scaled(13.5), weight: .semibold, design: .monospaced)).foregroundStyle(DiskMapTheme.ink)
            }
            .padding(.top, 22)
            .padding(.bottom, 10)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.ink).frame(height: 1) }
            ForEach(shown) { item in
                CleanRow(item: item, ticked: ticked.contains(item.id), staged: model.isStaged(item.revealURL)) {
                    if ticked.contains(item.id) { ticked.remove(item.id) } else { ticked.insert(item.id) }
                }
            }
            if rows.count > group.preview {
                let hidden = rows.dropFirst(group.preview)
                Button {
                    withAnimation(.easeOut(duration: 0.15)) { if open { expanded.remove(group) } else { expanded.insert(group) } }
                } label: {
                    HStack {
                        Text(open ? "Show fewer" : "\(hidden.count) more · \(ByteFormat.string(hidden.reduce(0) { $0 + $1.bytes }))")
                            .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accent)
                        Spacer()
                    }
                    .padding(.leading, 31)
                    .frame(height: 34)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    // MARK: Receipt

    private func receiptColumn(_ picked: [CleanItem]) -> some View {
        let bytes = picked.reduce(Int64(0)) { $0 + $1.bytes }
        let vol = model.analysis.volume
        let skipped = ScanModel.splitForBulkStage(picked.flatMap(\.requests)).skipped
        return ZStack(alignment: .top) {
            DiskMapTheme.sidebar
            BoardGrid().opacity(0.8)
            ScrollView {
                VStack(spacing: 0) {
                    Receipt(lines: picked.map { ($0.title, $0.bytes) }, total: bytes,
                            freeBefore: vol.map { Int64($0.freeBytes) }, volumeName: vol?.volumeName ?? model.rootURL?.lastPathComponent ?? "This Mac",
                            skippedCount: skipped.count, compact: widthClass == .compact)
                    Button {
                        Task { await moveTicked(picked) }
                    } label: {
                        VStack(alignment: .leading, spacing: widthClass == .compact ? 10 : 18) {
                            Text("⏎ ENTER").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).opacity(0.85)
                            Text(picked.isEmpty ? "Tick something to free" : "Move \(picked.count) item\(picked.count == 1 ? "" : "s") to Trash")
                                .font(.system(size: DiskMapType.scaled(widthClass == .compact ? 19 : 23), weight: .semibold))
                                .kerning(-0.4)
                        }
                        .foregroundStyle(DiskMapTheme.onAccent)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 20)
                        .padding(.vertical, 16)
                        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(DiskMapTheme.accent.opacity(picked.isEmpty ? 0.45 : 1)))
                        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(Color.black.opacity(0.85), lineWidth: 1))
                        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.black.opacity(0.88)).offset(x: 5, y: 5))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .keyboardShortcut(.defaultAction)
                    .disabled(picked.isEmpty || isMoving)
                    .padding(.top, widthClass == .compact ? 22 : 32)
                    .padding(.trailing, 5)
                    .accessibilityLabel(picked.isEmpty ? "Nothing ticked" : "Move \(picked.count) items to Trash, \(ByteFormat.string(bytes))")
                    Text("You’ll see the full list once more before anything moves.")
                        .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                        .multilineTextAlignment(.center)
                        .padding(.top, 12)
                    if let last = model.lastCleanup, !last.items.isEmpty {
                        lastCleanupCard(last).padding(.top, 28)
                    }
                }
                .padding(.horizontal, widthClass == .compact ? 18 : 28)
                .padding(.vertical, widthClass.pagePadding)
            }
        }
    }

    private func lastCleanupCard(_ record: CleanupRecord) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Last cleanup").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
            HStack(spacing: 10) {
                Text("\(record.date.formatted(.dateTime.weekday(.wide))) · \(record.items.count) item\(record.items.count == 1 ? "" : "s")")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                Spacer(minLength: 6)
                Text(ByteFormat.string(record.items.reduce(0) { $0 + $1.bytes })).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                Button("Put Back") { Task { await model.putBackLastCleanup() } }.buttonStyle(SecondaryButtonStyle())
            }
            .padding(.horizontal, 14)
            .frame(height: 50)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(DiskMapTheme.line, style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
        }
    }

    private func moveTicked(_ picked: [CleanItem]) async {
        guard !picked.isEmpty, !isMoving else { return }
        isMoving = true
        defer { isMoving = false }
        let (safe, skipped) = ScanModel.splitForBulkStage(picked.flatMap(\.requests))
        guard !safe.isEmpty else {
            model.showToast("\(countLabel(skipped.count, "risky item")) — add \(skipped.count == 1 ? "it" : "them") from Explore to see why")
            return
        }
        let summary = await model.stageForCleanup(safe)
        if summary.wasBusy { model.showToast("Still adding the last selection — try again in a moment"); return }
        if !skipped.isEmpty { model.showToast("Skipped \(countLabel(skipped.count, "risky item")) — add \(skipped.count == 1 ? "it" : "them") one at a time to see why") }
        ticked.subtract(picked.map(\.id))
        model.isCleanupQueuePresented = true
    }
}

/// The paper receipt: what moves, what you get back, where it goes, the undo.
private struct Receipt: View {
    var lines: [(String, Int64)]
    var total: Int64
    var freeBefore: Int64?
    var volumeName: String
    var skippedCount: Int
    var compact: Bool

    var body: some View {
        let maxLines = compact ? 4 : 7
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text("freedisk.space").font(.system(size: DiskMapType.scaled(15), weight: .semibold)).foregroundStyle(Color.black)
                Spacer()
                Text("CLEANUP").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(Color.black.opacity(0.4))
            }
            Text("\(Date().formatted(date: .omitted, time: .shortened)) · \(volumeName)")
                .font(DiskMapType.secondary).foregroundStyle(Color.black.opacity(0.55)).padding(.top, 3)
            dashed
            if lines.isEmpty {
                Text("Nothing ticked yet.").font(DiskMapType.body).foregroundStyle(Color.black.opacity(0.45)).padding(.vertical, 6)
            }
            ForEach(Array(lines.prefix(maxLines).enumerated()), id: \.offset) { _, line in
                dotted(line.0, ByteFormat.string(line.1))
            }
            if lines.count > maxLines {
                dotted("and \(lines.count - maxLines) more", ByteFormat.string(lines.dropFirst(maxLines).reduce(0) { $0 + $1.1 }), muted: true)
            }
            if skippedCount > 0 {
                Text("\(skippedCount) risky item\(skippedCount == 1 ? "" : "s") will be skipped. Add \(skippedCount == 1 ? "it" : "them") on \(skippedCount == 1 ? "its" : "their") own to see why.")
                    .font(DiskMapType.secondary).foregroundStyle(Color.black.opacity(0.55)).padding(.top, 4)
                    .fixedSize(horizontal: false, vertical: true)
            }
            dashed
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text("You get back").font(.system(size: DiskMapType.scaled(16), weight: .semibold)).foregroundStyle(Color.black)
                    .lineLimit(1).fixedSize()
                DottedLeader()
                Text(ByteFormat.string(total)).font(.system(size: DiskMapType.scaled(16), weight: .semibold, design: .monospaced)).foregroundStyle(DiskMapTheme.accent)
                    .lineLimit(1).fixedSize()
            }
            .padding(.vertical, 6)
            if let freeBefore {
                dotted("Free space after", "\(ByteFormat.hero(freeBefore)) → \(ByteFormat.hero(freeBefore + total))")
            }
            dotted("Where it goes", "The Trash", mono: false)
            dotted("Deleted forever", "Nothing", mono: false)
            dotted("Undo", "Put Back, any time", mono: false)
            if !compact {
                dashed
                Text("The space comes back when you empty the Trash. Put Back returns everything to the same place, with the same name.")
                    .font(DiskMapType.secondary).foregroundStyle(Color.black.opacity(0.55))
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, compact ? 18 : 26)
        .padding(.top, compact ? 18 : 24)
        .padding(.bottom, compact ? 20 : 28)
        .background(ReceiptPaper().fill(Color.white))
        .overlay(ReceiptPaper().stroke(Color.black.opacity(0.1), lineWidth: 1))
        .shadow(color: .black.opacity(0.18), radius: 22, x: 0, y: 18)
        .environment(\.colorScheme, .light)
        .accessibilityElement(children: .combine)
    }

    private var dashed: some View {
        Line().stroke(Color.black.opacity(0.22), style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
            .frame(height: 1).padding(.vertical, 11)
    }

    private func dotted(_ label: String, _ value: String, muted: Bool = false, mono: Bool = true) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(label).font(DiskMapType.body).foregroundStyle(Color.black.opacity(muted ? 0.5 : 0.9)).lineLimit(1)
                .layoutPriority(1)
            DottedLeader()
            Text(value).font(mono ? DiskMapType.figure : DiskMapType.body).foregroundStyle(Color.black.opacity(muted ? 0.5 : 0.9)).lineLimit(1)
                .fixedSize()
        }
        .padding(.vertical, 4)
    }
}

private struct Line: Shape {
    func path(in rect: CGRect) -> Path { Path { $0.move(to: CGPoint(x: 0, y: rect.midY)); $0.addLine(to: CGPoint(x: rect.maxX, y: rect.midY)) } }
}

private struct DottedLeader: View {
    var body: some View {
        Line().stroke(Color.black.opacity(0.25), style: StrokeStyle(lineWidth: 1, dash: [1, 3]))
            .frame(height: 1).offset(y: -3).frame(minWidth: 12)
    }
}

/// A receipt with a torn, zig-zag bottom edge.
private struct ReceiptPaper: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        let tooth: CGFloat = 8, depth: CGFloat = 7
        p.move(to: CGPoint(x: rect.minX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        var x = rect.maxX
        var down = false
        while x > rect.minX {
            x = max(rect.minX, x - tooth)
            p.addLine(to: CGPoint(x: x, y: rect.maxY + (down ? 0 : depth)))
            down.toggle()
        }
        p.addLine(to: CGPoint(x: rect.minX, y: rect.minY))
        p.closeSubpath()
        return p
    }
}

/// A violet tick box: off, on, or mixed (some of a group).
struct TickBox: View {
    enum State { case off, on, mixed }
    var state: State
    var action: () -> Void
    var body: some View {
        Button(action: action) {
            RoundedRectangle(cornerRadius: 4, style: .continuous)
                .fill(state == .off ? Color.clear : DiskMapTheme.accent)
                .overlay(RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .stroke(state == .off ? DiskMapTheme.ink3 : DiskMapTheme.accent, lineWidth: 1.5))
                .overlay {
                    if state == .on { Image(systemName: "checkmark").font(.system(size: 9.5, weight: .bold)).foregroundStyle(.white) }
                    if state == .mixed { Rectangle().fill(.white).frame(width: 7, height: 1.6) }
                }
                .frame(width: 16, height: 16)
                .contentShape(Rectangle().inset(by: -6))
        }
        .buttonStyle(.plain)
    }
}

/// One Clean up row: tick, name, why, size; click anywhere to tick.
private struct CleanRow: View {
    var item: CleanUpView.CleanItem
    var ticked: Bool
    var staged: Bool
    var toggle: () -> Void
    @Environment(\.widthClass) private var widthClass
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 13) {
            TickBox(state: ticked ? .on : .off, action: toggle)
                .accessibilityLabel(ticked ? "Ticked" : "Not ticked")
            Text(item.title).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
            Text(item.why).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1).truncationMode(.middle)
            if staged { BoardTag(text: "In basket", tone: .accent) }
            if let tag = item.tag { BoardTag(text: tag, tone: .accent) }
            Spacer(minLength: 8)
            Text(ByteFormat.string(item.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
        }
        .padding(.leading, 3)
        .padding(.trailing, 2)
        .frame(height: widthClass == .compact ? 40 : widthClass == .regular ? 44 : 48)
        .background(hovering ? DiskMapTheme.hover : Color.clear)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.55)).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture(perform: toggle)
        .onHover { hovering = $0 }
        .contextMenu {
            Button(ticked ? "Untick" : "Tick", action: toggle)
            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([item.revealURL]) }
            Button("Quick Look") { DiskMapQuickLook.shared.show(item.revealURL) }
        }
        .help(item.revealURL.path)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(item.title), \(ByteFormat.string(item.bytes)), \(ticked ? "ticked" : "not ticked")")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: "Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([item.revealURL]) }
    }
}
