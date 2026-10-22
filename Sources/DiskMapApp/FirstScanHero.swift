import AppKit
import DiskMapCore
import SwiftUI

/// First launch and scanning (redesign boards 11 and 12): what to scan, the
/// optional Full Disk Access steps, and then a live map of what is found.
struct FirstScanHero: View {
    @ObservedObject var model: ScanModel
    var pickFolder: () -> Void
    var onScanMac: () -> Void
    @Environment(\.widthClass) private var widthClass
    @AppStorage(DustyPreference.key) private var showDusty = true
    @AppStorage("firstLaunch.skipFullDiskAccess") private var skippedAccess = false
    @State private var choice: Choice = .mac
    @State private var hasAccess = FullDiskAccess.isGranted()

    enum Choice { case mac, folder }

    var body: some View {
        Group {
            if model.isScanning { ScanningBoard(model: model) } else { firstLaunch }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(BoardGrid(tint: DiskMapTheme.ink.opacity(0.035)))
        .background(DiskMapTheme.page)
    }

    // MARK: First launch

    private var firstLaunch: some View {
        let showAccess = !hasAccess && !skippedAccess
        let stacked = widthClass == .compact && showAccess
        return ScrollView {
            VStack(alignment: .leading, spacing: 36) {
                let layout = stacked ? AnyLayout(VStackLayout(alignment: .leading, spacing: 28)) : AnyLayout(HStackLayout(alignment: .center, spacing: 48))
                layout {
                    choose.frame(maxWidth: 620, alignment: .leading)
                    if showAccess { accessCard.frame(maxWidth: 520) }
                }
                promises
            }
            .padding(.horizontal, widthClass == .compact ? 36 : 72)
            .padding(.vertical, 48)
            .frame(maxWidth: .infinity, minHeight: 600)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // Coming back from System Settings: no restart needed.
            hasAccess = FullDiskAccess.isGranted()
        }
    }

    private var choose: some View {
        VStack(alignment: .leading, spacing: 22) {
            if showDusty {
                HStack(spacing: 14) {
                    Dusty(pose: .smallHello, width: 44)
                        .frame(width: 70, height: 70)
                        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
                            .stroke(DiskMapTheme.line, style: StrokeStyle(lineWidth: 1, dash: [3, 3])))
                    Text("Hi, I'm Dusty. I'll help you find space.").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3)
                }
                .accessibilityElement(children: .combine)
            }
            VStack(alignment: .leading, spacing: 12) {
                Text("See what fills your Mac")
                    .font(.system(size: DiskMapType.scaled(widthClass == .compact ? 40 : 52), weight: .semibold))
                    .foregroundStyle(DiskMapTheme.ink)
                    .tracking(-1)
                    .fixedSize(horizontal: false, vertical: true)
                Text("One scan shows every folder drawn to its real size, and what you can safely free. It takes about a minute.")
                    .font(.system(size: DiskMapType.scaled(15))).foregroundStyle(DiskMapTheme.ink2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            VStack(spacing: 10) {
                macOption
                folderOption
            }
            HStack(spacing: 14) {
                Button(choice == .mac ? "Scan this Mac" : "Choose a folder…") {
                    if choice == .mac { onScanMac() } else { pickFolder() }
                }
                .buttonStyle(AccentButtonStyle(large: true, hard: true))
                .keyboardShortcut(.defaultAction)
                Text("↩ to start · you can stop any time").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
            }
            .padding(.top, 4)
        }
    }

    private var macOption: some View {
        let volume = VolumeStats.forPath(NSHomeDirectory())
        let used = Double(volume?.usedBytes ?? 0)
        let total = Double(max(volume?.totalBytes ?? 1, 1))
        return option(.mac) {
            IconTile(systemName: "internaldrive", size: 40)
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(volume?.volumeName ?? "This Mac").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                    if let volume { Text(ByteFormat.hero(Int64(volume.totalBytes))).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3) }
                    Text("· your home folder").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                }
                if let volume {
                    GeometryReader { geo in
                        HStack(spacing: 0) {
                            Rectangle().fill(DiskMapTheme.ink).frame(width: geo.size.width * used / total)
                            FreeStripes()
                        }
                    }
                    .frame(height: 8)
                    .clipShape(RoundedRectangle(cornerRadius: 2))
                    HStack {
                        Text("\(ByteFormat.hero(Int64(volume.usedBytes))) used".uppercased())
                        Spacer()
                        Text("\(ByteFormat.hero(Int64(volume.freeBytes))) free".uppercased())
                    }
                    .font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced))
                    .foregroundStyle(DiskMapTheme.ink3)
                }
            }
        }
    }

    private var folderOption: some View {
        option(.folder) {
            IconTile(systemName: "folder", size: 40)
            VStack(alignment: .leading, spacing: 4) {
                Text("Choose a folder…").font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                Text("Scan just one place, like Downloads or a project.").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
            }
            Spacer(minLength: 8)
            Button("Choose…", action: pickFolder).buttonStyle(SecondaryButtonStyle())
        }
    }

    private func option<Content: View>(_ value: Choice, @ViewBuilder content: () -> Content) -> some View {
        let on = choice == value
        return HStack(spacing: 16) {
            Circle()
                .strokeBorder(on ? DiskMapTheme.accent : DiskMapTheme.ink3, lineWidth: 1.5)
                .background(Circle().fill(on ? DiskMapTheme.accent : .clear).padding(5))
                .frame(width: 18, height: 18)
            content()
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(on ? DiskMapTheme.accent : DiskMapTheme.line, lineWidth: on ? 1.5 : 1))
        .contentShape(Rectangle())
        .onTapGesture { choice = value }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(on ? [.isSelected, .isButton] : .isButton)
    }

    private var accessCard: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 8) {
                Text("BEFORE YOU SCAN · OPTIONAL").font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced))
                    .foregroundStyle(DiskMapTheme.ink3)
                Text("Let freedisk.space see every folder").font(.system(size: DiskMapType.scaled(19), weight: .semibold)).foregroundStyle(DiskMapTheme.ink)
                Text("macOS hides Mail, Messages and some app data until you allow Full Disk Access. Without it, those folders are left out of the totals. You can do this later.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
            }
            VStack(spacing: 0) {
                step(1, "Open Privacy & Security settings", "System Settings › Privacy & Security")
                step(2, "Choose Full Disk Access", "scroll to it in the list")
                step(3, "Turn on freedisk.space", "then come back here, no restart needed")
            }
            HStack {
                Button("Open Privacy settings") { model.openFullDiskAccessSettings() }.buttonStyle(PrimaryButtonStyle())
                Spacer()
                Button("Skip for now") { skippedAccess = true }.buttonStyle(.plain).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3)
            }
        }
        .padding(28)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
    }

    private func step(_ n: Int, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Text("\(n)").font(.system(size: DiskMapType.scaled(12), weight: .semibold, design: .monospaced))
                .frame(width: 26, height: 26)
                .overlay(Circle().stroke(DiskMapTheme.ink, lineWidth: 1.2))
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                Text(detail).font(.system(size: DiskMapType.scaled(11.5), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 12)
        .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .accessibilityElement(children: .combine)
    }

    private var promises: some View {
        let layout = widthClass == .compact ? AnyLayout(VStackLayout(alignment: .leading, spacing: 18)) : AnyLayout(HStackLayout(alignment: .top, spacing: 32))
        return VStack(spacing: 24) {
            Rectangle().fill(DiskMapTheme.line).frame(height: 1)
            layout {
                promise("lock", "Nothing leaves your Mac", "No accounts, no analytics. Scans stay on this disk.")
                promise("arrow.uturn.backward", "Nothing is deleted without you", "Everything goes to the Trash first, so you can put it back.")
                promise("checkmark", "Free and open source", "No trial, no upgrade screen. Read every line on GitHub.")
            }
        }
    }

    private func promise(_ symbol: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            IconTile(systemName: symbol, size: 38)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink)
                Text(detail).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2).fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

/// Full Disk Access, checked without asking: only an app with it can open
/// the system privacy database. Read-only; nothing is written.
enum FullDiskAccess {
    static func isGranted() -> Bool {
        let fd = open("/Library/Application Support/com.apple.TCC/TCC.db", O_RDONLY)
        guard fd >= 0 else { return false }
        close(fd)
        return true
    }
}

// MARK: - Scanning

/// The live scan: counts, a map of the biggest top folders so far, and which one is being read.
struct ScanningBoard: View {
    @ObservedObject var model: ScanModel
    @Environment(\.widthClass) private var widthClass
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var root: URL? { model.pendingRootURL ?? model.rootURL }
    private var progress: ScanProgress? { model.liveProgress }

    var body: some View {
        VStack(alignment: .leading, spacing: widthClass.gap) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(Self.title(root: root, phase: model.scanPhase)).font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink)
                        .lineLimit(1)
                    Text(Self.subtitle(phase: model.scanPhase)).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                }
                Spacer(minLength: 8)
                Button("Cancel") { model.cancelScan() }.buttonStyle(SecondaryButtonStyle()).keyboardShortcut(.cancelAction)
            }
            stats
            HStack(alignment: .top, spacing: widthClass.gap) {
                foundSoFar
                if widthClass != .compact { places.frame(width: widthClass == .regular ? 300 : 340) }
            }
            .frame(maxHeight: .infinity)
        }
        .padding(.horizontal, widthClass.pagePadding)
        .padding(.top, widthClass == .compact ? 14 : 20)
        .padding(.bottom, widthClass.pagePadding)
    }

    private var stats: some View {
        let fraction = Self.fraction(found: progress?.bytesFound ?? 0, root: root)
        return HStack(spacing: 0) {
            stat("Files", (progress?.itemCount ?? model.scannedCount).formatted(), unit: "")
            divider
            let found = Self.splitBytes(progress?.bytesFound ?? 0)
            stat("Found", found.number, unit: found.unit)
            divider
            stat("Time", "\(Int(progress?.elapsedSeconds ?? 0))", unit: "s")
            divider
            VStack(alignment: .leading, spacing: 12) {
                Text(fraction.map { "About \(Int($0 * 100))% done" } ?? "Reading folders")
                    .font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).textCase(.uppercase)
                    .foregroundStyle(DiskMapTheme.ink3)
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        FreeStripes()
                        if let fraction {
                            Rectangle().fill(DiskMapTheme.ink).frame(width: geo.size.width * fraction)
                        } else {
                            Hatch(stripe: DiskMapTheme.ink.opacity(0.5), ground: DiskMapTheme.soft, spacing: 6, width: 2)
                        }
                    }
                }
                .frame(height: 8)
                .clipShape(RoundedRectangle(cornerRadius: 2))
                HStack(spacing: 6) {
                    Circle().fill(DiskMapTheme.accent).frame(width: 6, height: 6)
                    Text(progress.map { CanonicalPath.displayPath(absolutePath: $0.currentFolder) } ?? "Starting…")
                        .font(.system(size: DiskMapType.scaled(11.5), design: .monospaced)).foregroundStyle(DiskMapTheme.ink2)
                        .lineLimit(1).truncationMode(.middle)
                }
            }
            .padding(.horizontal, 28)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 22)
        .fixedSize(horizontal: false, vertical: true)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
    }

    private var divider: some View { Rectangle().fill(DiskMapTheme.line).frame(width: 1) }

    private func stat(_ label: String, _ value: String, unit: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(label.uppercased()).font(.system(size: DiskMapType.scaled(10.5), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(value).font(DiskMapType.hero(widthClass == .compact ? 30 : 40)).foregroundStyle(DiskMapTheme.ink)
                    .contentTransition(reduceMotion ? .identity : .numericText())
                    .lineLimit(1).minimumScaleFactor(0.6)
                Text(unit).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3)
            }
        }
        .padding(.horizontal, 28)
        .frame(maxWidth: widthClass == .compact ? 200 : 260, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    /// The top folder that holds the folder being read.
    private var readingName: String? {
        guard let progress, let root else { return nil }
        return Self.topFolder(of: progress.currentFolder, root: root.path)
    }

    private var foundSoFar: some View {
        let folders = progress?.topFolders ?? []
        return VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text("Found so far").font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
                Text("The dashed box is still being read").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                Spacer()
                Text("\(folders.count) places so far".uppercased()).font(.system(size: DiskMapType.scaled(11), weight: .medium, design: .monospaced))
                    .foregroundStyle(DiskMapTheme.ink3)
            }
            .padding(.horizontal, 20)
            .frame(height: 50)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            // No layout animation: boxes jump to their new place rather than sliding over each other.
            LiveMap(folders: folders, reading: readingName)
                .padding(16)
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
    }

    private var places: some View {
        let folders = progress?.topFolders ?? []
        return VStack(spacing: 0) {
            HStack {
                Text("Places").font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
                Spacer()
                Text(root.map { CanonicalPath.displayPath(absolutePath: $0.path) } ?? "").font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                    .lineLimit(1).truncationMode(.middle)
            }
            .padding(.horizontal, 20)
            .frame(height: 50)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(folders, id: \.name) { folder in
                        let reading = folder.name == readingName
                        HStack(spacing: 10) {
                            if reading {
                                Circle().fill(DiskMapTheme.accent).frame(width: 7, height: 7).frame(width: 14)
                            } else {
                                Image(systemName: "checkmark").font(.system(size: 10, weight: .semibold)).foregroundStyle(DiskMapTheme.accent).frame(width: 14)
                            }
                            Text(folder.name).font(reading ? DiskMapType.bodyEmphasis : DiskMapType.body).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                            if reading {
                                Text("READING").font(.system(size: DiskMapType.scaled(10), weight: .medium, design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                            }
                            Spacer(minLength: 6)
                            Text(ByteFormat.hero(folder.bytes) + (reading ? " so far" : "")).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                        }
                        .padding(.horizontal, 20)
                        .frame(height: 46)
                        .background(reading ? DiskMapTheme.accentWash : Color.clear)
                        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
                        .accessibilityElement(children: .combine)
                    }
                }
            }
            Spacer(minLength: 0)
            Text("Nothing is changed while scanning. We only read sizes.")
                .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 20).padding(.vertical, 14)
                .overlay(alignment: .top) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    // MARK: Words

    static func title(root: URL?, phase: ScanModel.ScanPhase) -> String {
        if phase == .summarizing { return "Putting it together" }
        if phase == .checkingChanges { return "Checking what changed" }
        guard let root else { return "Scanning" }
        if root.path == "/" { return "Scanning " + (VolumeStats.forPath("/")?.volumeName ?? "your Mac") }
        if CanonicalPath.displayPath(absolutePath: root.path) == "~" { return "Scanning your home folder" }
        return "Scanning " + root.lastPathComponent
    }

    static func subtitle(phase: ScanModel.ScanPhase) -> String {
        switch phase {
        case .summarizing: return "Sizing folders and preparing the first screen."
        case .checkingChanges: return "Starting from your last scan and re-reading only what macOS says changed."
        default: return "Reading every folder. Boxes fill in as sizes are found."
        }
    }

    /// Only a whole-disk scan knows its end: how much of the used space is found.
    static func fraction(found: Int64, root: URL?) -> Double? {
        guard let root, root.path == "/", let used = VolumeStats.forPath("/")?.usedBytes, used > 0 else { return nil }
        return min(0.99, Double(found) / Double(used))
    }

    /// "212" and "GB".
    static func splitBytes(_ bytes: Int64) -> (number: String, unit: String) {
        let text = ByteFormat.hero(bytes)
        guard let space = text.lastIndex(of: " ") else { return (text, "") }
        return (String(text[..<space]), String(text[text.index(after: space)...]))
    }

    /// "/Users/me/Library/Caches/x" under "/Users/me" → "Library".
    static func topFolder(of path: String, root: String) -> String? {
        let base = root.hasSuffix("/") ? root : root + "/"
        guard path.hasPrefix(base) else { return nil }
        return path.dropFirst(base.count).split(separator: "/").first.map(String.init)
    }
}

/// The scan's biggest top folders as outlined boxes; the one being read is dashed.
private struct LiveMap: View {
    var folders: [ScanProgress.Folder]
    var reading: String?

    var body: some View {
        GeometryReader { geo in
            let items = folders.enumerated().map { (id: Int32($0.offset), size: max(1, $0.element.bytes)) }
            let rects = SquarifiedTreemap.layout(items: items, in: CGRect(origin: .zero, size: geo.size))
            ZStack(alignment: .topLeading) {
                if folders.isEmpty {
                    Hatch(stripe: DiskMapTheme.line, ground: DiskMapTheme.card, spacing: 10, width: 1)
                        .overlay(Text("Looking for the first folders…").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink3))
                }
                ForEach(rects.map { (rect: $0, folder: folders[Int($0.id)]) }, id: \.folder.name) { item in
                    let r = item.rect
                    let folder = item.folder
                    let isReading = folder.name == reading
                    ZStack(alignment: .topLeading) {
                        if isReading {
                            Hatch(stripe: DiskMapTheme.accent.opacity(0.35), ground: DiskMapTheme.accentWash, spacing: 8, width: 1.5)
                            Rectangle().strokeBorder(DiskMapTheme.accent, style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                        } else {
                            Rectangle().fill(DiskMapTheme.card)
                            Rectangle().strokeBorder(DiskMapTheme.ink.opacity(0.85), lineWidth: 1)
                        }
                        if r.rect.width > 60, r.rect.height > 26 {
                            HStack(spacing: 6) {
                                Text(folder.name).font(.system(size: DiskMapType.scaled(12.5), weight: .semibold))
                                    .foregroundStyle(isReading ? DiskMapTheme.accentDeep : DiskMapTheme.ink)
                                Text(isReading ? "reading…" : DevChip.short(folder.bytes))
                                    .font(.system(size: DiskMapType.scaled(11), design: .monospaced)).foregroundStyle(DiskMapTheme.ink3)
                            }
                            .lineLimit(1)
                            .padding(8)
                            .frame(maxWidth: r.rect.width, alignment: .leading)
                        }
                    }
                    .frame(width: max(0, r.rect.width - 4), height: max(0, r.rect.height - 4))
                    .offset(x: r.rect.minX + 2, y: r.rect.minY + 2)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("\(folder.name), \(ByteFormat.string(folder.bytes))\(isReading ? ", being read" : "")")
                }
            }
        }
    }
}
