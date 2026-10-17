import AppKit
import DiskMapCore
import SwiftUI

/// Task-oriented shell (Overview / Find / Clean / Explore) matching DiskMap1/2 IA.
struct AppShellView: View {
    @ObservedObject var model: ScanModel
    @State private var showExplain = false
    @State private var showPalette = false
    @State private var showCompactSidebar = false
    @State private var searchText = ""
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var hasCompletedScan: Bool { model.tree != nil }

    static let comfortablePageHeight: CGFloat = 720

    /// The boards' sidebar widths: 13" Air, 14" Pro, a large display.
    static func sidebarWidth(_ windowWidth: CGFloat) -> CGFloat {
        let base: CGFloat = windowWidth < 1_400 ? 200 : windowWidth < 1_700 ? 220 : 236
        return (base * max(1, DiskMapType.scale)).rounded()
    }
    /// Destinations whose list lives in its own scroll view under a fixed
    /// header. Pages that already scroll as a whole are not listed.
    static let pageScrollsWhenShort: Set<AppDestination> = [
        .find, .biggestFiles, .biggestFolders, .forgottenFiles, .duplicates,
        .cleanSafe, .cleanCaches, .fileBrowser, .visualize, .applications,
    ]

    var body: some View {
        GeometryReader { window in
            let compactSidebar = window.size.width < 1_000
            let sideW = Self.sidebarWidth(window.size.width)
            ZStack(alignment: .topLeading) {
                VStack(spacing: 0) {
                    topBar(compactSidebar: compactSidebar)
                    Hairline()
                    HStack(spacing: 0) {
                        if !compactSidebar {
                            sidebar
                                .frame(width: sideW)
                            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
                        }
                        GeometryReader { geo in
                            let page = destinationBody
                                .environment(\.widthClass, WidthClass(window.size.width))
                                .environment(
                                    \.diskMapContentWidth,
                                    geo.size.width + (compactSidebar ? 0 : sideW + 1)
                                )
                            // Screens built as "fixed header + inner list" squeezed the
                            // list to nothing in a short window (Safe to Review's list
                            // measured 56 pt at 600 pt tall, Caches' 0). Below a
                            // comfortable height they lay out at that height inside a
                            // page scroll instead, so everything stays reachable.
                            if Self.pageScrollsWhenShort.contains(model.destination), geo.size.height < Self.comfortablePageHeight {
                                ScrollView(.vertical) {
                                    page.frame(width: geo.size.width, height: Self.comfortablePageHeight)
                                }
                            } else {
                                page.frame(width: geo.size.width, height: geo.size.height)
                            }
                        }
                    }
                }

                if compactSidebar, showCompactSidebar {
                    Color.black.opacity(0.18)
                        .padding(.top, DiskMapMetric.topBarHeight + 1)
                        .ignoresSafeArea(edges: [.horizontal, .bottom])
                        .onTapGesture { showCompactSidebar = false }
                    sidebar
                        .frame(width: DiskMapMetric.sidebarWidth)
                        .padding(.top, DiskMapMetric.topBarHeight + 1)
                        .transition(.move(edge: .leading).combined(with: .opacity))
                        .shadow(color: .black.opacity(0.14), radius: 16, x: 5, y: 0)
                        .onExitCommand { showCompactSidebar = false }
                }

                if let toast = model.toastMessage {
                    Text(toast)
                        .font(DiskMapType.bodyEmphasis)
                        .foregroundStyle(DiskMapTheme.ink)
                        .padding(.horizontal, 14)
                        .frame(height: 34)
                        .background(
                            Capsule().fill(DiskMapTheme.raised)
                                .overlay(Capsule().stroke(DiskMapTheme.line, lineWidth: 1))
                                .shadow(color: .black.opacity(0.08), radius: 10, y: 3)
                        )
                        .padding(.top, DiskMapMetric.topBarHeight + 12)
                        .frame(maxWidth: .infinity)
                        .transition(.move(edge: .top).combined(with: .opacity))
                        .zIndex(10)
                }

            }
            .animation(reduceMotion ? nil : .easeInOut(duration: 0.2), value: showCompactSidebar)
        }
        .animation(reduceMotion ? nil : .easeInOut(duration: 0.2), value: model.toastMessage)
        .background(DiskMapTheme.page)
        .frame(minWidth: 880, minHeight: 600)
        .onChange(of: model.destination) { _, _ in model.clearMultiSelection() }
        .sheet(isPresented: $model.isCleanupQueuePresented) {
            CleanupQueueView(model: model)
                .frame(minWidth: 640, minHeight: 480)
        }
        .sheet(item: $model.pendingBulkStage) { BulkStageSheet(proposal: $0) }
        .sheet(item: $model.pendingRiskyStage) { RiskyStageSheet(proposal: $0) }
        .sheet(isPresented: $showExplain) {
            ExplainStorageSheet(model: model)
                .frame(minWidth: 520, minHeight: 420)
        }
        .overlay {
            if showPalette {
                ZStack {
                    Color.black.opacity(reduceMotion ? 0.35 : 0.25)
                        .ignoresSafeArea()
                        .onTapGesture { showPalette = false }
                        .accessibilityLabel("Dismiss command palette")
                        .accessibilityAddTraits(.isButton)
                    CommandPalette(
                        model: model,
                        isPresented: $showPalette,
                        initialQuery: searchText,
                        onReviewCleanup: { showPalette = false; model.isCleanupQueuePresented = true },
                        onExplain: { showPalette = false; showExplain = true }
                    )
                }
                .transition(reduceMotion ? .identity : .opacity)
            }
        }
        .onChange(of: showPalette) { _, open in if !open { searchText = "" } }
        .onReceive(NotificationCenter.default.publisher(for: .diskMapOpenPalette)) { _ in
            if hasCompletedScan { showPalette = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: .diskMapOpenExplain)) { note in
            // `object: false` closes it (the harness, after capturing).
            if hasCompletedScan { showExplain = (note.object as? Bool) ?? true }
        }
    }

    private func topBar(compactSidebar: Bool) -> some View {
        HStack(spacing: 10) {
            if compactSidebar {
                Button {
                    showCompactSidebar.toggle()
                } label: {
                    Label(showCompactSidebar ? "Hide Sidebar" : "Show Sidebar", systemImage: "sidebar.left")
                }
                .buttonStyle(IconButtonStyle())
                .help(showCompactSidebar ? "Hide Sidebar" : "Show Sidebar")
            }
            pageContext
                .layoutPriority(1)
            Spacer(minLength: 12)
            if model.isScanning, model.tree != nil {
                // The first scan shows its own counter on the page.
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini)
                    Text(model.scanPhase == .checkingChanges ? "Checking changes" : "Rescanning · \(model.scannedCount.formatted())")
                        .font(DiskMapType.figureSmall)
                        .foregroundStyle(DiskMapTheme.ink2)
                        .lineLimit(1)
                }
                .accessibilityElement(children: .combine)
            } else if hasCompletedScan, let finished = model.lastScanFinishedAt {
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    Text("Scanned \(RelativeTime.string(since: finished, now: context.date))")
                        .font(DiskMapType.secondary)
                        .foregroundStyle(DiskMapTheme.ink3)
                        .lineLimit(1)
                }
            }
            if hasCompletedScan {
                Menu {
                    Button("Quick Update — re-read what changed") {
                        if let root = model.rootURL { Task { await model.scan(root) } }
                    }
                    Button("Full Rescan — walk every folder") {
                        if let root = model.rootURL { Task { await model.scan(root, mode: .full) } }
                    }
                } label: {
                    Text("Rescan")
                } primaryAction: {
                    if let root = model.rootURL { Task { await model.scan(root) } } else { pickFolder() }
                }
                .menuStyle(.button)
                .buttonStyle(SecondaryButtonStyle())
                .menuIndicator(.hidden)
                .fixedSize()
                .disabled(model.isScanning)
                .help("Rescan — re-reads only what changed. Hold for a full rescan.")
                .accessibilityLabel("Rescan")
            }
            // ⌘K stays global even though the field moved to the sidebar foot.
            Button { if hasCompletedScan { showPalette = true } } label: { EmptyView() }
                .buttonStyle(.plain)
                .keyboardShortcut("k", modifiers: .command)
                .frame(width: 0, height: 0)
                .accessibilityHidden(true)
        }
        .padding(.leading, 14)
        .padding(.trailing, 12)
        .frame(height: DiskMapMetric.topBarHeight)
        .background(DiskMapTheme.page)
    }

    /// The left side of the page bar: what this page is looking at.
    @ViewBuilder
    private var pageContext: some View {
        switch model.destination.navParent {
        case .overview:
            if let vol = model.analysis.volume, hasCompletedScan {
                HStack(spacing: 8) {
                    Text(vol.volumeName).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                    Text(ByteFormat.string(Int64(vol.totalBytes))).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                }
                .lineLimit(1)
            } else {
                Text(hasCompletedScan ? (model.rootURL?.lastPathComponent ?? "Overview") : "freedisk.space")
                    .font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
            }
        default:
            Text(model.destination.navParent.navTitle)
                .font(DiskMapType.bodyEmphasis)
                .foregroundStyle(DiskMapTheme.ink)
                .lineLimit(1)
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 1) {
            ForEach(AppDestination.primaryNav) { navRow($0) }
            MonoLabel("More")
                .padding(.horizontal, 10)
                .padding(.top, 14)
                .padding(.bottom, 6)
            ForEach(AppDestination.moreNav) { navRow($0) }
            Spacer(minLength: 12)
            if hasCompletedScan { basketCard }
            sidebarFooter
        }
        .padding(.horizontal, 9)
        .padding(.top, 12)
        .padding(.bottom, 10)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(DiskMapTheme.sidebar)
    }

    /// Compact size for a nav figure: "31.6G".
    static func compactBytes(_ bytes: Int64) -> String {
        let g = Double(bytes) / 1_000_000_000
        if g >= 100 { return String(format: "%.0fG", g) }
        if g >= 0.95 { return String(format: "%.1fG", g) }
        return String(format: "%.0fM", max(1, Double(bytes) / 1_000_000))
    }

    /// A quiet figure beside a nav item, read from catalogs already built —
    /// never builds one (catalogs stay lazy, TASK-043).
    private func navFigure(_ dest: AppDestination) -> (text: String, accent: Bool)? {
        func bytes(_ value: Int64, accent: Bool = false) -> (String, Bool)? { value > 0 ? (Self.compactBytes(value), accent) : nil }
        switch dest {
        case .cleanSafe: return model.isCatalogReady(.reviewables) ? bytes(model.safeReclaimableBytes, accent: true) : nil
        case .developerStorage: return model.isCatalogReady(.developer) ? bytes(model.cachedDeveloper.summary.totalBytes) : nil
        case .duplicates:
            guard model.duplicateDidRun else { return nil }
            return bytes(model.duplicateGroups.reduce(Int64(0)) { $0 + Int64(max(0, $1.physicalCopies - 1)) * $1.sizeEach })
        case .snapshots:
            guard let c = model.weekComparison, abs(c.freeDelta) >= OverviewView.growthNoise else { return nil }
            let used = -c.freeDelta
            return ((used >= 0 ? "+" : "−") + Self.compactBytes(abs(used)).dropLast(), false)
        default: return nil
        }
    }

    private func navRow(_ dest: AppDestination) -> some View {
        let locked = dest.requiresScan && !hasCompletedScan
        let selected = model.destination.navParent == dest && !locked
        return Button {
            guard !locked else { return }
            showCompactSidebar = false
            model.destination = dest
            if dest == .visualize { model.topNav = .explore }
            if dest == .duplicates { model.topNav = .duplicates }
            if dest == .applications { model.topNav = .applications }
            if dest == .snapshots { model.topNav = .snapshots }
        } label: {
            HStack(spacing: 10) {
                Image(systemName: dest.navSymbol)
                    .font(.system(size: DiskMapType.scaled(12.5), weight: .regular))
                    .foregroundStyle(locked ? DiskMapTheme.ink3 : selected ? DiskMapTheme.ink : DiskMapTheme.ink2)
                    .frame(width: 17)
                Text(dest.navTitle)
                    .font(selected ? DiskMapType.bodyEmphasis : DiskMapType.body)
                    .foregroundStyle(locked ? DiskMapTheme.ink3 : selected ? DiskMapTheme.ink : DiskMapTheme.ink2)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if !locked, let figure = navFigure(dest) {
                    Text(figure.text)
                        .font(DiskMapType.figureSmall)
                        .foregroundStyle(figure.accent ? DiskMapTheme.accent : DiskMapTheme.ink3)
                        .lineLimit(1)
                        .fixedSize()
                }
            }
            .padding(.horizontal, 10)
            .frame(height: DiskMapType.scaled(30))
            .background(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(selected ? DiskMapTheme.soft : Color.clear)
            )
            .overlay(alignment: .leading) {
                if selected {
                    UnevenRoundedRectangle(topLeadingRadius: 7, bottomLeadingRadius: 7)
                        .fill(DiskMapTheme.accent).frame(width: 2)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(locked)
        .help(locked ? "Scan your Mac first." : dest.navTitle)
        .accessibilityLabel(locked ? "\(dest.navTitle), scan first" : navFigure(dest).map { "\(dest.navTitle), \($0.text)" } ?? dest.navTitle)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    /// What's staged for the Trash, and what free space it leaves.
    private var basketCard: some View {
        let count = model.stagedItems.count
        let freeAfter = model.analysis.volume.map { Int64($0.freeBytes) + model.reclaimableBytes }
        return VStack(alignment: .leading, spacing: 3) {
            HStack {
                MonoLabel("Basket")
                Spacer()
                MonoLabel(count == 0 ? "Empty" : "\(count) item\(count == 1 ? "" : "s")")
            }
            Text(count == 0 ? "Nothing yet" : ByteFormat.string(model.reclaimableBytes))
                .font(.system(size: DiskMapType.scaled(count == 0 ? 15 : 21), weight: .semibold))
                .foregroundStyle(count == 0 ? DiskMapTheme.ink3 : DiskMapTheme.ink)
                .padding(.top, 1)
            if count > 0, let freeAfter {
                MonoLabel("Free after · \(ByteFormat.string(freeAfter))")
            }
            Button(count == 0 ? "Open basket" : "Review basket") { model.isCleanupQueuePresented = true }
                .buttonStyle(PrimaryButtonStyle(fullWidth: true))
                .padding(.top, 7)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(count == 0 ? "Basket, empty" : "Basket, \(count) items, \(ByteFormat.string(model.reclaimableBytes))")
    }

    private var sidebarFooter: some View {
        HStack(spacing: 6) {
            Button { if hasCompletedScan { showPalette = true } } label: {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").font(.system(size: DiskMapType.scaled(10), weight: .medium))
                    MonoLabel("⌘K Find anything")
                }
            }
            .buttonStyle(.plain)
            .disabled(!hasCompletedScan)
            .help(hasCompletedScan ? "Find files, folders and actions (⌘K)" : "Scan first to search")
            Spacer()
            SettingsLink { Image(systemName: "gearshape").font(.system(size: DiskMapType.scaled(11.5))) }
                .buttonStyle(.plain)
                .foregroundStyle(DiskMapTheme.ink3)
                .help("Settings")
                .accessibilityLabel("Settings")
        }
        .foregroundStyle(DiskMapTheme.ink3)
        .padding(.horizontal, 6)
        .padding(.top, 10)
    }

    @ViewBuilder
    private var destinationBody: some View {
        switch model.destination {
        case .overview:
            OverviewView(
                model: model,
                pickFolder: pickFolder,
                onReviewCleanup: { model.isCleanupQueuePresented = true },
                onExplain: { showExplain = true },
                onOpenVisualize: { model.destination = .visualize },
                onSelectFile: { id in
                    model.selectedNode = id
                    model.destination = .biggestFiles
                },
                onOpenBiggestFiles: { model.destination = .biggestFiles }
            )
        case .find:
            if let tree = model.tree, let root = model.rootURL {
                FindView(model: model, tree: tree, rootURL: root)
            } else { needsScan }
        case .fileBrowser:
            if let tree = model.tree, let root = model.rootURL {
                FileBrowserView(model: model, tree: tree, rootURL: root)
            } else { needsScan }
        case .visualize:
            VisualizeView(model: model, pickFolder: pickFolder)
        case .biggestFiles:
            if let tree = model.tree, let root = model.rootURL {
                BiggestFilesView(model: model, tree: tree, rootURL: root, onOpenCleanup: { model.isCleanupQueuePresented = true })
            } else { needsScan }
        case .biggestFolders:
            if let tree = model.tree, let root = model.rootURL {
                BiggestFoldersView(model: model, tree: tree, rootURL: root, onOpenCleanup: { model.isCleanupQueuePresented = true })
            } else { needsScan }
        case .forgottenFiles:
            if let tree = model.tree, let root = model.rootURL {
                ForgottenFilesView(model: model, tree: tree, rootURL: root, onOpenCleanup: { model.isCleanupQueuePresented = true })
            } else { needsScan }
        case .duplicates:
            if let tree = model.tree, let root = model.rootURL {
                DuplicatesView(model: model, tree: tree, rootURL: root)
            } else { needsScan }
        case .cleanSafe:
            SafeToReviewView(
                model: model,
                onOpenCaches: { model.destination = .cleanCaches }
            )
        case .cleanCaches:
            CachesReviewView(
                model: model
            )
        case .cleanDownloads:
            OldDownloadsView(
                model: model,
                pickFolder: pickFolder
            )
        case .cleanMedia:
            LargeMediaView(
                model: model,
                pickFolder: pickFolder
            )
        case .developerStorage:
            DeveloperStorageView(
                model: model,
                pickFolder: pickFolder
            )
        case .applications:
            AppsView(model: model)
        case .snapshots:
            SnapshotsView(model: model)
        case .history:
            HistoryView(model: model)
        }
    }

    private var needsScan: some View {
        FirstScanHero(
            model: model,
            pickFolder: pickFolder,
            onScanMac: {
                model.destination = .overview
                let home = FileManager.default.homeDirectoryForCurrentUser
                Task { await model.scan(home) }
            }
        )
    }

    private func pickFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Scan"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task { await model.scan(url) }
    }
}

struct ExplainStorageSheet: View {
    @ObservedObject var model: ScanModel
    @Environment(\.dismiss) private var dismiss
    @AppStorage(DustyPreference.key) private var showDusty = true

    var body: some View {
        let snap = model.analysis
        let stories = StorageNarrator.stories(from: snap)
        let recs = StorageNarrator.recommendations(from: snap)
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: DiskMapSpace.sm) {
                if showDusty {
                    Dusty(pose: .smallCurious, width: DiskMapType.scaled(40))
                }
                VStack(alignment: .leading, spacing: 4) {
                    MonoLabel("From your last scan")
                    Text("Explain my storage")
                        .font(DiskMapType.title)
                        .foregroundStyle(DiskMapTheme.ink)
                }
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(SecondaryButtonStyle())
                    .keyboardShortcut(.cancelAction)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 18)
            Hairline()
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    Group {
                        if let vol = snap.volume {
                            Text("Your Mac has \(ByteFormat.string(Int64(vol.freeBytes))) free of \(ByteFormat.string(Int64(vol.totalBytes))) — \(snap.health.title.lowercased()).")
                        } else {
                            Text("This covers \(ByteFormat.string(snap.scannedBytes)) from your last scan.")
                        }
                    }
                    .font(DiskMapType.heading)
                    .foregroundStyle(DiskMapTheme.ink)
                    .fixedSize(horizontal: false, vertical: true)

                    VStack(alignment: .leading, spacing: 10) {
                        SectionHeader(label: "What’s going on")
                        ForEach(stories) { story in
                            VStack(alignment: .leading, spacing: 3) {
                                Text(story.title)
                                    .font(DiskMapType.bodyEmphasis)
                                    .foregroundStyle(DiskMapTheme.ink)
                                Text(story.detail)
                                    .font(DiskMapType.secondary)
                                    .foregroundStyle(DiskMapTheme.ink2)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }

                    VStack(alignment: .leading, spacing: 4) {
                        SectionHeader(label: "Next steps")
                        ForEach(recs) { rec in
                            NextStepRow(rec: rec) {
                                model.destination = rec.destination
                                dismiss()
                            }
                        }
                    }

                    Text("Every figure here comes from your last local scan — nothing is estimated beyond it.")
                        .font(DiskMapType.figureSmall)
                        .foregroundStyle(DiskMapTheme.ink3)
                }
                .padding(24)
            }
        }
        .frame(minWidth: 520, minHeight: 440)
        .background(DiskMapTheme.canvas)
    }
}

/// A next step that opens the page reviewing it.
private struct NextStepRow: View {
    let rec: StorageRecommendation
    var open: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: open) {
            HStack(spacing: 12) {
                Circle().fill(rec.safety == .safe ? DiskMapTheme.safe : DiskMapTheme.review).frame(width: 6, height: 6)
                VStack(alignment: .leading, spacing: 2) {
                    Text(rec.title)
                        .font(DiskMapType.bodyEmphasis)
                        .foregroundStyle(DiskMapTheme.ink)
                    Text(rec.detail)
                        .font(DiskMapType.secondary)
                        .foregroundStyle(DiskMapTheme.ink3)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                Text(ByteFormat.string(rec.bytes))
                    .font(DiskMapType.figure)
                    .foregroundStyle(DiskMapTheme.ink)
                Text("Review")
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.accent)
                Image(systemName: "chevron.right")
                    .font(.system(size: DiskMapType.scaled(10), weight: .semibold))
                    .foregroundStyle(DiskMapTheme.accent)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(RowBackground(selected: false, hovering: hovering))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .padding(.horizontal, -10)
        .accessibilityLabel("\(rec.title), \(ByteFormat.string(rec.bytes)). Review")
    }
}

extension Notification.Name {
    /// Opens the command palette (the harness's `--palette`).
    static let diskMapOpenPalette = Notification.Name("DiskMapOpenPalette")
    /// Opens the Explain sheet (the harness's `--sheet explain`).
    static let diskMapOpenExplain = Notification.Name("DiskMapOpenExplain")
}
