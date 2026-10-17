import AppKit
import DiskMapCore
import SwiftUI

/// Clean up → Old downloads: large or older files in Downloads.
struct OldDownloadsView: View {
    @ObservedObject var model: ScanModel
    var pickFolder: () -> Void

    @State private var query = ""
    @State private var ageFilter: OldDownloadsAgeFilter = .days30
    @State private var sizeFilter: OldDownloadsSizeFilter = .any
    @State private var typeFilter: OldDownloadsTypeFilter = .all
    @State private var sort: OldDownloadsSort = .largest
    @Environment(\.widthClass) private var widthClass

    private var catalog: OldDownloadsCatalogResult { model.cachedOldDownloads }

    static func row(_ c: OldDownloadsCandidate, rootPath: String?) -> CleanDetailRow {
        CleanDetailRow(
            id: String(c.nodeID), nodeID: c.nodeID, name: c.name, path: c.absolutePath,
            place: CleanUpView.place(c.absolutePath, rootPath: rootPath),
            badge: BiggestFilesView.badge(c.name, kind: c.kind),
            kind: c.status == .likelyDisposable ? "Likely done with" : "Look first",
            bytes: c.bytes, changed: VisualizeView.lastUsed(c.modifiedDay, today: AgeMap.today()),
            note: .plain(c.whyHere), accent: c.status == .likelyDisposable,
            requests: c.safety.level == .protected ? []
                : [CleanupStageRequest(url: URL(fileURLWithPath: c.absolutePath), size: c.bytes, reason: "Old Downloads: \(c.name)")])
    }

    var body: some View {
        Group {
            if model.tree == nil {
                DiskMapEmptyState(symbol: "arrow.down.circle", title: "Scan to find older downloads",
                                  message: "freedisk.space looks through Downloads after a scan.",
                                  primaryTitle: "Choose Folder…", primaryAction: pickFolder)
            } else {
                page
            }
        }
        .catalogGate(.oldDownloads, model: model, title: "Looking through Downloads…")
    }

    private var page: some View {
        let root = model.rootURL?.path
        let filtered = OldDownloadsCatalog.sorted(
            OldDownloadsCatalog.filter(catalog.candidates, age: ageFilter, size: sizeFilter, type: typeFilter, query: query), by: sort)
        return CleanDetailPage(
            model: model, title: "Old downloads", subtitle: "Large or older files in Downloads. Age is the last-modified date.",
            rows: filtered.map { Self.row($0, rootPath: root) },
            totalCount: catalog.summary.totalCount, totalBytes: catalog.summary.totalBytes,
            kindTitle: "Suggestion", changedTitle: "Last changed", noteTitle: "Why it's here",
            query: $query, searchPlaceholder: "Search Downloads",
            emptyTitle: catalog.candidates.isEmpty ? "Nothing old enough to review" : "No files match these filters",
            emptyMessage: catalog.candidates.isEmpty ? "Downloads has nothing matching this view. That’s a good thing." : "Try another age, size or type.",
            bulkTitle: { count, bytes in "Add \(count) shown · \(ByteFormat.hero(bytes))" },
            bulkFilter: { _ in true }
        ) {
            ForEach(OldDownloadsTypeFilter.allCases.prefix(widthClass == .compact ? 3 : 6)) { type in
                let count = OldDownloadsCatalog.filter(catalog.candidates, age: ageFilter, size: sizeFilter, type: type, query: "").count
                if type == .all || count > 0 {
                    ListChip(title: type.title, count: count, isOn: typeFilter == type) { typeFilter = type }
                }
            }
            Spacer(minLength: 8)
            ListDropdown(label: "Older than", options: OldDownloadsAgeFilter.allCases, selection: $ageFilter, title: \.title)
        } trailing: {
            if widthClass != .compact {
                ListDropdown(label: "Size", options: OldDownloadsSizeFilter.allCases, selection: $sizeFilter, title: \.title)
            }
            ListDropdown(label: "Sort", options: OldDownloadsSort.allCases, selection: $sort, title: \.title)
            Button("Open Downloads") {
                NSWorkspace.shared.open(URL(fileURLWithPath: NSHomeDirectory() + "/Downloads", isDirectory: true))
            }
            .buttonStyle(SecondaryButtonStyle())
        }
    }
}
