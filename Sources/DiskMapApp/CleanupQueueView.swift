import AppKit
import Quartz
import SwiftUI
import DiskMapCore

struct CleanupQueueView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.dismiss) private var dismiss
    @State private var confirmingTrash = false
    @State private var showReceipt = false
    /// The entry just removed, so it can be put back (Undo) for a few seconds.
    @State private var lastRemoved: CleanupQueue.StagedItem?
    /// What this sheet just moved to the Trash: the success moment shows until
    /// it's dismissed, put back, or something new is added.
    @State private var justMoved: CleanupRecord?

    @Environment(\.widthClass) private var widthClass

    var body: some View {
        Group {
            if model.stagedItems.isEmpty, let moved = justMoved, model.lastCleanup == moved {
                CleanupSuccessView(
                    count: moved.items.count,
                    freedLine: model.lastCommitLines.last { $0.hasSuffix("when you empty the Trash.") } ?? "",
                    details: model.lastCommitLines.filter { !$0.hasSuffix("when you empty the Trash.") },
                    onPutBack: { Task { await model.putBackLastCleanup(); justMoved = nil } },
                    onDone: { dismiss() }
                )
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .transition(.opacity)
            } else {
                HStack(spacing: 0) {
                    itemsColumn
                    Rectangle().fill(DiskMapTheme.line).frame(width: 1)
                    receiptColumn.frame(width: 360)
                }
            }
        }
        .background(DiskMapTheme.page)
        .frame(width: 1_000, height: 680)
        .task { await model.refreshQueue() }
        // Every item that will move, by full path and size (MAC-FIXES-FROM-WINDOWS §4.2).
        .sheet(isPresented: $confirmingTrash) {
            PathListConfirmSheet(
                title: "Move \(countLabel(model.stagedItems.count, "item")) to the Trash?",
                message: confirmMessage,
                footnote: nil,
                rows: model.stagedItems.map { ConfirmPathRow(path: $0.url.path, bytes: $0.size) },
                confirmTitle: "Move to Trash",
                destructive: true,
                onConfirm: {
                    confirmingTrash = false
                    Task {
                        await model.commitCleanup()
                        // Only after the move succeeded: never during the confirmation.
                        if let record = model.lastCleanup, !record.items.isEmpty, record.date.timeIntervalSinceNow > -30 {
                            withAnimation(.easeOut(duration: 0.2)) { justMoved = record }
                        }
                    }
                },
                onCancel: { confirmingTrash = false }
            )
        }
        .interactiveDismissDisabled(model.commitProgress != nil)
    }

    private var itemsColumn: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            commitProgressBar
            if model.stagedItems.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Your basket is empty").font(DiskMapType.heading).foregroundStyle(DiskMapTheme.ink)
                    Text("Tick things in Clean up, or add files from any page with Add to basket (⌘⌫). Nothing moves to the Trash until you confirm here.")
                        .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                }
                .padding(24)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(DiskMapTheme.line, style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
                .padding(.horizontal, 24)
                Spacer()
            } else {
                list
            }
            undoRow
        }
        .frame(maxWidth: .infinity)
    }

    private var receiptColumn: some View {
        let vol = model.analysis.volume
        let lines = grouped.map { group in
            (group.items.count == 1 ? group.items[0].url.lastPathComponent : "\(group.source) · \(group.items.count)",
             group.items.reduce(Int64(0)) { $0 + (model.reclaimEstimate.perItem[$1.id] ?? $1.size) })
        }
        let busy = model.commitProgress != nil
        return ZStack(alignment: .top) {
            DiskMapTheme.sidebar
            BoardGrid().opacity(0.8)
            ScrollView {
                VStack(spacing: 0) {
                    Receipt(lines: lines, total: model.reclaimEstimate.bytes, freeBefore: vol.map { Int64($0.freeBytes) },
                            volumeName: vol?.volumeName ?? model.rootURL?.lastPathComponent ?? "This Mac", skippedCount: 0, compact: false)
                    if model.reclaimEstimate.heldByUnqueuedCopies > 0 {
                        Text("\(diskByteString(model.reclaimEstimate.heldByUnqueuedCopies)) stays in use by copies or links that aren’t in the basket.")
                            .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.top, 18)
                    }
                    Button { confirmingTrash = true } label: {
                        VStack(alignment: .leading, spacing: 14) {
                            Text("YOU’LL CONFIRM ONCE MORE").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).opacity(0.85)
                            Text(model.stagedItems.isEmpty ? "Nothing to move" : "Move \(countLabel(model.stagedItems.count, "item")) to Trash…")
                                .font(.system(size: DiskMapType.scaled(21), weight: .semibold)).kerning(-0.3)
                        }
                        .foregroundStyle(DiskMapTheme.onAccent)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 20).padding(.vertical, 16)
                        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(DiskMapTheme.accent.opacity(model.stagedItems.isEmpty || busy ? 0.45 : 1)))
                        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(Color.black.opacity(0.85), lineWidth: 1))
                        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.black.opacity(0.88)).offset(x: 5, y: 5))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .disabled(model.stagedItems.isEmpty || busy)
                    .padding(.top, 28).padding(.trailing, 5)
                    .help("Moves everything here to the Trash, after you confirm")
                    .accessibilityLabel("Move \(model.stagedItems.count) items to Trash")
                    lastCleanup.padding(.top, 24)
                }
                .padding(24)
            }
        }
    }

    /// "Verifying sizes… 3 of 12" then "Moving… 7 of 12" while a commit runs.
    @ViewBuilder
    private var commitProgressBar: some View {
        if let progress = model.commitProgress {
            let (label, done, total): (String, Int, Int) = {
                switch progress {
                case let .verifying(done, total): return ("Verifying sizes…", done, total)
                case let .moving(done, total): return ("Moving to the Trash…", done, total)
                }
            }()
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(label).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                    Spacer()
                    Text("\(done) of \(total)").font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                }
                ProgressView(value: Double(done), total: Double(max(1, total)))
                    .progressViewStyle(.linear)
            }
            .padding(.horizontal, 24)
            .padding(.bottom, 12)
            .accessibilityElement(children: .combine)
            .accessibilityIdentifier("cleanup-commit-progress")
        }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Basket")
                    .font(DiskMapType.pageTitle)
                    .foregroundStyle(DiskMapTheme.ink)
                Text(model.stagedItems.isEmpty ? "Nothing in it yet" : "\(countLabel(model.stagedItems.count, "item")) · " + freeSummary)
                    .font(DiskMapType.body)
                    .foregroundStyle(DiskMapTheme.ink2)
            }
            Spacer()
            if model.reclaimEstimate.isCalculating {
                ProgressView().controlSize(.small)
            }
            // Committing finishes any measuring itself, with progress, so the
            // Move button in the receipt never waits on it (MAC-FIXES-FROM-WINDOWS §2.3).
            Button("Done") { dismiss() }
                .keyboardShortcut(.cancelAction)
                .buttonStyle(SecondaryButtonStyle())
        }
        .padding(.horizontal, 24)
        .padding(.top, 22)
        .padding(.bottom, 16)
    }

    private var list: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 14) {
                ForEach(grouped, id: \.source) { group in
                    VStack(spacing: 0) {
                        HStack {
                            Text(group.source).font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
                            Text(countLabel(group.items.count, "item")).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                            Spacer()
                            Text(diskByteString(group.items.reduce(0) { $0 + (model.reclaimEstimate.perItem[$1.id] ?? $1.size) }))
                                .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                        }
                        .padding(.horizontal, 16)
                        .frame(height: 44)
                        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
                        ForEach(group.items) { item in row(item) }
                    }
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                }
            }
            .padding(.horizontal, 24)
            .padding(.bottom, 24)
        }
    }

    private func row(_ item: CleanupQueue.StagedItem) -> some View {
        CleanupRow(item: item, caption: rowCaption(for: item), detail: Self.detail(of: item.reason)) {
            QuickLookPresenter.shared.present(item.url)
        } onRemove: {
            Task {
                await model.unstageFromCleanup(item)
                withAnimation(.easeOut(duration: 0.15)) { lastRemoved = item }
                try? await Task.sleep(nanoseconds: 6_000_000_000)
                if lastRemoved?.id == item.id { withAnimation(.easeOut(duration: 0.15)) { lastRemoved = nil } }
            }
        }
    }

    /// Shown inside the sheet (a toast would sit under it): the last removed
    /// entry, with Undo putting it back exactly as it was staged.
    @ViewBuilder
    private var undoRow: some View {
        if let item = lastRemoved {
            HStack(spacing: 8) {
                Text("Removed “\(item.url.lastPathComponent)” — it stays on disk")
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.ink2)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Button("Undo") { undoRemove(item) }
                    .buttonStyle(LinkButtonStyle())
                    .font(DiskMapType.secondary)
                    .keyboardShortcut("z", modifiers: .command)
                Spacer()
            }
            .padding(.horizontal, 24)
            .frame(height: 40)
            .overlay(alignment: .top) { Hairline() }
            .transition(.opacity)
            .accessibilityElement(children: .combine)
            .accessibilityAction(named: "Undo") { undoRemove(item) }
        }
    }

    private func undoRemove(_ item: CleanupQueue.StagedItem) {
        lastRemoved = nil
        Task {
            _ = await model.stageForCleanup([CleanupStageRequest(
                url: item.url, size: item.size, reason: item.reason,
                sharesStorageGroup: item.sharesStorageGroup, groupCopyCount: item.groupCopyCount
            )])
        }
    }

    /// TASK-080: the last Move to Trash can be undone, from right here.
    @ViewBuilder
    private var lastCleanup: some View {
        if let last = model.lastCleanup, !last.items.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text("Last cleanup").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("\(countLabel(last.items.count, "item")) · \(ByteFormat.hero(last.items.reduce(0) { $0 + $1.bytes }))")
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                        Text(last.date.formatted(.relative(presentation: .named))).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                    }
                    Spacer(minLength: 6)
                    Button("Put Back") { Task { await model.putBackLastCleanup() } }
                        .buttonStyle(SecondaryButtonStyle())
                        .help("Move them from the Trash back where they were. Nothing that is there now is replaced.")
                }
                .padding(.horizontal, 14).padding(.vertical, 10)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(DiskMapTheme.line, style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
                if !model.lastCommitLines.isEmpty {
                    Button(showReceipt ? "Hide what moved" : "Show what moved") {
                        withAnimation(.easeOut(duration: 0.15)) { showReceipt.toggle() }
                    }
                    .buttonStyle(.plain).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
                    if showReceipt {
                        // PR #16: one line per item — bounded so it can't push the column away.
                        ScrollView {
                            Text(model.lastCommitLines.joined(separator: "\n"))
                                .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink2)
                                .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .frame(maxHeight: 140)
                    }
                }
            }
        } else if !model.lastCommitLines.isEmpty {
            ScrollView {
                Text(model.lastCommitLines.joined(separator: "\n"))
                    .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink2)
                    .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 140)
        }
    }

    /// Items grouped by where they came from ("Old Downloads", "Biggest
    /// file"), not by their full per-item reason, which made one-item groups.
    private var grouped: [(source: String, items: [CleanupQueue.StagedItem])] {
        let groups = Dictionary(grouping: model.stagedItems) { Self.source(of: $0.reason) }
        return groups.keys.sorted().map { ($0, groups[$0] ?? []) }
    }

    static func source(of reason: String) -> String {
        let head = reason.split(separator: ":", maxSplits: 1).first.map(String.init) ?? reason
        let trimmed = head.trimmingCharacters(in: .whitespaces)
        guard let first = trimmed.first else { return "Other" }
        return first.uppercased() + trimmed.dropFirst()
    }

    static func detail(of reason: String) -> String? {
        let parts = reason.split(separator: ":", maxSplits: 1)
        guard parts.count == 2 else { return nil }
        let text = parts[1].trimmingCharacters(in: .whitespaces)
        return text.isEmpty ? nil : text
    }
}

/// One staged item: icon, name over path, what it frees, hover actions.
private struct CleanupRow: View {
    let item: CleanupQueue.StagedItem
    let caption: String
    let detail: String?
    var onQuickLook: () -> Void
    var onRemove: () -> Void
    @State private var hovering = false

    private var isFolder: Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: item.url.path, isDirectory: &dir) && dir.boolValue
            && item.url.pathExtension.isEmpty
    }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            if isFolder {
                Image(systemName: "folder")
                    .font(.system(size: DiskMapType.scaled(14)))
                    .foregroundStyle(DiskMapTheme.ink2)
                    .frame(width: 28, height: 28)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(DiskMapTheme.ink.opacity(0.06)))
            } else {
                FileIdentityIcon(url: item.url, size: 28)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(item.url.lastPathComponent)
                    .font(DiskMapType.bodyEmphasis)
                    .foregroundStyle(DiskMapTheme.ink)
                    .lineLimit(1)
                Text(CanonicalPath.parentDisplay(of: item.url.path))
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1)
                    .truncationMode(.middle)
                // Staged from any screen (Biggest Files can surface Docker.raw):
                // warn where the Trash would damage a tool's state.
                if let recipe = CleanupRecipes.recipe(forPath: item.url.path), recipe.trashIsUnsafe {
                    Text("Moving this to the Trash damages \(recipe.id == "docker" ? "Docker" : "the tool")’s data. Use `\(recipe.command)` instead.")
                        .font(DiskMapType.secondary)
                        .foregroundStyle(DiskMapTheme.danger)
                        .fixedSize(horizontal: false, vertical: true)
                }
                // The last Move to Trash left it here: say why, in plain words.
                if let failure = item.lastFailure {
                    Text("Couldn’t move: \(failure)")
                        .font(DiskMapType.secondary)
                        .foregroundStyle(DiskMapTheme.review)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("cleanup-row-failure")
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if hovering {
                HStack(spacing: 0) {
                    Button(action: onQuickLook) { Label("Quick Look", systemImage: "eye") }
                        .help("Quick Look")
                    Button(action: onRemove) { Label("Take out of the basket", systemImage: "xmark") }
                        .help("Take out of the basket — the file stays where it is")
                }
                .buttonStyle(IconButtonStyle(size: 24))
            }
            Text(caption)
                .font(DiskMapType.figureSmall)
                .foregroundStyle(DiskMapTheme.ink2)
                .multilineTextAlignment(.trailing)
                .frame(maxWidth: 220, alignment: .trailing)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .frame(minHeight: DiskMapSpace.rowTwoLine + 4)
        .background(hovering ? DiskMapTheme.soft.opacity(0.6) : Color.clear)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.7)).frame(height: 1) }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .help(detail ?? "")
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(item.url.lastPathComponent), \(caption)")
        .accessibilityAction(named: "Quick Look", onQuickLook)
        .accessibilityAction(named: "Take out of the basket", onRemove)
        .contextMenu {
            Button("Quick Look", action: onQuickLook)
            Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting([item.url]) }
            Divider()
            Button("Take Out of the Basket", action: onRemove)
        }
    }
}

/// Quick Look from SwiftUI sheets needs a real NSResponder in the chain.
/// Folders used to no-op; we preview the largest suitable child file instead.
final class QuickLookPresenter: NSResponder, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = QuickLookPresenter()
    private var url: URL?

    func present(_ url: URL) {
        let target = resolvePreviewURL(url)
        guard let target else {
            NSWorkspace.shared.activateFileViewerSelecting([url.standardizedFileURL])
            return
        }
        self.url = target
        NSApp.activate(ignoringOtherApps: true)
        if nextResponder == nil, let window = NSApp.keyWindow ?? NSApp.mainWindow {
            nextResponder = window.nextResponder
            window.nextResponder = self
        }
        windowMakeFirstResponder()
        guard let panel = QLPreviewPanel.shared() else {
            NSWorkspace.shared.activateFileViewerSelecting([target])
            return
        }
        panel.dataSource = self
        panel.delegate = self
        panel.currentPreviewItemIndex = 0
        panel.reloadData()
        if panel.isVisible {
            panel.refreshCurrentPreviewItem()
        } else {
            panel.makeKeyAndOrderFront(nil)
        }
    }

    private func windowMakeFirstResponder() {
        if let window = NSApp.keyWindow ?? NSApp.mainWindow {
            _ = window.makeFirstResponder(self)
        }
    }

    /// Files preview as-is. Directories resolve to the largest previewable child
    /// (video/image/PDF/archive/disk image) so movie folders Quick Look usefully.
    private func resolvePreviewURL(_ url: URL) -> URL? {
        let standardized = url.standardizedFileURL
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: standardized.path, isDirectory: &isDirectory) else {
            return nil
        }
        if !isDirectory.boolValue { return standardized }
        return largestPreviewableChild(in: standardized) ?? standardized
    }

    private static let previewExtensions: Set<String> = [
        "mkv", "mp4", "mov", "m4v", "avi", "webm",
        "jpg", "jpeg", "png", "heic", "gif", "webp", "tiff", "tif",
        "pdf", "txt", "rtf", "md",
        "zip", "dmg", "iso", "pkg", "rar", "7z",
        "mp3", "m4a", "wav", "aac",
    ]

    private func largestPreviewableChild(in directory: URL) -> URL? {
        let fm = FileManager.default
        guard let enumerator = fm.enumerator(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey, .isDirectoryKey],
            options: [.skipsHiddenFiles, .skipsPackageDescendants]
        ) else { return nil }
        var best: (URL, Int64)?
        var scanned = 0
        for case let fileURL as URL in enumerator {
            scanned += 1
            if scanned > 2_000 { break }
            let ext = fileURL.pathExtension.lowercased()
            guard Self.previewExtensions.contains(ext) else { continue }
            let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey, .isDirectoryKey])
            guard values?.isDirectory != true, values?.isRegularFile == true else { continue }
            let size = Int64(values?.fileSize ?? 0)
            if best == nil || size > best!.1 {
                best = (fileURL, size)
            }
        }
        return best?.0
    }

    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool { true }

    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        panel.dataSource = self
        panel.delegate = self
    }

    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {}

    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int { url == nil ? 0 : 1 }

    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> QLPreviewItem {
        (url ?? URL(fileURLWithPath: "/")) as NSURL
    }
}


extension CleanupQueueView {
    /// Moving to the Trash frees nothing on its own; say when it does.
    var freeSummary: String {
        let estimate = model.reclaimEstimate
        if estimate.isCalculating { return "Calculating what emptying the Trash will free…" }
        let amount = diskByteString(estimate.bytes)
        return estimate.isLowerBound
            ? "At least \(amount) freed when you empty the Trash"
            : "\(amount) freed when you empty the Trash"
    }

    var confirmMessage: String {
        let estimate = model.reclaimEstimate
        let count = model.stagedItems.count
        var text = "Moves \(count) item\(count == 1 ? "" : "s") to the Trash — nothing is deleted permanently. "
        text += estimate.isLowerBound ? "At least " : "About "
        text += "\(diskByteString(estimate.bytes)) is freed once you empty the Trash."
        if estimate.heldByUnqueuedCopies > 0 {
            text += " \(diskByteString(estimate.heldByUnqueuedCopies)) stays in use because other copies or links of these files aren’t queued."
        }
        let unsafe = model.stagedItems.compactMap { CleanupRecipes.recipe(forPath: $0.url.path) }.filter(\.trashIsUnsafe)
        if let first = unsafe.first {
            text += " Warning: the queue includes data a tool manages itself — `\(first.command)` is the safe way to clean it."
        }
        return text
    }

    /// What this row contributes, and why it may be less than its size.
    func rowCaption(for item: CleanupQueue.StagedItem) -> String {
        if item.isMeasuring { return "\(diskByteString(item.size)) · measuring…" }
        let freed = model.reclaimEstimate.perItem[item.id] ?? item.size
        let path = item.url.path
        let insideQueuedFolder = model.stagedItems.contains { other in
            other.id != item.id && path.hasPrefix(other.url.path.hasSuffix("/") ? other.url.path : other.url.path + "/")
        }
        if insideQueuedFolder {
            return "Included in a queued folder — counted there"
        }
        let occupied = item.sharing?.allocatedBytes ?? item.size
        if freed == 0 && occupied > 0 {
            return "\(diskByteString(occupied)) · shared — freed only when every copy or link is queued"
        }
        let base = freed + 4_096 < occupied
            ? "Frees \(diskByteString(freed)) of \(diskByteString(occupied)) — the rest is shared"
            : diskByteString(freed)
        // TASK-082: measured from the last scan (checked unchanged since).
        if case .scan(let date) = item.measurementSource {
            return base + " · from the scan at \(date.formatted(date: .omitted, time: .shortened))"
        }
        return base
    }
}
