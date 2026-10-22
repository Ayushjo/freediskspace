import AppKit
import DiskMapCore
import SwiftUI

/// Developer › Xcode (P3): device support, simulators, previews, runtimes,
/// archives and the Swift package cache, measured where they live. One row
/// each; what to do folds open. Only what Xcode rebuilds by itself can reach
/// Cleanup — the rest is a command to copy or a place in Xcode.
struct XcodeStorageList: View {
    @ObservedObject var model: ScanModel

    var body: some View {
        if let items = model.xcodeStorage {
            if items.isEmpty {
                DiskMapEmptyState(symbol: "hammer", title: "No Xcode storage on this Mac",
                                  message: "Device support, simulators and previews show up here once Xcode creates them.")
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        summary(items)
                        VStack(alignment: .leading, spacing: 12) {
                            ForEach(items) { item in row(item) }
                        }
                        .padding(16)
                        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
                    }
                }
                .accessibilityIdentifier("xcode-storage")
            }
        } else {
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Measuring Xcode storage…").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
            }
            .padding(.top, 24)
        }
    }

    private func summary(_ items: [SystemStorageCatalog.Item]) -> some View {
        let total = items.reduce(Int64(0)) { $0 + $1.bytes }
        let freeable = items.filter { $0.entry.action == .trash }.reduce(Int64(0)) { $0 + $1.suggestedBytes }
        return HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(ByteFormat.string(total)).font(DiskMapType.figureStrong).foregroundStyle(DiskMapTheme.ink)
            Text("in Xcode and simulators").font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
            if freeable > 0 {
                Text("·").foregroundStyle(DiskMapTheme.ink3)
                Text("\(ByteFormat.string(freeable)) rebuilds by itself").font(DiskMapType.secondary)
                    .foregroundStyle(DiskMapTheme.accent)
            }
        }
    }

    private func row(_ item: SystemStorageCatalog.Item) -> some View {
        FoldingRow(title: item.entry.title, startsOpen: SnapshotHarness.restState == "xcode-open" && item.entry.perChild) {
            Image(systemName: SystemStorageList.symbol(item.entry.action))
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(item.entry.action == .trash ? DiskMapTheme.accent : DiskMapTheme.ink3)
                .frame(width: 12)
                .help(item.entry.action == .trash ? "Xcode rebuilds this; it can go to Cleanup." : "Removed with a command or in Xcode.")
        } trailing: {
            Text(ByteFormat.string(item.bytes) + (item.isPartial ? "+" : ""))
                .font(DiskMapType.figureStrong).foregroundStyle(DiskMapTheme.ink)
        } detail: {
            Text(item.entry.why)
                .font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink2)
                .fixedSize(horizontal: false, vertical: true)
            if item.entry.perChild { versions(item) }
            ForEach(item.entry.steps, id: \.self) { step in
                Text(step).font(DiskMapType.secondary).foregroundStyle(DiskMapTheme.ink)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let command = item.entry.command { commandBox(command) }
            actions(item)
        }
        .accessibilityIdentifier("xcode-row-\(item.id)")
    }

    /// One line per version: name, size, and which one stays.
    private func versions(_ item: SystemStorageCatalog.Item) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(item.found.sorted { $0.bytes > $1.bytes }, id: \.path) { found in
                HStack(spacing: 8) {
                    Text((found.path as NSString).lastPathComponent)
                        .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                    if item.keep.contains(found.path) {
                        Text("KEEP").font(DiskMapType.label).foregroundStyle(DiskMapTheme.ink3)
                            .help("The newest version for this platform stays.")
                    }
                    Spacer()
                    Text(ByteFormat.string(found.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink2)
                }
            }
        }
        .padding(.vertical, 2)
    }

    private func commandBox(_ command: String) -> some View {
        HStack(spacing: 8) {
            Text(command)
                .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                .textSelection(.enabled)
            Spacer()
            Button("Copy") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(command, forType: .string)
                model.showToast("Copied — run it in Terminal")
            }
            .buttonStyle(LinkButtonStyle())
            .font(DiskMapType.secondary)
            .accessibilityIdentifier("xcode-copy-command")
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.raised))
    }

    @ViewBuilder
    private func actions(_ item: SystemStorageCatalog.Item) -> some View {
        if item.entry.action == .trash {
            let requests = Self.stageRequests(item)
            if !requests.isEmpty {
                Button(Self.stageTitle(item, count: requests.count)) {
                    Task { _ = await model.confirmStageMany(requests, title: item.entry.title) }
                }
                .buttonStyle(LinkButtonStyle())
                .font(DiskMapType.secondary)
                .accessibilityIdentifier("xcode-stage-\(item.id)")
            }
        }
    }

    /// What "Add to Cleanup" offers: only `trash` entries, never a kept
    /// version, never a guide-only path.
    static func stageRequests(_ item: SystemStorageCatalog.Item) -> [CleanupStageRequest] {
        guard item.entry.action == .trash else { return [] }
        return item.found.filter { !item.keep.contains($0.path) }.map {
            CleanupStageRequest(url: URL(fileURLWithPath: $0.path), size: $0.bytes, reason: item.entry.title)
        }
    }

    static func stageTitle(_ item: SystemStorageCatalog.Item, count: Int) -> String {
        let size = ByteFormat.string(item.suggestedBytes)
        if item.entry.perChild {
            return count == 1 ? "Add the older version to Cleanup · \(size)" : "Add \(count) older versions to Cleanup · \(size)"
        }
        return "Add to Cleanup · \(size)"
    }
}
