import AppKit
import DiskMapCore
import SwiftUI

/// The flat list row every list uses: leading icon, name over a secondary
/// line, trailing mono columns, hover actions, accent selection.
struct KitRow<Leading: View, Trailing: View>: View {
    var title: String
    var subtitle: String?
    var selected: Bool
    /// Hover actions (Quick Look, Reveal, Add to Cleanup) need a path.
    var path: String?
    var onStage: (() -> Void)?
    var height: CGFloat = DiskMapSpace.rowTwoLine
    @ViewBuilder var leading: Leading
    @ViewBuilder var trailing: Trailing
    @State private var hovering = false
    /// Set by `CheckRow`: the highlight reaches back under the checkbox.
    @Environment(\.kitRowLeadingInset) private var leadingInset

    var body: some View {
        HStack(spacing: 12) {
            leading
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(DiskMapType.bodyEmphasis)
                    .foregroundStyle(DiskMapTheme.ink)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if let subtitle {
                    Text(subtitle)
                        .font(DiskMapType.secondary)
                        .foregroundStyle(DiskMapTheme.ink3)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
            .frame(minWidth: 120, maxWidth: .infinity, alignment: .leading)
            if let path, hovering {
                RowHoverActions(path: path, onStage: onStage)
                    .transition(.opacity)
            }
            trailing
        }
        .padding(.horizontal, 10)
        .frame(minHeight: height)
        .background(RowBackground(selected: selected, hovering: hovering).padding(.leading, -leadingInset))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
    }
}

/// The checkbox for review lists: drawn, so it has no focus ring, an
/// accent fill when ticked, and a 24 pt hit area.
struct KitCheckbox: View {
    @Binding var isOn: Bool
    /// What ticking it means for VoiceOver ("Mark Docker").
    var label: String
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button { isOn.toggle() } label: {
            RoundedRectangle(cornerRadius: 4, style: .continuous)
                .fill(isOn ? DiskMapTheme.accent : DiskMapTheme.raised)
                .overlay(
                    RoundedRectangle(cornerRadius: 4, style: .continuous)
                        .strokeBorder(isOn ? DiskMapTheme.accent : DiskMapTheme.ink3.opacity(0.7), lineWidth: 1)
                )
                .overlay {
                    if isOn {
                        Image(systemName: "checkmark")
                            .font(.system(size: DiskMapType.scaled(8.5), weight: .bold))
                            .foregroundStyle(.white)
                    }
                }
                .frame(width: DiskMapType.scaled(14), height: DiskMapType.scaled(14))
                .opacity(isEnabled ? 1 : 0.35)
                .frame(width: 24, height: 24)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .focusEffectDisabled()
        .zIndex(1)
        .accessibilityLabel(label)
        .accessibilityValue(isOn ? "checked" : "unchecked")
        .accessibilityAddTraits(isOn ? [.isToggle, .isSelected] : .isToggle)
    }
}

/// A checkbox followed by a `KitRow`, with the row's highlight running
/// under the checkbox so the two read as one row.
struct CheckRow<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        HStack(spacing: 2) {
            content
        }
        .padding(.leading, 8)
        .environment(\.kitRowLeadingInset, 8 + 24 + 2)
    }
}

private struct KitRowLeadingInsetKey: EnvironmentKey {
    static let defaultValue: CGFloat = 0
}

extension EnvironmentValues {
    var kitRowLeadingInset: CGFloat {
        get { self[KitRowLeadingInsetKey.self] }
        set { self[KitRowLeadingInsetKey.self] = newValue }
    }
}

/// A row's mono column (size, date, count).
struct MonoColumn: View {
    var text: String
    var width: CGFloat
    var emphasis = false
    var body: some View {
        Text(text)
            .font(emphasis ? DiskMapType.figureStrong : DiskMapType.figureSmall)
            .foregroundStyle(emphasis ? DiskMapTheme.ink : DiskMapTheme.ink2)
            .lineLimit(1)
            .frame(width: width * max(1, DiskMapType.scale), alignment: .trailing)
    }
}

/// A row's plain text column (kind, source).
struct TextColumn: View {
    var text: String
    var width: CGFloat
    var body: some View {
        Text(text)
            .font(DiskMapType.secondary)
            .foregroundStyle(DiskMapTheme.ink2)
            .lineLimit(1)
            .frame(width: width * max(1, DiskMapType.scale), alignment: .leading)
    }
}

/// Separator between rows, indented to the text.
struct RowSeparator: View {
    var indent: CGFloat = 52
    var body: some View {
        Rectangle().fill(DiskMapTheme.line.opacity(0.7)).frame(height: 1).padding(.leading, indent)
    }
}

/// The leading slot of a multi-selectable row: a check when ⌘-selected.
struct MultiSelectMark: View {
    var on: Bool
    var body: some View {
        Image(systemName: on ? "checkmark.circle.fill" : "circle")
            .font(.system(size: DiskMapType.scaled(12)))
            .foregroundStyle(on ? DiskMapTheme.accent : .clear)
            .frame(width: 14)
            .accessibilityHidden(true)
    }
}

// MARK: - The one file inspector

/// Used by every page that inspects a single file: header, facts, why, safety, actions.
struct FileInspector: View {
    @ObservedObject var model: ScanModel
    let tree: FileTree
    let rootURL: URL
    let id: Int32
    let size: Int64
    /// Cleanup reason recorded with the staged item ("Biggest file: …").
    var reason: String
    var extraFacts: [(String, String)] = []
    /// Replaces the "why it's large" note (e.g. Forgotten Files' reasons).
    var note: (label: String, text: String)? = nil
    /// False where the page has its own reason not to offer cleanup.
    var allowStage = true
    /// Shown above the header (Large Media's thumbnail).
    var preview: AnyView? = nil

    private var usedDenominator: Int64 {
        if let vol = model.analysis.volume { return max(1, Int64(vol.usedBytes)) }
        return max(1, model.analysis.scannedBytes)
    }

    var body: some View {
        let name = tree.name(of: id)
        let abs = tree.path(of: id, root: rootURL).path
        let url = URL(fileURLWithPath: abs)
        let kind = FileKind.classify(fileName: name, path: abs)
        let safety = SafetyClassifier.assess(path: abs, name: name, isDirectory: false)
        let share = Double(size) / Double(usedDenominator)
        let allowTrash = allowStage && safety.level != .protected && kind != .virtualDisk
        let staged = model.isStaged(url)

        return InspectorColumn {
            if let preview { preview }
            InspectorHeader(name: name, size: ByteFormat.string(size),
                            detail: "\(kind.title) · \(Self.percent(share)) of used space") {
                if preview == nil { FileIdentityIcon(url: url, kind: kind, size: 40) }
            }
            Hairline()
            FactRow(label: "Location", value: CanonicalPath.parentDisplay(of: abs))
            FactRow(label: "Modified", value: RelativeAge.long(day: tree.modifiedDay[Int(id)]))
            ForEach(extraFacts, id: \.0) { fact in FactRow(label: fact.0, value: fact.1) }
            if let shared = tree.sharingInfo(of: id) {
                FactRow(label: "APFS clone", value: "Shares \(ByteFormat.string(shared.sharedBytes)) with \(shared.otherCopies) other cop\(shared.otherCopies == 1 ? "y" : "ies")")
            }
            Hairline()
            if let note {
                Note(label: note.label, text: note.text)
            } else {
                Note(label: "Why it's large", text: FileKind.whyLarge(kind: kind, name: name))
            }
            SafetyLine(assessment: safety)
            if kind == .virtualDisk || name.lowercased().hasSuffix(".raw") {
                Text("Don't delete this file directly — manage its storage in the app that owns it.")
                    .font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.danger)
                    .fixedSize(horizontal: false, vertical: true)
            }
            InspectorActions(
                primaryTitle: staged ? "In Cleanup" : "Add to Cleanup",
                primaryDone: staged,
                primaryEnabled: allowTrash,
                primary: {
                    if staged { model.isCleanupQueuePresented = true } else {
                        model.stageRow(path: abs, size: size, reason: reason)
                    }
                },
                path: abs
            ) {
                Button("Show in Visualize") {
                    model.selectedNode = id
                    model.currentNode = max(0, tree.parent[Int(id)])
                    model.destination = .visualize
                }
                Button("Show in File Browser") {
                    model.currentNode = max(0, tree.parent[Int(id)])
                    model.selectedNode = id
                    model.destination = .fileBrowser
                }
            }
            .padding(.top, 4)
        }
    }

    static func percent(_ fraction: Double) -> String {
        let pct = fraction * 100
        if pct > 0 && pct < 0.1 { return "<0.1%" }
        return String(format: "%.1f%%", min(100, pct))
    }
}

// MARK: - The one folder inspector

/// Used by every page that inspects a folder: header, composition, largest
/// files, why, safety, actions.
/// A folder whose size is mostly cloned copies (clone accounting off): what
/// it really takes up, and the way to exact figures.
struct CloneCopiesNote: View {
    @ObservedObject var model: ScanModel
    var counted: Int64
    var shared: Int64

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            MonoLabel("Shared copies")
            Text(Self.sentence(counted: counted, shared: shared))
                .font(DiskMapType.secondary)
                .foregroundStyle(DiskMapTheme.ink2)
                .fixedSize(horizontal: false, vertical: true)
            Button("Count clones once (rescan)") { model.countClonesOnce() }
                .buttonStyle(LinkButtonStyle())
                .font(DiskMapType.secondary)
                .disabled(model.isScanning)
                .help(CloneAccounting.explanation(.refcount))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("clone-copies-note")
    }

    static func sentence(counted: Int64, shared: Int64) -> String {
        (Double(shared) >= Double(counted) * 0.5 ? "Most" : "Part") + " of this is APFS clones — copies that share one set of blocks. "
            + "\(ByteFormat.string(counted)) of files, but only about \(ByteFormat.string(max(0, counted - shared))) on disk."
    }
}

struct FolderInspector: View {
    @ObservedObject var model: ScanModel
    let tree: FileTree
    let rootURL: URL
    let id: Int32
    /// Cleanup reason recorded with the staged folder.
    var reason: String? = nil

    private var usedDenominator: Int64 {
        if let vol = model.analysis.volume { return max(1, Int64(vol.usedBytes)) }
        return max(1, model.analysis.scannedBytes)
    }

    /// Built off the main thread: composition and largest files walk the
    /// whole subtree, which is slow for a home folder or a volume root.
    @State private var insight: FolderInsight?
    @State private var builtFor: String?

    private var buildKey: String { "\(id):\(model.scanID):\(model.sizeBasis)" }

    var body: some View {
        Group {
            if let insight, insight.nodeID == id {
                content(insight)
            } else if builtFor == buildKey {
                DiskMapEmptyState(symbol: "folder", title: "Select a folder", message: "Its details and actions appear here.")
            } else {
                DiskMapLoadingState(title: "Reading folder", detail: tree.name(of: id))
            }
        }
        .task(id: buildKey) {
            let key = buildKey
            let (tree, root, id) = (self.tree, self.rootURL, self.id)
            let totals = model.selectedTotals
            let files = model.descendantFileCounts, folders = model.descendantFolderCounts
            let categories = model.fileTypeCategories
            let built = await Task.detached(priority: .userInitiated) {
                FolderInsight.build(nodeID: id, tree: tree, root: root, totals: totals,
                                    fileCounts: files, folderCounts: folders, categories: categories)
            }.value
            guard !Task.isCancelled else { return }
            insight = built
            builtFor = key
        }
    }

    private func content(_ insight: FolderInsight) -> some View {
        let url = URL(fileURLWithPath: insight.absolutePath, isDirectory: true)
        let staged = model.isStaged(url)
        let index = model.freeableIndex
        let totals = model.selectedTotals
        let freeBytes = index.bytes(under: id, tree: tree, totals: totals)
        let insideTarget = index.contains(id, tree: tree)
        let verdict = StorageClassifier.classify(path: insight.absolutePath, isDirectory: true)
        let kids = tree.children(of: id, totals: totals).filter { $0.size > 0 }.sorted { $0.size > $1.size }.prefix(4)
        let biggestKid = max(1, kids.first?.size ?? 1)
        return InspectorColumn {
            VStack(alignment: .leading, spacing: 4) {
                Text("Selected folder").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                Text(insight.name).font(.system(size: DiskMapType.scaled(22), weight: .semibold)).kerning(-0.5)
                    .foregroundStyle(DiskMapTheme.ink).lineLimit(2)
                Text(insight.displayPath).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(2).truncationMode(.middle)
                    .textSelection(.enabled)
            }
            Text(ByteFormat.hero(insight.bytes))
                .font(.system(size: DiskMapType.scaled(40), weight: .semibold).monospacedDigit())
                .kerning(-1.6)
                .foregroundStyle(DiskMapTheme.ink)
                .lineLimit(1).minimumScaleFactor(0.6)
            VStack(spacing: 7) {
                kv("You can free", freeBytes > 0 ? ByteFormat.string(freeBytes) : "Nothing here", accent: freeBytes > 0)
                kv("Last changed", VisualizeView.lastUsed(tree.modifiedDay[Int(id)], today: AgeMap.today()))
                kv("Files", insight.fileCount.formatted())
            }
            if let shared = model.cloneSharedBytes(of: id) {
                CloneCopiesNote(model: model, counted: model.allocatedTotals[Int(id)], shared: shared)
            }
            VStack(alignment: .leading, spacing: 3) {
                Text("What is this?").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                Text(insideTarget ? "This rebuilds by itself. " + (verdict.storageClass.blurb.isEmpty ? insight.whyLarge : verdict.storageClass.blurb)
                     : (verdict.storageClass.id == "other" ? insight.whyLarge : verdict.storageClass.blurb))
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
                if let note = verdict.note {
                    Text(note).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2).padding(.top, 2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(.vertical, 10)
            .padding(.horizontal, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            .overlay(alignment: .leading) {
                UnevenRoundedRectangle(topLeadingRadius: 7, bottomLeadingRadius: 7).fill(DiskMapTheme.accent).frame(width: 3)
            }
            if !kids.isEmpty {
                VStack(alignment: .leading, spacing: 7) {
                    Text("Biggest inside").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    VStack(spacing: 0) {
                        ForEach(Array(kids), id: \.id) { kid in
                            let rebuilds = index.contains(kid.id, tree: tree)
                            Button { model.selectedNode = kid.id } label: {
                                HStack(spacing: 7) {
                                    Text(tree.name(of: kid.id)).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                                    if rebuilds { Text("rebuilds").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep) }
                                    Spacer(minLength: 4)
                                    Text(ByteFormat.string(kid.size)).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink)
                                }
                                .padding(.horizontal, 10)
                                .frame(height: 34)
                                .background(alignment: .leading) {
                                    GeometryReader { g in
                                        RoundedRectangle(cornerRadius: 4, style: .continuous).fill(DiskMapTheme.soft)
                                            .frame(width: max(6, (g.size.width - 10) * 0.86 * CGFloat(Double(kid.size) / Double(biggestKid))))
                                            .padding(.vertical, 5).padding(.leading, 5)
                                    }
                                }
                                .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.6)).frame(height: 1) }
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
                }
            }
            if !insight.largestFiles.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("Largest files").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                        Spacer()
                        Button("View all") {
                            model.folderFilterPath = insight.absolutePath
                            model.destination = .biggestFiles
                        }
                        .buttonStyle(LinkButtonStyle())
                        .font(DiskMapType.secondary)
                    }
                    ForEach(insight.largestFiles.prefix(4), id: \.id) { file in
                        HStack {
                            Text(file.name).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink)
                                .lineLimit(1).truncationMode(.middle)
                            Spacer(minLength: 6)
                            Text(ByteFormat.string(file.bytes)).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink2)
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
            }
            SafetyLine(assessment: insight.safety)
            VStack(spacing: 8) {
                HStack(spacing: 8) {
                    Button("Reveal") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                    Button("Quick Look") { DiskMapQuickLook.shared.show(url) }
                        .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                }
                if freeBytes > 0, !insideTarget {
                    Button("Add \(ByteFormat.string(freeBytes)) to basket") { Task { await stageFreeable() } }
                        .buttonStyle(AccentButtonStyle(large: true, fullWidth: true, hard: true))
                        .padding(.trailing, 3)
                        .help("Adds only what rebuilds by itself, as in Clean up")
                } else {
                    Button(staged ? "In basket" : "Add \(ByteFormat.string(insight.bytes)) to basket") {
                        if staged { model.isCleanupQueuePresented = true } else {
                            model.stageRow(path: insight.absolutePath, size: insight.bytes,
                                           reason: reason ?? "Folder: " + insight.name)
                        }
                    }
                    .buttonStyle(AccentButtonStyle(large: true, fullWidth: true, hard: !staged))
                    .padding(.trailing, 3)
                    .disabled(insight.safety.level == .protected)
                }
                Menu("More") {
                    Button("Open in File Browser") {
                        model.currentNode = id
                        model.selectedNode = id
                        model.destination = .fileBrowser
                    }
                    Button("Show in Explore") {
                        model.currentNode = id
                        model.selectedNode = id
                        model.destination = .visualize
                    }
                    Button("Biggest Files in This Folder") {
                        model.folderFilterPath = insight.absolutePath
                        model.destination = .biggestFiles
                    }
                    Button("Copy Path") { copyPaths([insight.absolutePath]) }
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .padding(.top, 4)
        }
    }

    private func kv(_ label: String, _ value: String, accent: Bool = false) -> some View {
        HStack {
            Text(label).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
            Spacer(minLength: 8)
            Text(value).font(DiskMapType.figure).foregroundStyle(accent ? DiskMapTheme.accent : DiskMapTheme.ink).lineLimit(1)
        }
    }

    /// The safe Clean up items inside this folder, staged as one bulk add.
    private func stageFreeable() async {
        let totals = model.selectedTotals
        let inside = model.cachedReviewables.filter { t in
            t.isGenerallySafe && t.nodeIDs.contains { $0 == id || FreeableIndex.isAncestor(id, of: $0, tree: tree) }
        }
        let requests: [CleanupStageRequest] = inside.flatMap { t in
            t.nodeIDs.filter { $0 == id || FreeableIndex.isAncestor(id, of: $0, tree: tree) }.map { node in
                CleanupStageRequest(url: tree.path(of: node, root: rootURL), size: Int(node) < totals.count ? totals[Int(node)] : 0,
                                    reason: "Explore: " + t.displayName)
            }
        }
        _ = await model.confirmStageMany(requests, title: "Things that rebuild by themselves in \(tree.name(of: id))")
    }
}
