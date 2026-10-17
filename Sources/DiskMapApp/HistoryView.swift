import DiskMapCore
import SwiftUI

/// History & Put Back: what freedisk.space moved to the Trash, and the way back.
struct HistoryView: View {
    @ObservedObject var model: ScanModel
    @Environment(\.widthClass) private var widthClass

    var body: some View {
        VStack(alignment: .leading, spacing: widthClass.gap) {
            VStack(alignment: .leading, spacing: 6) {
                Text("History & Put Back").font(DiskMapType.pageTitle).foregroundStyle(DiskMapTheme.ink)
                Text("Every cleanup goes to the Trash first. While it’s there, Put Back returns it to the same place, with the same name.")
                    .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
            }
            if let record = model.lastCleanup, !record.items.isEmpty {
                BoardCard {
                    VStack(spacing: 0) {
                        BoardCardHeader(title: record.date.formatted(date: .abbreviated, time: .shortened)) {
                            Text("\(record.items.count) item\(record.items.count == 1 ? "" : "s") · \(ByteFormat.string(record.items.reduce(0) { $0 + $1.bytes }))")
                                .font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink2)
                            Button("Put Back all") { Task { await model.putBackLastCleanup() } }
                                .buttonStyle(AccentButtonStyle())
                        }
                        ForEach(record.items, id: \.trashedPath) { item in
                            HStack(spacing: 12) {
                                Text(URL(fileURLWithPath: item.originalPath).lastPathComponent)
                                    .font(DiskMapType.bodyEmphasis).foregroundStyle(DiskMapTheme.ink).lineLimit(1)
                                Text(CanonicalPath.displayPath(absolutePath: item.originalPath))
                                    .font(DiskMapType.figureSmall).foregroundStyle(DiskMapTheme.ink3)
                                    .lineLimit(1).truncationMode(.middle)
                                Spacer(minLength: 8)
                                Text(ByteFormat.string(item.bytes)).font(DiskMapType.figure).foregroundStyle(DiskMapTheme.ink)
                            }
                            .padding(.horizontal, 16)
                            .frame(minHeight: 40)
                            .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line.opacity(0.6)).frame(height: 1) }
                        }
                    }
                }
            } else {
                BoardCard(padding: 24) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Nothing moved yet").font(DiskMapType.heading).foregroundStyle(DiskMapTheme.ink)
                        Text("When you clean up, the items appear here so you can put them back.")
                            .font(DiskMapType.body).foregroundStyle(DiskMapTheme.ink2)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(widthClass.pagePadding)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(DiskMapTheme.page)
    }
}
