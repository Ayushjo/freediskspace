import SwiftUI

/// Map | Rings | List, with the other views one click further. Shared by the
/// Explore canvas and its list pages so moving between them feels like one page.
struct ExploreViewSwitch: View {
    @ObservedObject var model: ScanModel

    static let listDestinations: Set<AppDestination> = [.biggestFiles, .biggestFolders, .forgottenFiles, .find]
    private static let moreModes: [ExploreViewMode] = [.flame, .bubbles, .mindMap, .ageMap]

    private var onList: Bool { Self.listDestinations.contains(model.destination) }

    var body: some View {
        HStack(spacing: 8) {
            SegmentSwitch(items: [
                .init(title: "Map", isOn: !onList && model.exploreMode == .treemap) { show(.treemap) },
                .init(title: "Rings", isOn: !onList && model.exploreMode == .sunburst) { show(.sunburst) },
                .init(title: "List", isOn: onList) { if !onList { model.destination = .biggestFiles } },
            ])
            Menu {
                ForEach(Self.moreModes) { mode in
                    Button { show(mode) } label: {
                        if !onList && model.exploreMode == mode { Label(mode.rawValue, systemImage: "checkmark") } else { Text(mode.rawValue) }
                    }
                }
            } label: {
                Text(!onList && Self.moreModes.contains(model.exploreMode) ? model.exploreMode.rawValue : "More")
                    .font(DiskMapType.bodyEmphasis)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Flame, Bubbles, Mind Map and Age Map")
        }
    }

    private func show(_ mode: ExploreViewMode) {
        model.exploreMode = mode
        if model.destination != .visualize { model.destination = .visualize }
    }
}
