import AppKit
import DiskMapCore
import SwiftUI

/// Duplicates (redesign board 07): groups of byte-identical files with one
/// copy kept and the others ticked, a choice of which copy stays, and the
/// APFS clones that only look like duplicates set aside.
struct DuplicatesView: View {
    @ObservedObject var model: ScanModel
    let tree: FileTree
    let rootURL: URL

    @State private var checked: Set<Int32> = []
    /// Remembered, and set in Settings › Clean up.
    @AppStorage(KeepPolicy.key) private var policy: KeepPolicy = .oldest
    @State private var kindFilter: DupKind = .all
    /// Per group (by hash): the copy the person chose to keep instead.
    @State private var keeperOverride: [String: Int32] = [:]
    @Environment(\.widthClass) private var widthClass

    enum KeepPolicy: String, CaseIterable, Identifiable {
        case oldest, newest, shortestPath
        static let key = "Duplicates.Keep"
        var id: String { rawValue }
        var title: String {
            switch self {
            case .oldest: return "Oldest"
            case .newest: return "Newest"
            case .shortestPath: return "Shortest path"
            }
        }
        var tag: String {
            switch self {
            case .oldest: return "keeps the oldest"
            case .newest: return "keeps the newest"
            case .shortestPath: return "keeps the shortest path"
            }
        }
    }

    enum DupKind: String, CaseIterable, Identifiable {
        case all, videos, photos, archives, audio, documents, other
        var id: String { rawValue }
        var title: String {
            switch self {
            case .all: return "All"
            case .videos: return "Videos"
            case .photos: return "Photos"
            case .archives: return "Archives"
            case .audio: return "Audio"
            case .documents: return "Documents"
            case .other: return "Other"
            }
        }
    }

    private var hasGroups: Bool { !model.duplicateGroups.isEmpty && !model.isFindingDuplicates && model.duplicateError == nil }

    /// Groups that free something; whole-group clones are listed in the banner instead.
    private var realGroups: [DuplicateGroup] { model.duplicateGroups.filter { !$0.sharesStorage } }
    private var cloneGroups: [DuplicateGroup] { model.duplicateGroups.filter(\.sharesStorage) }

    private var shownGroups: [DuplicateGroup] {
        let groups = kindFilter == .all ? realGroups : realGroups.filter { Self.kind(of: tree.name(of: $0.fileIDs.first ?? 0)) == kindFilter }
        return groups.sorted { reclaim($0, deleting: Set(extras($0))) > reclaim($1, deleting: Set(extras($1))) }
    }

    var body: some View {
        Group {
            if hasGroups {
                HStack(spacing: 0) {
                    mainColumn
                    Rectangle().fill(DiskMapTheme.line).frame(width: 1)
                    summaryColumn
                        .frame(width: widthClass == .compact ? 290 : widthClass == .regular ? 320 : 360)
                        .background(DiskMapTheme.sidebar)
                }
            } else {
                emptyOrWorking
            }
        }
        .background(DiskMapTheme.page)
        // Extra copies start ticked; the oldest copy of each stays
        // (MAC-FIXES-FROM-WINDOWS §4.4 — the boxes did nothing on their own).
        .onChange(of: model.duplicateGroups.map { $0.fileIDs }) { _, _ in
            keeperOverride = [:]
            selectOtherCopies()
        }
        .onChange(of: policy) { _, _ in
            keeperOverride = [:]
            selectOtherCopies()
        }
        .onAppear { if checked.isEmpty { selectOtherCopies() } }
    }

    // MARK: Main column

    private var mainColumn: some View {
        VStack(alignment: .leading, spacing: widthClass.gap) {
            header
            if !cloneGroups.isEmpty { cloneBanner }
            filterRow
            ScrollView {
                LazyVStack(spacing: widthClass.gap) {
                    ForEach(shownGroups, id: \.hash) { group in groupCard(group) }
                    if shownGroups.isEmpty {
                        Text(realGroups.isEmpty ? "Every duplicate here is an APFS clone, so there is nothing to free."
                                                : "No duplicate \(kindFilter.title.lowercased()) here.")
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3)
                            .padding(.top, 30)
                    }
                }
                .padding(.bottom, widthClass.pagePadding)
            }
            .listKeyboard(
                ids: shownGroups.flatMap(\.fileIDs), selection: keyboardSelection,
                path: { tree.path(of: $0, root: rootURL).path },
                stage: { id in stageOne(id) },
                selectAll: { selectOtherCopies() },
                clearSelection: { checked.removeAll() }
            )
        }
        .padding(.horizontal, widthClass.pagePadding)
        .padding(.top, widthClass == .compact ? 14 : 20)
        .frame(maxWidth: .infinity)
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 6) {
                (Text("Duplicates").foregroundColor(DiskMapTheme.ink)
                 + Text(" · ").foregroundColor(DiskMapTheme.ink3)
                 + (extraCopiesBytes > 0
                    ? Text(ByteFormat.hero(extraCopiesBytes)).foregroundColor(DiskMapTheme.accent) + Text(" you can free").foregroundColor(DiskMapTheme.ink)
                    : Text("nothing to free").foregroundColor(DiskMapTheme.ink3)))
                    .font(DiskMapType.pageTitle)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Text("Files with exactly the same contents. We keep the \(policy == .shortestPath ? "copy with the shortest path" : policy.title.lowercased() + " copy") and tick the rest, but never files inside projects or apps.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
            }
            Spacer(minLength: 8)
            Button("Search again") { Task { await model.findDuplicates() } }
                .buttonStyle(SecondaryButtonStyle())
        }
    }

    private var cloneBanner: some View {
        let count = cloneGroups.count
        return HStack(alignment: .top, spacing: 24) {
            VStack(alignment: .leading, spacing: 6) {
                Text("\(count) \(count == 1 ? "group looks" : "groups look") like duplicates but \(count == 1 ? "shares" : "share") storage.")
                    .font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                Text("They are APFS clones, so removing one frees nothing. \(extraCopiesBytes > 0 ? "We left them out of the \(ByteFormat.hero(extraCopiesBytes))." : "")")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: 420, alignment: .leading)
            if widthClass != .compact {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(cloneGroups.prefix(3), id: \.hash) { group in
                        HStack(spacing: 8) {
                            Image(systemName: "square.on.square").font(.system(size: 11)).foregroundStyle(DiskMapTheme.ink3)
                            Text(tree.name(of: group.fileIDs.first ?? 0)).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).lineLimit(1)
                            Spacer(minLength: 8)
                            Text("\(group.fileIDs.count) × \(ByteFormat.hero(onDisk(group))) · 0 freed").font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                        }
                    }
                    if count > 3 {
                        Text("and \(count - 3) more").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    }
                }
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 16)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .overlay(alignment: .leading) {
            UnevenRoundedRectangle(topLeadingRadius: 12, bottomLeadingRadius: 12).fill(DiskMapTheme.ink3).frame(width: 3)
        }
    }

    private var filterRow: some View {
        let present = Set(realGroups.map { Self.kind(of: tree.name(of: $0.fileIDs.first ?? 0)) })
        let kinds = DupKind.allCases.filter { $0 == .all || present.contains($0) }
        return HStack(spacing: 12) {
            if kinds.count > 2 {
                SegmentSwitch(items: kinds.map { kind in .init(title: kind.title, isOn: kindFilter == kind) { kindFilter = kind } })
            }
            Spacer(minLength: 8)
            Text("Keep").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
            SegmentSwitch(items: KeepPolicy.allCases.map { p in .init(title: p.title, isOn: policy == p) { policy = p } })
        }
    }

    // MARK: Group cards

    private func groupCard(_ group: DuplicateGroup) -> some View {
        let keep = keeper(group)
        let ordered = Self.ordered(group, keeper: keep)
        let name = tree.name(of: keep ?? group.fileIDs.first ?? 0)
        let frees = reclaim(group, deleting: checked)
        return VStack(spacing: 0) {
            HStack(spacing: 14) {
                Text(BiggestFilesView.badge(name, kind: FileKind.classify(fileName: name, path: name)))
                    .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                    .foregroundStyle(DiskMapTheme.ink2)
                    .frame(width: 40, height: 40, alignment: .bottomLeading)
                    .padding(.leading, 8).padding(.bottom, 6)
                    .frame(width: 40, height: 40)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.soft))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 4) {
                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                        Text(name).font(.system(size: DiskMapType.scaled(15), weight: .semibold)).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                        Text("\(group.fileIDs.count) copies · \(ByteFormat.hero(onDisk(group))) each".uppercased())
                            .font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
                            .foregroundStyle(DiskMapTheme.ink3).lineLimit(1).fixedSize()
                        if widthClass != .compact {
                            Text(keeperOverride[group.hash] != nil ? "you chose which stays" : policy.tag)
                                .font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
                                .foregroundStyle(DiskMapTheme.ink2)
                                .padding(.horizontal, 8).frame(height: 22)
                                .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                                .fixedSize()
                        }
                    }
                    if !group.cloneSets.isEmpty {
                        Text("Some copies are APFS clones of each other: removing one frees nothing until its clones go too.")
                            .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).lineLimit(2)
                    }
                }
                Spacer(minLength: 8)
                Text(frees > 0 ? ByteFormat.hero(frees) : "0 B")
                    .font(.system(size: DiskMapType.scaled(14), weight: .medium, design: .monospaced))
                    .foregroundStyle(frees > 0 ? DiskMapTheme.accent : DiskMapTheme.ink3)
                    .help("Freed by the ticked copies in this group")
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 14)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            ForEach(ordered, id: \.self) { id in copyRow(id, group: group, isKeeper: id == keep) }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func copyRow(_ id: Int32, group: DuplicateGroup, isKeeper: Bool) -> some View {
        let url = tree.path(of: id, root: rootURL)
        let staged = model.isStaged(url)
        let isOn = checked.contains(id)
        let selected = model.selectedNode == id
        return HStack(spacing: 14) {
            Group {
                if staged {
                    Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(DiskMapTheme.accent)
                        .help("Already in the basket")
                } else {
                    TickBox(state: isOn ? .on : .off) { toggle(id, in: group, isKeeper: isKeeper) }
                        .help(isKeeper ? "Tick to remove this one and keep another copy instead" : isOn ? "Untick to keep this copy too" : "Tick to remove this copy")
                        .accessibilityLabel(isOn ? "Unmark \(tree.name(of: id))" : "Mark \(tree.name(of: id)) for removal")
                        .accessibilityAddTraits(.isButton)
                }
            }
            .frame(width: 20)
            Text(Self.place(url.path, root: rootURL.path))
                .font(.system(size: DiskMapType.scaled(12.5), design: .monospaced))
                .foregroundStyle(isOn || isKeeper ? DiskMapTheme.ink : DiskMapTheme.ink2)
                .lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(Self.dateText(day: tree.modifiedDay[Int(id)]))
                .font(.system(size: DiskMapType.scaled(12), design: .monospaced))
                .foregroundStyle(DiskMapTheme.ink3)
                .frame(width: 104, alignment: .trailing)
            Group {
                if isKeeper {
                    BoardTag(text: "Kept", tone: .ink)
                } else if staged {
                    BoardTag(text: "In basket", tone: .accent)
                } else {
                    Text(ByteFormat.hero(tree.allocatedSize[Int(id)])).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                }
            }
            .frame(width: 80, alignment: .trailing)
        }
        .padding(.leading, 20)
        .padding(.trailing, 20)
        .frame(height: 44)
        .background(selected ? DiskMapTheme.accentWash.opacity(0.6) : Color.clear)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.7)).frame(height: 1) }
        .contentShape(Rectangle())
        .onTapGesture {
            model.selectedNode = id
            let parent = tree.parent[Int(id)]
            if parent >= 0 { model.currentNode = parent }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(tree.name(of: id)), \(diskByteString(tree.allocatedSize[Int(id)]))\(isKeeper ? ", kept" : isOn ? ", ticked" : "")")
        .rowActions(path: url.path, stage: nil)
    }

    // MARK: Summary column

    private var summaryColumn: some View {
        let groups = realGroups
        let ticked = groups.flatMap { $0.fileIDs.filter(checked.contains) }
        let kept = groups.filter { g in g.fileIDs.contains { !checked.contains($0) } }.count
        return VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack(alignment: .firstTextBaseline) {
                            Text("Duplicates").font(.system(size: DiskMapType.scaled(16), weight: .semibold)).foregroundStyle(DiskMapTheme.ink)
                            Spacer()
                            Text(rootURL.lastPathComponent.uppercased()).font(.system(size: DiskMapType.scaled(10), weight: .medium, design: .monospaced))
                                .foregroundStyle(DiskMapTheme.ink3).lineLimit(1)
                        }
                        LeaderRow(label: "Extra copies", value: "\(ticked.count)")
                        LeaderRow(label: "Groups", value: "\(groups.count)")
                        LeaderRow(label: "Originals kept", value: "\(kept) of \(groups.count)")
                        if !cloneGroups.isEmpty {
                            LeaderRow(label: "Clones skipped", value: "\(cloneGroups.count) · 0 B", faint: true)
                        }
                        Rectangle().fill(DiskMapTheme.line).frame(height: 1).padding(.vertical, 4)
                        LeaderRow(label: "You get back", value: ByteFormat.hero(reclaimable), emphasis: true, accentValue: true)
                        LeaderRow(label: "Where it goes", value: "The Trash")
                        LeaderRow(label: "Undo", value: "Put Back, any time")
                    }
                    .padding(18)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    VStack(alignment: .leading, spacing: 6) {
                        Text("How we check").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                        Text("Same size, then the same contents byte by byte. Names don't matter. A file is never a duplicate of itself through a link.")
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(16)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    .overlay(alignment: .leading) {
                        UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10).fill(DiskMapTheme.accent).frame(width: 3)
                    }
                    Text("Untick a copy to keep it. Tick a kept file to swap which one stays.")
                        .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3).fixedSize(horizontal: false, vertical: true)
                }
                .padding(20)
            }
            VStack(spacing: 10) {
                Button(checked.isEmpty ? "Tick the copies to keep" : "Untick all") {
                    if checked.isEmpty { selectOtherCopies() } else { checked.removeAll() }
                }
                .buttonStyle(SecondaryButtonStyle(fullWidth: true))
                Button("Add \(checked.count.formatted()) \(checked.count == 1 ? "copy" : "copies") to basket") { Task { await stageSelected() } }
                    .buttonStyle(AccentButtonStyle(large: true, fullWidth: true, hard: !checked.isEmpty))
                    .disabled(checked.isEmpty)
                Text("Originals stay where they are").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
            }
            .padding(20)
        }
    }

    // MARK: Empty and working states

    @ViewBuilder
    private var emptyOrWorking: some View {
        if model.isFindingDuplicates {
            loadingState
        } else if let err = model.duplicateError {
            DiskMapEmptyState(
                symbol: "exclamationmark.triangle", title: "Couldn’t finish duplicate search", message: err,
                primaryTitle: "Try again", primaryAction: { Task { await model.findDuplicates() } },
                secondaryTitle: "Clear",
                secondaryAction: {
                    model.duplicateError = nil
                    model.duplicatePhase = .idle
                }
            )
        } else if model.duplicatePhase == .cancelled && model.duplicateGroups.isEmpty {
            DiskMapEmptyState(
                symbol: "stop.circle", title: "Search cancelled",
                message: "No duplicate groups were kept from the cancelled run. Start again when you’re ready.",
                primaryTitle: "Find Duplicates", primaryAction: { Task { await model.findDuplicates() } }
            )
        } else {
            DiskMapEmptyState(
                symbol: "doc.on.doc",
                title: model.duplicateDidRun ? "No duplicates found" : "Find files with identical contents",
                message: model.duplicateDidRun
                    ? "Nothing in this scan is stored twice."
                    : "freedisk.space compares local files in stages, on this Mac. Cloud-only placeholders are skipped; shared APFS clones are recognised.",
                primaryTitle: model.duplicateDidRun ? "Search Again" : "Find Duplicates",
                primaryAction: { Task { await model.findDuplicates() } },
                dusty: model.duplicateDidRun ? .happy : nil
            )
        }
    }

    private var loadingState: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            let total = model.duplicateProgressTotal
            let done = model.duplicateProgressDone
            DiskMapLoadingState(
                title: model.duplicatePhase.title,
                detail: loadingDetail(at: context.date),
                fraction: total > 0 ? Double(done) / Double(total) : nil,
                processed: total > 0 ? done : nil,
                total: total > 0 ? total : nil,
                onCancel: { model.cancelDuplicateSearch() }
            )
        }
    }

    private func loadingDetail(at now: Date) -> String {
        let elapsed = Int(now.timeIntervalSince(model.duplicateStartedAt ?? now))
        let stalled = now.timeIntervalSince(model.duplicateLastProgressAt ?? now) >= 30
        let suffix = stalled ? " No progress has been reported for 30 seconds; the disk may still be working. You can cancel safely." : " Elapsed: \(elapsed)s."
        switch model.duplicatePhase {
        case .preparing: return "Preparing a local comparison." + suffix
        case .collecting:
            return "\(model.duplicateFilesExamined.formatted()) items checked · \(model.duplicateCandidateCount.formatted()) local candidates. Cloud-only placeholders are skipped." + suffix
        case .grouping: return "Looking for files that share the same size — the inexpensive first filter." + suffix
        case .hashing: return "Comparing candidate groups locally. Shared APFS clones avoid a full content hash when possible." + suffix
        case .assembling: return "Preparing the groups for review." + suffix
        default: return "Working…"
        }
    }

    // MARK: Keeping and ticking

    private func keeper(_ group: DuplicateGroup) -> Int32? {
        if let chosen = keeperOverride[group.hash], group.fileIDs.contains(chosen) { return chosen }
        return Self.keeper(group, policy: policy, day: { tree.modifiedDay[Int($0)] },
                           pathLength: { tree.path(of: $0, root: rootURL).path.count })
    }

    /// Which copy stays under a policy. Ties go to the oldest, then the lowest id, so it never flickers.
    static func keeper(_ group: DuplicateGroup, policy: KeepPolicy, day: (Int32) -> Int32, pathLength: (Int32) -> Int) -> Int32? {
        switch policy {
        case .oldest:
            return group.defaultKeeperID(modifiedDay: day)
        case .newest:
            return group.fileIDs.min { a, b in day(a) != day(b) ? day(a) > day(b) : a < b }
        case .shortestPath:
            return group.fileIDs.min { a, b in
                if pathLength(a) != pathLength(b) { return pathLength(a) < pathLength(b) }
                return day(a) != day(b) ? day(a) < day(b) : a < b
            }
        }
    }

    /// The kept copy first, then the rest as found.
    static func ordered(_ group: DuplicateGroup, keeper: Int32?) -> [Int32] {
        guard let keeper else { return group.fileIDs }
        return [keeper] + group.fileIDs.filter { $0 != keeper }
    }

    private func extras(_ group: DuplicateGroup) -> [Int32] {
        let keep = keeper(group)
        return group.fileIDs.filter { $0 != keep }
    }

    /// Ticking the kept copy swaps: the first other copy becomes the one that stays.
    static func swap(_ group: DuplicateGroup, keeper: Int32, checked: Set<Int32>) -> (keeper: Int32, checked: Set<Int32>)? {
        let others = group.fileIDs.filter { $0 != keeper }
        guard let next = others.first(where: { !checked.contains($0) }) ?? others.first else { return nil }
        var result = checked
        result.insert(keeper)
        result.remove(next)
        return (next, result)
    }

    private func toggle(_ id: Int32, in group: DuplicateGroup, isKeeper: Bool) {
        if checked.contains(id) {
            checked.remove(id)
        } else if isKeeper {
            guard let swapped = Self.swap(group, keeper: id, checked: checked) else { return }
            keeperOverride[group.hash] = swapped.keeper
            checked = swapped.checked
        } else {
            checked.insert(id)
        }
        // Never every copy of a file.
        if group.fileIDs.allSatisfy(checked.contains) {
            checked.remove(id)
            model.showToast("Keep at least one copy")
        }
    }

    private func selectOtherCopies() {
        var next: Set<Int32> = []
        for group in model.duplicateGroups where !group.sharesStorage {
            let keep = keeper(group)
            // Copies already in the basket stay as they are; copies a project
            // or app owns (node_modules, .git, bundles…) are never pre-ticked.
            for id in group.fileIDs where id != keep {
                let url = tree.path(of: id, root: rootURL)
                if !model.isStaged(url), !DuplicatePreTick.isToolOwned(path: url.path) { next.insert(id) }
            }
        }
        checked = next
    }

    // MARK: Figures

    /// On-disk size of one member, the basis every reclaim figure uses.
    private func onDisk(_ group: DuplicateGroup) -> Int64 {
        group.fileIDs.map { tree.allocatedSize[Int($0)] }.max() ?? group.sizeEach
    }

    private func reclaim(_ group: DuplicateGroup, deleting set: Set<Int32>) -> Int64 {
        group.reclaimableBytes(deleting: set) { tree.allocatedSize[Int($0)] }
    }

    private var reclaimable: Int64 { model.duplicateGroups.reduce(Int64(0)) { $0 + reclaim($1, deleting: checked) } }

    /// What removing every copy but the kept one would free.
    private var extraCopiesBytes: Int64 { realGroups.reduce(Int64(0)) { $0 + reclaim($1, deleting: Set(extras($1))) } }

    // MARK: Words

    static func kind(of name: String) -> DupKind {
        let ext = (name as NSString).pathExtension.lowercased()
        let photos: Set<String> = ["jpg", "jpeg", "heic", "heif", "png", "gif", "tif", "tiff", "dng", "raw", "cr2", "cr3", "nef", "arw", "orf", "webp"]
        let audio: Set<String> = ["mp3", "wav", "aif", "aiff", "m4a", "flac", "aac", "ogg", "caf"]
        if photos.contains(ext) { return .photos }
        if audio.contains(ext) { return .audio }
        switch FileKind.classify(fileName: name, path: name) {
        case .video: return .videos
        case .archive, .diskImage: return .archives
        case .document: return .documents
        default: return .other
        }
    }

    /// "~/Movies/Holiday.mov" under home; otherwise from the scanned folder ("dupdemo/Movies/Holiday.mov").
    static func place(_ path: String, root: String) -> String {
        let display = CanonicalPath.displayPath(absolutePath: path)
        if display.hasPrefix("~") { return display }
        let base = root.hasSuffix("/") ? String(root.dropLast()) : root
        guard !base.isEmpty, path.hasPrefix(base + "/") else { return display }
        return (base as NSString).lastPathComponent + String(path.dropFirst(base.count))
    }

    /// "12 Jan 2024", from days since 1970.
    static func dateText(day: Int32) -> String {
        guard day > 0 else { return "—" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_GB")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "dd MMM yyyy"
        return formatter.string(from: Date(timeIntervalSince1970: TimeInterval(day) * 86_400))
    }

    // MARK: Staging

    private var keyboardSelection: Binding<Int32?> {
        Binding(
            get: { model.duplicateGroups.contains { $0.fileIDs.contains(model.selectedNode) } ? model.selectedNode : nil },
            set: { id in
                guard let id else { return }
                model.selectedNode = id
                let parent = tree.parent[Int(id)]
                if parent >= 0 { model.currentNode = parent }
            }
        )
    }

    private func otherCopiesStaged(_ id: Int32, in group: DuplicateGroup) -> Bool {
        group.fileIDs.filter { $0 != id }.allSatisfy { model.isStaged(tree.path(of: $0, root: rootURL)) }
    }

    private func stageOne(_ id: Int32) {
        if let group = model.duplicateGroups.first(where: { $0.fileIDs.contains(id) }), otherCopiesStaged(id, in: group) {
            model.showToast("Keep at least one copy")
            return
        }
        model.stageRow(path: tree.path(of: id, root: rootURL).path, size: tree.allocatedSize[Int(id)], reason: "Duplicate of \(tree.name(of: id))")
    }

    private func stageSelected() async {
        var requests: [CleanupStageRequest] = []
        for group in model.duplicateGroups {
            for id in group.fileIDs where checked.contains(id) {
                let url = tree.path(of: id, root: rootURL)
                // Each clone set is its own shared-storage group.
                let set = group.cloneSet(of: id)
                requests.append(CleanupStageRequest(
                    url: url,
                    size: tree.allocatedSize[Int(id)],
                    reason: set != nil ? "Shared APFS copy" : "Duplicate copy",
                    sharesStorageGroup: set.map { "clone-" + $0.map(String.init).joined(separator: "-") },
                    groupCopyCount: set?.count ?? group.fileIDs.count
                ))
            }
        }
        guard let result = await model.confirmStageMany(requests, title: "Duplicates") else { return }
        let rejected = Set(result.rejectedURLs.map(\.standardizedFileURL.path))
        checked = Set(checked.filter { rejected.contains(tree.path(of: $0, root: rootURL).standardizedFileURL.path) })
    }

    /// What removing this copy does, given the clone sets in its group.
    static func copyNote(_ group: DuplicateGroup, _ id: Int32) -> (label: String, text: String) {
        if group.sharesStorage {
            return ("Shared storage", "These copies are APFS clones. Removing one frees nothing; the space returns only when every copy is gone.")
        }
        if let set = group.cloneSet(of: id) {
            return ("Shared storage", "This copy is an APFS clone of \(countLabel(set.count - 1, "other copy", "other copies")) here. Removing it frees nothing unless they go too.")
        }
        return ("Same contents", "Every copy in this group is byte-for-byte identical. Keep one.")
    }
}
