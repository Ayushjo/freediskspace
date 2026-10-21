import DiskMapCore
import SwiftUI

extension ScanModel {
    /// What Clean up ticks for you: reviewable items that are generally safe
    /// (they rebuild by themselves). Zero until the catalog is built.
    var safeReclaimableBytes: Int64 {
        cachedReviewables.reduce(0) { $0 + ($1.isGenerallySafe ? $1.bytes : 0) }
    }

    var safeReclaimables: [ReviewableTarget] {
        cachedReviewables.filter(\.isGenerallySafe).sorted { $0.bytes > $1.bytes }
    }

    /// Build output and dependencies that come back with a rebuild or
    /// reinstall, but are left unticked (Clean up's "check first" group).
    var reviewFirstReclaimableBytes: Int64 {
        cachedReviewables.reduce(0) { $0 + ($1.isReviewFirst ? $1.bytes : 0) }
    }

    /// Overview's line when nothing is ticked for you.
    var nothingTickedLine: String {
        let review = reviewFirstReclaimableBytes
        return review > 0
            ? "Nothing is ticked for you here. \(ByteFormat.string(review)) of build files and dependencies comes back with a rebuild or reinstall — check it in Clean up."
            : "Nothing here rebuilds by itself right now. Clean up still lists old downloads and big files to review."
    }
}

/// Home — answer "Why is my Mac full?" in ~5 seconds: free space, what you
/// can free, where it all went (redesign board 01).
struct OverviewView: View {
    @ObservedObject var model: ScanModel
    var pickFolder: () -> Void
    var onReviewCleanup: () -> Void
    var onExplain: () -> Void
    var onOpenVisualize: () -> Void
    var onSelectFile: (Int32) -> Void
    var onOpenBiggestFiles: () -> Void = {}

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.widthClass) private var widthClass
    @State private var showWhy = false
    @State private var hideUnreadable = false

    var body: some View {
        Group {
            if model.tree == nil {
                FirstScanHero(
                    model: model,
                    pickFolder: pickFolder,
                    onScanMac: {
                        let home = FileManager.default.homeDirectoryForCurrentUser
                        Task { await model.scan(home) }
                    }
                )
            } else {
                loaded
                    .task(id: model.catalogGeneration) { await model.ensureCatalog(.reviewables) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(DiskMapTheme.page)
    }

    private var snap: AnalysisSnapshot { model.analysis }
    private var gap: CGFloat { widthClass.gap }

    private var loaded: some View {
        GeometryReader { geo in
            ScrollView {
                VStack(alignment: .leading, spacing: gap) {
                    unreadableNotice
                    cloneNotice
                    HStack(alignment: .top, spacing: gap) {
                        freeCard
                            .frame(maxWidth: .infinity)
                        canFreeCard
                            .frame(width: max(300, (geo.size.width - widthClass.pagePadding * 2 - gap) * 0.42))
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    usageCard
                    HStack(alignment: .top, spacing: gap) {
                        biggestPlaces
                            .frame(maxWidth: .infinity)
                        readyToFree
                            .frame(width: max(300, (geo.size.width - widthClass.pagePadding * 2 - gap) * 0.44))
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    footer
                }
                .padding(widthClass.pagePadding)
                .frame(minHeight: geo.size.height, alignment: .top)
            }
        }
    }

    // MARK: Free space

    private var heroSize: CGFloat {
        switch widthClass { case .compact: return 88; case .regular: return 116; case .wide: return 146 }
    }

    private var freeCard: some View {
        ZStack(alignment: .topLeading) {
            BoardGrid().opacity(0.7)
            VStack(alignment: .leading, spacing: 0) {
                if let vol = snap.volume {
                    Text("Free space").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                    HStack(alignment: .lastTextBaseline, spacing: heroSize * 0.1) {
                        tightText(ByteFormat.hero(Int64(vol.freeBytes)), kerning: -heroSize * 0.05)
                            .font(DiskMapType.hero(heroSize))
                            .foregroundStyle(DiskMapTheme.ink)
                            .contentTransition(reduceMotion ? .identity : .numericText())
                            .accessibilityIdentifier("overview-free")
                        Text("of \(ByteFormat.hero(Int64(vol.totalBytes)))")
                            .font(.system(size: DiskMapType.scaled(heroSize * 0.27), weight: .medium))
                            .foregroundStyle(DiskMapTheme.ink3)
                    }
                    .lineLimit(1)
                    .minimumScaleFactor(0.5)
                    .padding(.top, 6)
                    Text(paceLine(vol))
                        .font(DiskMapType.body)
                        .foregroundStyle(DiskMapTheme.ink2)
                        .padding(.top, 12)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text((model.rootURL?.lastPathComponent ?? "This scan")).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                    tightText(ByteFormat.string(snap.scannedBytes), kerning: -heroSize * 0.05)
                        .font(DiskMapType.hero(heroSize))
                        .foregroundStyle(DiskMapTheme.ink)
                        .lineLimit(1).minimumScaleFactor(0.5)
                        .padding(.top, 6)
                }
                coverageLine.padding(.top, 6)
            }
            .padding(.horizontal, widthClass == .compact ? 24 : 36)
            .padding(.vertical, widthClass == .compact ? 22 : 32)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        }
        .frame(minHeight: widthClass == .compact ? 206 : widthClass == .regular ? 250 : 300)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    /// "30% of your disk is free. At this week's pace you'll be low in about 8 weeks."
    private func paceLine(_ vol: VolumeStats) -> String {
        let freePct = Int((Double(vol.freeBytes) / Double(max(1, vol.totalBytes)) * 100).rounded())
        var line = "\(freePct)% of your disk is free."
        if let c = model.weekComparison, c.isWeek, c.freeDelta < -Self.growthNoise {
            let weeklyLoss = Double(-c.freeDelta)
            let lowMark = Double(vol.totalBytes) * 0.1
            let weeks = (Double(vol.freeBytes) - lowMark) / weeklyLoss
            if weeks <= 0 {
                line += " You're already low on space."
            } else if weeks < 52 {
                let n = max(1, Int(weeks.rounded()))
                line += " At this week's pace you'll be low in about \(n) week\(n == 1 ? "" : "s")."
            }
        } else if snap.health != .healthy {
            line += " " + snap.health.title + "."
        }
        return line
    }

    /// One line on what the scan covers, with the causes behind "Why?".
    private var coverageLine: some View {
        HStack(alignment: .firstTextBaseline, spacing: DiskMapSpace.xs) {
            Text(coverageText)
                .font(DiskMapType.secondary)
                .foregroundStyle(DiskMapTheme.ink3)
                .lineLimit(2)
                .accessibilityIdentifier("volume-reconciliation")
            if hasWhyDetail {
                Button("Why?") { showWhy.toggle() }
                    .buttonStyle(LinkButtonStyle())
                    .font(DiskMapType.secondary)
                    .popover(isPresented: $showWhy, arrowEdge: .bottom) { whyPopover }
                    .accessibilityHint("Explains the gap between this scan and the disk")
            }
        }
    }

    private var coverageText: String {
        if let rec = snap.reconciliation { return reconciliationHeadline(rec) }
        return "\(ByteFormat.string(snap.scannedBytes)) found in this scan."
    }

    private var hasWhyDetail: Bool {
        if let rec = snap.reconciliation, reconciliationDetail(rec) != nil { return true }
        if snap.hasSharingInfo { return Self.cloneText(snap.sharingCorrection) != nil }
        return model.rootURL.map { StorageSharing.isAPFS($0.path) } == true
    }

    private var whyPopover: some View {
        VStack(alignment: .leading, spacing: DiskMapSpace.md) {
            MonoLabel("Why the numbers differ")
            if let rec = snap.reconciliation, let detail = reconciliationDetail(rec) {
                Text(detail)
                    .font(DiskMapType.body)
                    .foregroundStyle(DiskMapTheme.ink)
                    .fixedSize(horizontal: false, vertical: true)
            }
            cloneNote
        }
        .padding(DiskMapSpace.lg)
        .frame(width: 340, alignment: .leading)
        .background(DiskMapTheme.raised)
    }

    // MARK: You can free

    private var canFreeCard: some View {
        let ready = model.isCatalogReady(.reviewables)
        let bytes = model.safeReclaimableBytes
        return ZStack(alignment: .topLeading) {
            BoardGrid(tint: Color.white.opacity(0.05))
            VStack(alignment: .leading, spacing: 0) {
                Text("You can free").font(DiskMapType.body).foregroundStyle(Color.white.opacity(0.62))
                Group {
                    if ready {
                        tightText(bytes > 0 ? ByteFormat.hero(bytes) : "Nothing yet", kerning: -heroSize * 0.02)
                            .contentTransition(reduceMotion ? .identity : .numericText())
                    } else {
                        Text("Checking…")
                    }
                }
                .font(DiskMapType.hero(bytes > 0 || !ready ? heroSize * 0.43 : heroSize * 0.3))
                .foregroundStyle(ready && bytes > 0 ? DiskMapTheme.accentBright : Color.white.opacity(0.7))
                .lineLimit(1)
                .minimumScaleFactor(0.6)
                .padding(.top, 8)
                .accessibilityIdentifier("overview-can-free")
                Text(bytes > 0 || !ready
                     ? "Caches and build files that rebuild by themselves. They go to the Trash first, so you can always put them back."
                     : model.nothingTickedLine)
                    .font(DiskMapType.body)
                    .foregroundStyle(Color.white.opacity(0.82))
                    .lineSpacing(2)
                    .padding(.top, 12)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 16)
                Button(bytes > 0 ? "Review \(ByteFormat.hero(bytes))" : "Open Clean up") {
                    model.destination = .cleanSafe
                }
                .buttonStyle(AccentButtonStyle(large: true))
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.black).offset(x: 3, y: 3))
                .opacity(ready ? 1 : 0)
            }
            .padding(.horizontal, widthClass == .compact ? 24 : 32)
            .padding(.vertical, widthClass == .compact ? 22 : 30)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(minHeight: widthClass == .compact ? 206 : widthClass == .regular ? 250 : 300)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(DiskMapTheme.night))
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .environment(\.colorScheme, .dark)
    }

    // MARK: What's using your disk

    /// The bar's parts: the biggest categories in ink greys, the rest as
    /// "Everything else", what the scan didn't reach, then free space.
    private struct UsagePart: Identifiable {
        enum Fill { case grey(Int), unscanned, free }
        var id: String
        var title: String
        var bytes: Int64
        var fill: Fill
        var category: StorageCategory?
    }

    private var usageParts: [UsagePart] {
        let cats = snap.categories.filter { $0.bytes > 0 }
        let shown = Array(cats.prefix(6))
        var parts = shown.enumerated().map { i, c in UsagePart(id: c.key, title: c.title, bytes: c.bytes, fill: .grey(i), category: c) }
        let rest = cats.dropFirst(6).reduce(Int64(0)) { $0 + $1.bytes }
        if rest > 0 { parts.append(UsagePart(id: "rest", title: "Everything else", bytes: rest, fill: .grey(6))) }
        if let vol = snap.volume {
            let unscanned = Int64(vol.usedBytes) - snap.scannedBytes
            if unscanned > Int64(vol.totalBytes) / 100 {
                parts.append(UsagePart(id: "unscanned", title: snap.scanRootPath == "/" ? "System and other" : "Outside this scan", bytes: unscanned, fill: .unscanned))
            }
            parts.append(UsagePart(id: "free", title: "Free", bytes: Int64(vol.freeBytes), fill: .free))
        }
        return parts
    }

    private static let greys: [Double] = [1.0, 0.74, 0.56, 0.42, 0.30, 0.21, 0.14]

    @ViewBuilder
    private func swatch(_ fill: UsagePart.Fill) -> some View {
        switch fill {
        case .grey(let i): DiskMapTheme.ink.opacity(Self.greys[min(i, Self.greys.count - 1)])
        case .unscanned: DiskMapTheme.ink.opacity(0.08)
        case .free: FreeStripes()
        }
    }

    private var usageCard: some View {
        let parts = usageParts
        let total = max(1, parts.reduce(Int64(0)) { $0 + $1.bytes })
        let used = snap.volume.map { Int64($0.usedBytes) } ?? snap.scannedBytes
        return VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text(snap.categoryMode == .folder ? "What’s in this folder" : "What’s using your disk")
                    .font(.system(size: DiskMapType.scaled(16), weight: .semibold))
                    .foregroundStyle(DiskMapTheme.ink)
                Text("\(ByteFormat.string(used)) used").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                Spacer()
                Button("Open Explore →", action: onOpenVisualize)
                    .buttonStyle(LinkButtonStyle())
                    .font(DiskMapType.secondary)
            }
            GeometryReader { bar in
                HStack(spacing: 2) {
                    ForEach(parts) { part in
                        let w = bar.size.width * CGFloat(Double(part.bytes) / Double(total))
                        if w >= 1 {
                            swatch(part.fill)
                                .frame(width: max(1, w - 2))
                                .help("\(part.title) · \(ByteFormat.string(part.bytes))")
                                .onTapGesture { if let c = part.category { open(c) } }
                        }
                    }
                }
            }
            .frame(height: widthClass == .compact ? 46 : widthClass == .regular ? 58 : 70)
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            .accessibilityHidden(true)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: widthClass == .compact ? 22 : 28), count: widthClass == .compact ? 3 : 4), alignment: .leading, spacing: 10) {
                ForEach(parts) { part in
                    LegendItem(title: part.title, bytes: part.bytes, help: part.category.map(openHelp) ?? part.title,
                               action: part.category.map { c in { open(c) } }) {
                        swatch(part.fill).frame(width: 12, height: 12)
                            .clipShape(RoundedRectangle(cornerRadius: 3, style: .continuous))
                    }
                }
            }
        }
        .padding(.horizontal, widthClass == .compact ? 20 : 28)
        .padding(.vertical, widthClass == .compact ? 16 : 24)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
    }

    // MARK: Biggest places

    private var places: (rows: [(id: Int32, name: String, bytes: Int64, files: Int)], restBytes: Int64, restCount: Int) {
        guard let tree = model.tree, !model.selectedTotals.isEmpty else { return ([], 0, 0) }
        let kids = tree.children(of: 0, totals: model.selectedTotals).sorted { $0.size > $1.size }
        let top = kids.prefix(6).filter { $0.size > 0 }.map { kid in
            (id: kid.id, name: tree.name(of: kid.id), bytes: kid.size,
             files: model.descendantFileCounts.indices.contains(Int(kid.id)) ? model.descendantFileCounts[Int(kid.id)] : 0)
        }
        let rest = kids.dropFirst(6)
        return (top, rest.reduce(0) { $0 + $1.size }, rest.count)
    }

    private var biggestPlaces: some View {
        let p = places
        let largest = max(1, p.rows.first?.bytes ?? 1)
        return BoardCard {
            VStack(spacing: 0) {
                BoardCardHeader(title: "Biggest places") {
                    Button("Open Explore →", action: onOpenVisualize)
                        .buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                }
                ForEach(p.rows, id: \.id) { row in
                    PlaceRow(name: row.name, detail: row.files > 0 ? "\(row.files.formatted()) files" : nil,
                             bytes: row.bytes, fraction: Double(row.bytes) / Double(largest)) {
                        model.currentNode = row.id
                        model.selectedNode = row.id
                        onOpenVisualize()
                    }
                }
                if p.restCount > 0 {
                    PlaceRow(name: "Everything else", detail: "\(p.restCount) folder\(p.restCount == 1 ? "" : "s")",
                             bytes: p.restBytes, fraction: Double(p.restBytes) / Double(largest), action: onOpenVisualize)
                }
            }
        }
        .accessibilityIdentifier("biggest-places")
    }

    // MARK: Ready to free

    private var readyToFree: some View {
        let ready = model.isCatalogReady(.reviewables)
        let items = model.safeReclaimables
        return BoardCard {
            VStack(spacing: 0) {
                BoardCardHeader(title: "Ready to free") {
                    if ready, model.safeReclaimableBytes > 0 {
                        Text(ByteFormat.string(model.safeReclaimableBytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.accent)
                    }
                }
                if !ready {
                    HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Looking for things that rebuild by themselves…").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2) }
                        .frame(maxWidth: .infinity, minHeight: 120)
                } else if items.isEmpty {
                    Text(model.nothingTickedLine)
                        .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                        .frame(maxWidth: .infinity, minHeight: 120).padding(.horizontal, 18)
                } else {
                    ForEach(items.prefix(widthClass == .compact ? 3 : 4)) { item in
                        ReadyRow(item: item, showDetail: widthClass != .compact) { model.destination = .cleanSafe }
                    }
                }
                Spacer(minLength: 0)
                HStack {
                    Text("Nothing is removed until you confirm.").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                    Spacer()
                    if ready, !items.isEmpty {
                        Button("See all \(items.count) →") { model.destination = .cleanSafe }
                            .buttonStyle(LinkButtonStyle()).font(DiskMapType.secondary)
                    }
                }
                .padding(.horizontal, 18)
                .frame(height: 44)
                .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            }
        }
        .accessibilityIdentifier("ready-to-free")
    }

    // MARK: Notices

    @ViewBuilder
    private var cloneNotice: some View {
        if let total = model.allocatedTotals.first,
           let shared = model.cloneSurvey.sharedBytes(of: 0, total: total, minimumFraction: 0.02,
                                                      minimumBytes: 2 * 1024 * 1024 * 1024) {
            BoardBanner(title: "About \(ByteFormat.string(shared)) is counted more than once.",
                        detail: "Cloned copies share blocks on disk, but this scan counts each copy in full.",
                        actionTitle: model.isScanning ? nil : "Count clones once",
                        action: { model.countClonesOnce() })
                .help(CloneAccounting.explanation(.refcount))
                .accessibilityIdentifier("clone-copies-notice")
        }
    }

    /// TASK-039: say when the totals are short because folders were unreadable.
    @ViewBuilder
    private var unreadableNotice: some View {
        let count = model.deniedDirectoryIDs.count
        if count > 0, !hideUnreadable {
            BoardBanner(title: "\(count.formatted()) folder\(count == 1 ? "" : "s") couldn’t be read.",
                        detail: "Allow Full Disk Access to see everything, then rescan.",
                        actionTitle: "Allow access",
                        action: { model.openFullDiskAccessSettings() },
                        onDismiss: { hideUnreadable = true })
                .help(model.deniedDirectoryExamples().joined(separator: "\n"))
                .accessibilityIdentifier("unreadable-folders-notice")
        }
    }

    /// TASK-077: what counting each clone family once changed — or, when it
    /// is off on an APFS volume, that clones may be counted more than once.
    @ViewBuilder
    private var cloneNote: some View {
        if snap.hasSharingInfo {
            if let text = Self.cloneText(snap.sharingCorrection) {
                Text(text)
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.ink2)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("clone-accounting")
            }
        } else if model.rootURL.map({ StorageSharing.isAPFS($0.path) }) == true {
            VStack(alignment: .leading, spacing: 6) {
                Text(cloneOffText)
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.ink2)
                    .fixedSize(horizontal: false, vertical: true)
                SettingsLink { Text("Count clones once…") }
                    .buttonStyle(LinkButtonStyle())
                    .font(DiskMapType.secondary)
            }
            .accessibilityIdentifier("clone-accounting-off")
        }
    }

    private var cloneOffText: String {
        let base = "Cloned files are counted once per copy, so these totals can be higher than the disk really uses."
        guard let total = model.allocatedTotals.first,
              let shared = model.cloneSurvey.sharedBytes(of: 0, total: total, minimumFraction: 0) else { return base }
        return base + " In this scan, that is about \(ByteFormat.string(shared))."
    }

    // MARK: Footer

    @ViewBuilder
    private var footer: some View {
        if let kind = model.lastScanKind {
            HStack(alignment: .firstTextBaseline, spacing: DiskMapSpace.xs) {
                Text(Self.scanKindText(kind))
                    .font(DiskMapType.figureSmall)
                    .foregroundStyle(DiskMapTheme.ink3)
                    .fixedSize(horizontal: false, vertical: true)
                if case .quick = kind, let root = model.rootURL {
                    Button("Full Rescan") { Task { await model.scan(root, mode: .full) } }
                        .buttonStyle(LinkButtonStyle())
                        .font(DiskMapType.figureSmall)
                        .disabled(model.isScanning)
                }
            }
            .padding(.top, 2)
            .accessibilityElement(children: .combine)
            .accessibilityIdentifier("scan-kind")
        }
    }

    // MARK: Helpers

    static let growthNoise: Int64 = 50_000_000

    static func cloneText(_ correction: FileTree.SharingCorrection) -> String? {
        var parts: [String] = []
        if correction.cloneCount > 0 {
            parts.append("\(correction.cloneCount.formatted()) cloned cop\(correction.cloneCount == 1 ? "y" : "ies") in \(correction.familyCount.formatted()) group\(correction.familyCount == 1 ? "" : "s") share \(ByteFormat.string(correction.bytes)) with their originals — counted once.")
        }
        if correction.partialCount > 0 {
            parts.append("\(ByteFormat.string(correction.partialSharedBytes)) in \(correction.partialCount.formatted()) edited cop\(correction.partialCount == 1 ? "y" : "ies") is shared with files freedisk.space can’t name, and counted in full.")
        }
        return parts.isEmpty ? nil : parts.joined(separator: " ")
    }

    static func scanKindText(_ kind: ScanModel.ScanKind) -> String {
        switch kind {
        case let .quick(seconds, changed, walked):
            if changed == 0 && walked == 0 {
                return "Updated from your last scan in \(String(format: "%.1f", seconds)) s — nothing changed; a sample of folders was re-read to confirm."
            }
            let walkedText = walked > 0 ? ", \(walked) new folder\(walked == 1 ? "" : "s") walked" : ""
            return "Updated from your last scan in \(String(format: "%.1f", seconds)) s — "
                + "\(changed) changed folder\(changed == 1 ? "" : "s") re-read\(walkedText), unchanged ones spot-checked."
        case let .full(seconds, reason):
            let base = "Full scan in \(String(format: "%.1f", seconds)) s."
            return reason.map { base + " (Walked in full: \($0).)" } ?? base
        }
    }

    private func reconciliationHeadline(_ rec: AnalysisSnapshot.VolumeReconciliation) -> String {
        let scanned = ByteFormat.string(rec.scannedBytes)
        let used = ByteFormat.string(rec.usedBytes)
        if rec.scannedExceedsUsed {
            return "This scan found \(scanned), more than the \(used) in use."
        }
        if rec.coverageFraction >= 0.98 {
            return "This scan accounts for all \(used) in use."
        }
        return "This scan accounts for \(scanned) of the \(used) in use."
    }

    private func reconciliationDetail(_ rec: AnalysisSnapshot.VolumeReconciliation) -> String? {
        if rec.scannedExceedsUsed {
            return "Cloned files share the same blocks on disk but are listed once per copy, so the scan total can exceed what is actually used."
        }
        guard rec.coverageFraction < 0.98 else { return nil }
        var causes: [String] = []
        if snap.scanRootPath != "/" {
            let display = CanonicalPath.displayPath(absolutePath: snap.scanRootPath)
            let place = display == "~"
                ? "your home folder"
                : "“\(URL(fileURLWithPath: snap.scanRootPath).lastPathComponent)”"
            causes.append("outside \(place) (macOS, apps and other users)")
        }
        causes.append("held by local Time Machine snapshots or purgeable space")
        let denied = model.deniedDirectoryIDs.count
        if denied > 0 {
            causes.append("inside \(denied.formatted()) folder\(denied == 1 ? "" : "s") freedisk.space couldn’t read")
        }
        let list = causes.count > 1
            ? causes.dropLast().joined(separator: ", ") + ", or " + (causes.last ?? "")
            : causes.first ?? ""
        return "The other \(ByteFormat.string(rec.unaccountedBytes)) is \(list)."
    }

    /// Storage categories that are mostly loose files open Biggest Files
    /// filtered to them; the rest open Explore at their biggest folder.
    static let fileListCategories: Set<String> = ["installers", "ai", "media", "vms", "downloads", "documents"]

    private func open(_ cat: StorageCategory) {
        if let kind = cat.fileKind {
            model.findQuery = "kind:\(kind)"
            model.destination = .find
        } else if Self.fileListCategories.contains(cat.key) {
            model.folderFilterPath = nil
            model.categoryFilter = cat.key
            onOpenBiggestFiles()
        } else if let node = cat.nodeID {
            model.currentNode = node
            model.selectedNode = node
            onOpenVisualize()
        } else {
            onOpenBiggestFiles()
        }
    }

    private func openHelp(_ cat: StorageCategory) -> String {
        if cat.fileKind != nil { return "List every \(cat.title.lowercased()) file in Find" }
        if Self.fileListCategories.contains(cat.key) { return "Show the biggest files in \(cat.title)" }
        return cat.nodeID != nil ? "Show this folder in Explore" : "Show the biggest files"
    }
}

/// One legend entry; clickable when it has somewhere to go.
private struct LegendItem<Swatch: View>: View {
    var title: String
    var bytes: Int64
    var help: String
    var action: (() -> Void)?
    @ViewBuilder var swatch: Swatch
    @State private var hovering = false
    var body: some View {
        HStack(spacing: 9) {
            swatch
            Text(title).font(DiskMapType.body).foregroundStyle(hovering && action != nil ? DiskMapTheme.accent : DiskMapTheme.ink).lineLimit(1)
            Spacer(minLength: 6)
            Text(ByteFormat.string(bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture { action?() }
        .help(help)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(title), \(ByteFormat.string(bytes))")
        .accessibilityAddTraits(action != nil ? .isButton : [])
        .accessibilityAction { action?() }
    }
}

/// A Biggest places row: a soft fill sized to the folder, name, detail, size.
private struct PlaceRow: View {
    var name: String
    var detail: String?
    var bytes: Int64
    var fraction: Double
    var action: () -> Void
    @Environment(\.widthClass) private var widthClass
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Text(name).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                if let detail { Text(detail).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(1) }
                Spacer(minLength: 8)
                Text(ByteFormat.string(bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
            }
            .padding(.horizontal, 16)
            .frame(height: widthClass == .compact ? 42 : widthClass == .regular ? 48 : 52)
            .background(alignment: .leading) {
                GeometryReader { g in
                    RoundedRectangle(cornerRadius: 5, style: .continuous)
                        .fill(hovering ? DiskMapTheme.accentWash : DiskMapTheme.soft)
                        .frame(width: max(8, (g.size.width - 16) * 0.86 * CGFloat(min(1, fraction))))
                        .padding(.vertical, 6)
                        .padding(.leading, 8)
                }
            }
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.55)).frame(height: 1) }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help("Open \(name) in Explore")
        .accessibilityLabel("\(name), \(ByteFormat.string(bytes))")
    }
}

/// A Ready to free row: line icon, what it is and what happens, size.
private struct ReadyRow: View {
    var item: ReviewableTarget
    var showDetail: Bool
    var action: () -> Void
    @Environment(\.widthClass) private var widthClass
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                IconTile(systemName: item.symbolName)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.displayName).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                    if showDetail {
                        Text(item.consequence.isEmpty ? item.detail : item.consequence)
                            .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                    }
                }
                Spacer(minLength: 8)
                Text(ByteFormat.string(item.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
            }
            .padding(.horizontal, 18)
            .frame(height: widthClass == .compact ? 58 : widthClass == .regular ? 70 : 78)
            .background(hovering ? DiskMapTheme.hover : Color.clear)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.55)).frame(height: 1) }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help("Review in Clean up")
        .accessibilityLabel("\(item.displayName), \(ByteFormat.string(item.bytes)). Review in Clean up")
    }
}

extension StorageRecommendation {
    /// The page that reviews this recommendation (Overview and Explain).
    var destination: AppDestination {
        switch id {
        case "rec-quickwins": return .cleanSafe
        case "rec-downloads": return .cleanDownloads
        case "rec-forgotten": return .forgottenFiles
        case "rec-caches": return .cleanCaches
        default: return .cleanMedia
        }
    }
}
