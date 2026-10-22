import AppKit
import DiskMapCore
import SwiftUI

/// Clean → Large Media: media-storage intelligence workspace (not Biggest Files).
struct LargeMediaView: View {
    @ObservedObject var model: ScanModel
    var pickFolder: () -> Void

    @State private var query = ""
    @State private var typeFilter: MediaTypeFilter = .all
    @State private var sizeFilter: MediaSizeFilter = .any
    @State private var ageFilter: MediaAgeFilter = .all
    @State private var locationFilter: MediaLocationFilter = .any
    @State private var sort: MediaSort = .largest
    @Environment(\.widthClass) private var widthClass

    private var catalog: MediaCatalogResult { model.cachedLargeMedia }

    static func row(_ c: MediaCandidate, rootPath: String?) -> CleanDetailRow {
        CleanDetailRow(
            id: String(c.nodeID), nodeID: c.nodeID, name: c.name, path: c.absolutePath,
            place: CleanUpView.place(c.absolutePath, rootPath: rootPath),
            badge: c.isDirectory ? "DIR" : BiggestFilesView.badge(c.name, kind: .other),
            kind: c.kind == .project ? "Media project" : c.kind == .image ? "Image" : c.kind.title,
            bytes: c.bytes, changed: VisualizeView.lastUsed(c.modifiedDay, today: AgeMap.today()),
            note: .plain(c.recommendation.isEmpty ? c.whyHere : c.recommendation),
            accent: c.status == .likelyDisposable,
            requests: c.safety.level == .protected ? []
                : [CleanupStageRequest(url: URL(fileURLWithPath: c.absolutePath), size: c.bytes, reason: "Large Media: \(c.name)")])
    }

    var body: some View {
        Group {
            if model.tree == nil {
                DiskMapEmptyState(symbol: "film", title: "Scan to find large media",
                                  message: "freedisk.space finds big videos, photos and audio after a scan.",
                                  primaryTitle: "Choose Folder…", primaryAction: pickFolder)
            } else {
                page
            }
        }
        .catalogGate(.largeMedia, model: model, title: "Finding large media…")
    }

    private var page: some View {
        let root = model.rootURL?.path
        let filtered = MediaCatalog.sorted(MediaCatalog.filter(catalog.candidates, type: typeFilter, size: sizeFilter, age: ageFilter,
                                                               location: locationFilter, query: query), by: sort)
        let total = catalog.candidates.reduce(Int64(0)) { $0 + $1.bytes }
        return CleanDetailPage(
            model: model, title: "Large media", subtitle: "The videos, photos, audio and media projects using the most space.",
            rows: filtered.map { Self.row($0, rootPath: root) },
            totalCount: catalog.candidates.count, totalBytes: total,
            kindTitle: "Kind", changedTitle: "Last changed", noteTitle: "Before you remove it",
            query: $query, searchPlaceholder: "Search media by name or folder",
            emptyTitle: catalog.candidates.isEmpty ? "No large media found" : "No media matches these filters",
            emptyMessage: catalog.candidates.isEmpty ? "Nothing here is big enough to bother with." : "Try another kind, place or size.",
            bulkTitle: { count, bytes in "Add \(count) likely done with · \(ByteFormat.hero(bytes))" },
            bulkFilter: { $0.accent }
        ) {
            ForEach(MediaTypeFilter.allCases.prefix(widthClass == .compact ? 3 : 6)) { type in
                let count = MediaCatalog.filter(catalog.candidates, type: type, size: sizeFilter, age: ageFilter, location: locationFilter, query: "").count
                if type == .all || count > 0 {
                    ListChip(title: type.title, count: count, isOn: typeFilter == type) { typeFilter = type }
                }
            }
            Spacer(minLength: 8)
            ListDropdown(label: "Where", options: MediaLocationFilter.allCases, selection: $locationFilter, title: \.title)
        } trailing: {
            if widthClass != .compact {
                ListDropdown(label: "Older than", options: MediaAgeFilter.allCases, selection: $ageFilter, title: \.title)
                ListDropdown(label: "Size", options: MediaSizeFilter.allCases, selection: $sizeFilter, title: \.title)
            }
            ListDropdown(label: "Sort", options: MediaSort.allCases, selection: $sort, title: \.title)
        }
    }
}
