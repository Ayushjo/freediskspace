import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// What changed (redesign board 08): the headline words, the chart's axis and the lists.
@Suite("What changed")
struct WhatChangedTests {
    private let now = Date(timeIntervalSince1970: 1_792_000_000)

    @Test func sinceWords() {
        let day: TimeInterval = 86_400
        #expect(SnapshotsView.sinceText(now.addingTimeInterval(-3_600), now: now) == "since earlier today")
        #expect(SnapshotsView.sinceText(now.addingTimeInterval(-day), now: now) == "since yesterday")
        #expect(SnapshotsView.sinceText(now.addingTimeInterval(-7 * day), now: now) == "since last week")
        #expect(SnapshotsView.sinceText(now.addingTimeInterval(-40 * day), now: now).hasPrefix("since "))
        let weekday = SnapshotsView.sinceText(now.addingTimeInterval(-3 * day), now: now)
        #expect(["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"].contains { weekday == "since " + $0 })
    }

    @Test func signedAndBeforeAfter() {
        #expect(SnapshotsView.signed(18_600_000_000) == "+18.6 GB")
        #expect(SnapshotsView.signed(-4_300_000_000) == "−4.30 GB")
        #expect(SnapshotsView.signed(0) == "±0")
        #expect(SnapshotsView.beforeAfter(22_800_000_000, 31_200_000_000) == "22.8 → 31.2 GB")
        #expect(SnapshotsView.beforeAfter(900_000_000, 1_200_000_000) == "0.9 → 1.2 GB", "both in the bigger unit")
    }

    @Test func axisHoldsEveryBarWithRoundSteps() {
        let values: [Double] = [301, 305, 312, 318, 310, 322, 327, 346]
        let axis = UsageChart.axis(values)
        #expect(axis.low <= 301 && axis.high >= 346)
        #expect(axis.low >= 0)
        let steps = (axis.high - axis.low) / axis.step
        #expect(abs(steps - steps.rounded()) < 1e-9, "grid lines land on the ends")
        #expect([1.0, 2, 5, 10, 20, 50].contains(axis.step))
        let flat = UsageChart.axis([50, 50])
        #expect(flat.high > flat.low, "one value still draws a range")
    }

    @Test func chartFigures() {
        #expect(UsageChart.figure(346.2) == "346")
        #expect(UsageChart.figure(18.64) == "18.6")
        let small = [UsageChart.Point(id: "a", label: "A", bytes: 500_000_000, role: .past, note: nil)]
        #expect(UsageChart.unit(small).name == "MB")
    }

    @Test func splitSortsBiggestChangeFirst() {
        let entries = [SnapshotComparison.Entry(path: "a", name: "a", beforeID: 1, afterID: 1, before: 10, after: 15, isDirectory: true),
                       SnapshotComparison.Entry(path: "b", name: "b", beforeID: 1, afterID: 1, before: 10, after: 40, isDirectory: true),
                       SnapshotComparison.Entry(path: "c", name: "c", beforeID: 1, afterID: 1, before: 10, after: 2, isDirectory: true),
                       SnapshotComparison.Entry(path: "d", name: "d", beforeID: 1, afterID: 1, before: 10, after: 9, isDirectory: true),
                       SnapshotComparison.Entry(path: "e", name: "e", beforeID: 1, afterID: 1, before: 10, after: 10, isDirectory: true)]
        let (grew, shrank) = SnapshotsView.split(entries)
        #expect(grew.map(\.path) == ["b", "a"])
        #expect(shrank.map(\.path) == ["c", "d"])
    }
}
