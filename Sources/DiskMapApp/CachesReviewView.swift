import AppKit
import DiskMapCore
import SwiftUI

/// Clean up → Caches: cache folders by the app that owns them.
struct CachesReviewView: View {
    @ObservedObject var model: ScanModel

    @State private var query = ""
    @State private var onlySafe = false

    private var caches: [ReviewableTarget] {
        model.cachedReviewables.filter { $0.category == .caches }.sorted { $0.bytes > $1.bytes }
    }

    static func rows(_ caches: [ReviewableTarget], totals: [Int64], rootPath: String?) -> [CleanDetailRow] {
        let items = CleanUpView.buildItems(reviewables: caches, downloads: [], media: [], totals: totals, rootPath: rootPath)
        return caches.compactMap { target in
            guard let item = items.first(where: { $0.id == "r-" + target.id }) else { return nil }
            let safe = target.isGenerallySafe
            return CleanDetailRow(
                id: target.id, nodeID: target.nodeIDs.first, name: target.displayName, path: target.primaryPath,
                place: CleanUpView.place(target.primaryPath, rootPath: rootPath),
                badge: "APP", kind: safe ? "Rebuilds" : "Look first", bytes: item.bytes,
                changed: target.paths.count == 1 ? "1 place" : "\(target.paths.count) places",
                note: .plain(target.consequence), accent: safe, requests: item.requests)
        }
    }

    var body: some View {
        let all = Self.rows(caches, totals: model.selectedTotals, rootPath: model.rootURL?.path)
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let shown = all.filter { (!onlySafe || $0.accent) && (q.isEmpty || $0.name.lowercased().contains(q) || $0.path.lowercased().contains(q)) }
        CleanDetailPage(
            model: model, title: "Caches", subtitle: "Temporary app data. Apps rebuild it, sometimes slower the first time.",
            rows: shown, totalCount: all.count, totalBytes: all.reduce(0) { $0 + $1.bytes },
            kindTitle: "Suggestion", changedTitle: "Where", noteTitle: "If you remove it",
            query: $query, searchPlaceholder: "Search caches by app or folder",
            emptyTitle: all.isEmpty ? "No caches worth clearing" : "No caches match",
            emptyMessage: all.isEmpty ? "Nothing here is big enough to bother with." : "Clear the search or show every cache.",
            bulkTitle: { count, bytes in "Add \(count) that rebuild · \(ByteFormat.hero(bytes))" },
            bulkFilter: { $0.accent }
        ) {
            ListChip(title: "Rebuild by themselves", count: all.filter(\.accent).count, isOn: onlySafe) { onlySafe.toggle() }
        } trailing: {
            EmptyView()
        }
        .catalogGate(.reviewables, model: model, title: "Finding caches…")
    }
}
