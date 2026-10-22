import AppKit
import DiskMapCore
import ServiceManagement
import SwiftUI

/// freedisk.space ▸ Settings… (⌘,), redesign board 14. Only real choices:
/// every switch here changes what the app does; nothing is a placeholder.
struct SettingsView: View {
    @AppStorage("ShowMenuBarExtra") private var showMenuBarExtra = true
    @AppStorage(CloneAccounting.key) private var cloneMode = CloneAccounting.defaultMode.rawValue
    @AppStorage(ScanModel.keepHistoryKey) private var keepHistory = true
    @AppStorage(TextSize.storageKey) private var textSize = TextSize.standard.rawValue
    @AppStorage(DustyPreference.key) private var showDusty = true
    @AppStorage(AppAppearance.storageKey) private var appearance = AppAppearance.system.rawValue
    @AppStorage(CleanUpView.tickKey) private var tickRebuilds = true
    @AppStorage(DuplicatesView.KeepPolicy.key) private var keep: DuplicatesView.KeepPolicy = .oldest
    @AppStorage(Updates.automaticKey) private var automaticUpdates = Updates.automaticDefault
    // Read when the page appears, never at launch: the Settings scene is built then.
    @State private var openAtLogin = false
    @State private var loginError: String?
    @State private var hasAccess = FullDiskAccess.isGranted()
    @State private var confirmReset = false
    @State private var section: Section = .general

    enum Section: String, CaseIterable, Identifiable {
        case general, scanning, cleanUp, updates, appearance, savedScans, privacy
        var id: String { rawValue }
        var title: String {
            switch self {
            case .general: return "General"
            case .scanning: return "Scanning"
            case .cleanUp: return "Clean up"
            case .updates: return "Updates"
            case .appearance: return "Appearance"
            case .savedScans: return "Saved scans"
            case .privacy: return "Privacy"
            }
        }
        var symbol: String {
            switch self {
            case .general: return "gearshape"
            case .scanning: return "magnifyingglass"
            case .cleanUp: return "trash"
            case .updates: return "checkmark"
            case .appearance: return "circle.lefthalf.filled"
            case .savedScans: return "clock"
            case .privacy: return "lock"
            }
        }
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Rectangle().fill(DiskMapTheme.line).frame(width: 1)
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Settings").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink)
                            Text("Changes save as you make them.").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                        }
                        HStack(alignment: .top, spacing: 22) {
                            VStack(alignment: .leading, spacing: 22) {
                                general.id(Section.general)
                                scanning.id(Section.scanning)
                                savedScans.id(Section.savedScans)
                            }
                            VStack(alignment: .leading, spacing: 22) {
                                cleanUp.id(Section.cleanUp)
                                updates.id(Section.updates)
                                appearanceGroup.id(Section.appearance)
                                privacy.id(Section.privacy)
                            }
                        }
                    }
                    .padding(28)
                }
                .onChange(of: section) { _, value in withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(value, anchor: .top) } }
            }
        }
        .frame(minWidth: 1_000, idealWidth: 1_080, minHeight: 640, idealHeight: 780)
        .background(DiskMapTheme.page)
        .onAppear { openAtLogin = LoginItem.isEnabled }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            hasAccess = FullDiskAccess.isGranted()
            openAtLogin = LoginItem.isEnabled
        }
        .confirmationDialog("Reset all settings?", isPresented: $confirmReset, titleVisibility: .visible) {
            Button("Reset settings", role: .destructive) { resetSettings() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Every choice on this page goes back to how it started. Saved scans and History are kept.")
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(Section.allCases) { item in
                let on = section == item
                Button { section = item } label: {
                    HStack(spacing: 10) {
                        Image(systemName: item.symbol).font(.system(size: 13)).frame(width: 18)
                        Text(item.title).font(DiskMapType.body)
                        Spacer()
                    }
                    .foregroundStyle(on ? DiskMapTheme.ink : DiskMapTheme.ink2)
                    .padding(.horizontal, 10)
                    .frame(height: 32)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(on ? DiskMapTheme.soft : .clear))
                    .overlay(alignment: .leading) { if on { Rectangle().fill(DiskMapTheme.accent).frame(width: 2) } }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
            Spacer()
            HStack {
                Text("freedisk.space").font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                Spacer()
                Text(Self.version).font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
            }
            .padding(.horizontal, 10)
        }
        .padding(12)
        .frame(width: 210)
        .background(DiskMapTheme.sidebar)
    }

    static var version: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev" }

    // MARK: Groups

    private var general: some View {
        group("General") {
            row("Open at login", openAtLogin ? "Starts quietly so the menu bar status is there." : "Off. Open freedisk.space yourself when you need it.",
                error: loginError) {
                Toggle("", isOn: Binding(get: { openAtLogin }, set: { setOpenAtLogin($0) })).labelsHidden().toggleStyle(.switch).tint(DiskMapTheme.accent)
            }
            row("Show free space in the menu bar", "The free space and the last scan, one click away.") {
                Toggle("", isOn: $showMenuBarExtra).labelsHidden().toggleStyle(.switch).tint(DiskMapTheme.accent)
            }
        }
    }

    private var scanning: some View {
        group("Scanning") {
            row("APFS clones", CloneAccounting.explanation(SharingMode(rawValue: cloneMode) ?? .off), stacked: true) {
                Picker("", selection: $cloneMode) {
                    Text("Count every copy").tag(SharingMode.off.rawValue)
                    Text("Count each family once").tag(SharingMode.refcount.rawValue)
                    Text("Also find edited clones").tag(SharingMode.full.rawValue)
                }
                .labelsHidden()
                .fixedSize()
            }
            row("Keep storage history", "A few hundred folder sizes per scan, so Overview can say what grew. Kept a year on this Mac, never sent anywhere.") {
                Toggle("", isOn: $keepHistory).labelsHidden().toggleStyle(.switch).tint(DiskMapTheme.accent)
            }
        }
    }

    private var savedScans: some View {
        group("Saved scans") {
            row("Stored on this Mac", CanonicalPath.displayPath(absolutePath: SnapshotStore.defaultDirectory().deletingLastPathComponent().path), mono: true) {
                Button("Show in Finder") {
                    let dir = SnapshotStore.defaultDirectory().deletingLastPathComponent()
                    NSWorkspace.shared.activateFileViewerSelecting([dir])
                }
                .buttonStyle(SecondaryButtonStyle())
            }
        }
    }

    private var cleanUp: some View {
        group("Clean up") {
            VStack(alignment: .leading, spacing: 8) {
                Text("What to tick for you").font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                choice(on: tickRebuilds, "Only things that rebuild by themselves", "Caches and build files. Recommended.") { tickRebuilds = true }
                choice(on: !tickRebuilds, "Nothing, I'll choose", "Every box starts empty.") { tickRebuilds = false }
            }
            .padding(16)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            row("For duplicates, keep the", "The other copies are ticked for the Trash.", stacked: true) {
                SegmentSwitch(items: DuplicatesView.KeepPolicy.allCases.map { p in .init(title: p.title, isOn: keep == p) { keep = p } })
            }
        }
    }

    private var updates: some View {
        group("Updates") {
            row("Check for updates automatically",
                Updates.shared.isConfigured ? "Off by default. You can also choose Check for Updates… in the menu." : "This build has no update feed, so it never contacts a server.") {
                Toggle("", isOn: Binding(get: { automaticUpdates }, set: { automaticUpdates = $0; Updates.shared.setAutomaticChecks($0) }))
                    .labelsHidden().toggleStyle(.switch).tint(DiskMapTheme.accent)
                    .disabled(!Updates.shared.isConfigured)
            }
            HStack(spacing: 8) {
                Circle().fill(DiskMapTheme.accent).frame(width: 6, height: 6)
                Text("Checking for updates is the only time freedisk.space uses the internet. Scans and cleanups never do.")
                    .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.accentDeep)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.accent.opacity(0.4), style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
            .padding(12)
        }
    }

    private var appearanceGroup: some View {
        group("Appearance") {
            row("Theme", nil) {
                SegmentSwitch(items: [AppAppearance.light, .dark, .system].map { a in
                    .init(title: a == .system ? "System" : a.title, isOn: appearance == a.rawValue) { setAppearance(a) }
                })
            }
            HStack(spacing: 14) {
                ForEach([AppAppearance.light, .dark, .system], id: \.self) { a in
                    Button { setAppearance(a) } label: {
                        VStack(spacing: 6) {
                            ThemeSwatch(kind: a).frame(width: 72, height: 46)
                                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
                                    .stroke(appearance == a.rawValue ? DiskMapTheme.accent : DiskMapTheme.line, lineWidth: appearance == a.rawValue ? 2 : 1))
                            Text(a == .system ? "System" : a.title).font(DiskMapType.secondary)
                                .foregroundStyle(appearance == a.rawValue ? DiskMapTheme.ink : DiskMapTheme.ink2)
                        }
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(a.title) theme")
                    .accessibilityAddTraits(appearance == a.rawValue ? .isSelected : [])
                }
                Text("System follows your Mac, including at night.").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(16)
            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
            row("Text size", nil) {
                Picker("", selection: Binding(
                    get: { TextSize(rawValue: textSize) ?? .standard },
                    set: { DiskMapType.scale = $0.scale; textSize = $0.rawValue }
                )) { ForEach(TextSize.allCases) { Text($0.title).tag($0) } }
                    .labelsHidden().fixedSize()
            }
            row("Show Dusty", "The mascot, in empty states and after a cleanup. Never on a step that removes files.") {
                Toggle("", isOn: $showDusty).labelsHidden().toggleStyle(.switch).tint(DiskMapTheme.accent)
            }
        }
    }

    private var privacy: some View {
        group("Privacy") {
            row("Full Disk Access", hasAccess ? "On. freedisk.space can read every folder." : "Off. Mail, Messages and some app data are left out of scans.") {
                if hasAccess {
                    BoardTag(text: "Allowed", tone: .accent)
                } else {
                    Button("Allow…") { ScanModel.shared.openFullDiskAccessSettings() }.buttonStyle(SecondaryButtonStyle())
                }
            }
            row("Analytics and crash reports", "None. There is nothing to turn on.") { BoardTag(text: "Never") }
            row("Reset all settings", "Saved scans and History are kept.") {
                Button("Reset…") { confirmReset = true }.buttonStyle(SecondaryButtonStyle())
            }
        }
    }

    // MARK: Pieces

    private func group<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink).padding(.leading, 4)
            VStack(spacing: 0) { content() }
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
        .frame(maxWidth: .infinity)
    }

    private func row<Control: View>(_ title: String, _ detail: String?, mono: Bool = false, error: String? = nil, stacked: Bool = false,
                                    @ViewBuilder control: () -> Control) -> some View {
        let layout = stacked ? AnyLayout(VStackLayout(alignment: .leading, spacing: 12)) : AnyLayout(HStackLayout(alignment: .center, spacing: 16))
        return layout {
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                if let detail {
                    Text(detail).font(mono ? DiskMapType.figureSmall : DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let error { Text(error).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.review) }
            }
            if !stacked { Spacer(minLength: 8) }
            control()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(minHeight: 58)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
        .accessibilityElement(children: .combine)
    }

    private func choice(on: Bool, _ title: String, _ detail: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 12) {
                Circle()
                    .strokeBorder(on ? DiskMapTheme.accent : DiskMapTheme.ink3, lineWidth: 1.5)
                    .background(Circle().fill(on ? DiskMapTheme.accent : .clear).padding(4))
                    .frame(width: 16, height: 16)
                    .padding(.top, 2)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink)
                    Text(detail).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink3)
                }
                Spacer()
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(on ? DiskMapTheme.accentWash : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    // MARK: Actions

    private func setAppearance(_ value: AppAppearance) {
        appearance = value.rawValue
        value.apply()
    }

    private func setOpenAtLogin(_ on: Bool) {
        do {
            try LoginItem.set(on)
            loginError = nil
        } catch {
            loginError = "macOS didn't allow that: \(error.localizedDescription)"
        }
        openAtLogin = LoginItem.isEnabled
    }

    /// Every key this page writes, back to its default. Saved scans, History and caches stay.
    static let resettableKeys = ["ShowMenuBarExtra", CloneAccounting.key, ScanModel.keepHistoryKey, TextSize.storageKey,
                                 DustyPreference.key, AppAppearance.storageKey, CleanUpView.tickKey,
                                 DuplicatesView.KeepPolicy.key, "firstLaunch.skipFullDiskAccess"]

    private func resetSettings() {
        for key in Self.resettableKeys { UserDefaults.standard.removeObject(forKey: key) }
        DiskMapType.scale = TextSize.standard.scale
        AppAppearance.system.apply()
        if automaticUpdates { automaticUpdates = false; Updates.shared.setAutomaticChecks(false) }
    }
}

/// A tiny picture of a theme for the Appearance choice.
private struct ThemeSwatch: View {
    var kind: AppAppearance
    var body: some View {
        GeometryReader { geo in
            HStack(spacing: 0) {
                switch kind {
                case .light: Color(white: 0.98)
                case .dark: Color(white: 0.08)
                case .system:
                    Color(white: 0.98).frame(width: geo.size.width / 2)
                    Color(white: 0.08)
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .accessibilityHidden(true)
    }
}

/// Open at login, through the system's login items. Nothing is copied or installed.
@MainActor
enum LoginItem {
    /// Only the installed app can be a login item; a bare build never asks the system.
    static var isAvailable: Bool {
        Bundle.main.bundleURL.pathExtension == "app" && !SnapshotHarness.isActive
    }

    static var isEnabled: Bool { isAvailable && SMAppService.mainApp.status == .enabled }

    struct Unavailable: LocalizedError {
        var errorDescription: String? { "only the installed freedisk.space app can open at login." }
    }

    static func set(_ on: Bool) throws {
        guard isAvailable else { throw Unavailable() }
        if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
    }
}

/// The clone-accounting choice (TASK-077), stored in user defaults.
enum CloneAccounting {
    static let key = "CloneAccounting"
    /// Off: reading clone facts made a home walk +14% (median) / +22% (p95)
    /// slower for refcount and ~1.9× for full (docs/perf-results/clone-scan-ab.txt)
    /// — over the 10% budget for a default.
    static let defaultMode: SharingMode = .off

    static var mode: SharingMode {
        if SnapshotHarness.isDeterministic { return defaultMode }
        return UserDefaults.standard.string(forKey: key).flatMap(SharingMode.init(rawValue:)) ?? defaultMode
    }

    static func explanation(_ mode: SharingMode) -> String {
        switch mode {
        case .off:
            return "Fastest, but a cloned copy (Finder, pnpm) is counted again for every copy, so totals can exceed what the disk uses."
        case .refcount:
            return "Clones count once so sizes match the disk; scans take about 15–20% longer, and the next one reads every folder."
        case .full:
            return "Also finds edited clones and lets Cleanup measure unchanged folders instantly; scans take nearly twice as long."
        }
    }
}
