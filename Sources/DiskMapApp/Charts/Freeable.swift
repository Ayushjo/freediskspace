import DiskMapCore
import SwiftUI

/// Which nodes "can be freed": the generally safe Clean up items and
/// everything inside them. Shared by the map, its table and the inspector so
/// all three agree with Clean up.
struct FreeableIndex: Sendable, Equatable {
    /// Safe target node → its size.
    let targets: [Int32: Int64]

    static let empty = FreeableIndex(targets: [:])

    init(targets: [Int32: Int64]) { self.targets = targets }

    init(reviewables: [ReviewableTarget], totals: [Int64]) {
        var t: [Int32: Int64] = [:]
        for r in reviewables where r.isGenerallySafe {
            for id in r.nodeIDs where id >= 0 && Int(id) < totals.count { t[id] = totals[Int(id)] }
        }
        targets = t
    }

    var isEmpty: Bool { targets.isEmpty }

    /// The node is a safe target or inside one.
    func contains(_ id: Int32, tree: FileTree) -> Bool {
        guard !targets.isEmpty, id >= 0, Int(id) < tree.count else { return false }
        var cursor = id
        var hops = 0
        while cursor >= 0, hops < 128 {
            if targets[cursor] != nil { return true }
            cursor = tree.parent[Int(cursor)]
            hops += 1
        }
        return false
    }

    /// Bytes under `id` that can be freed: all of it when it sits inside a
    /// target, otherwise the targets beneath it.
    func bytes(under id: Int32, tree: FileTree, totals: [Int64]) -> Int64 {
        guard !targets.isEmpty, id >= 0, Int(id) < totals.count else { return 0 }
        if contains(id, tree: tree) { return totals[Int(id)] }
        var sum: Int64 = 0
        for (target, size) in targets where Self.isAncestor(id, of: target, tree: tree) { sum += size }
        return min(sum, totals[Int(id)])
    }

    /// Striped on the map and rings: inside a target, or at least 60% freeable.
    func isMostly(_ id: Int32, tree: FileTree, totals: [Int64]) -> Bool {
        guard id >= 0, Int(id) < totals.count else { return false }
        if contains(id, tree: tree) { return true }
        let size = totals[Int(id)]
        guard size > 0 else { return false }
        return Double(bytes(under: id, tree: tree, totals: totals)) / Double(size) >= 0.6
    }

    static func isAncestor(_ ancestor: Int32, of id: Int32, tree: FileTree) -> Bool {
        var cursor = tree.parent[Int(id)]
        var hops = 0
        while cursor >= 0, hops < 128 {
            if cursor == ancestor { return true }
            cursor = tree.parent[Int(cursor)]
            hops += 1
        }
        return false
    }
}

extension ScanModel {
    var freeableIndex: FreeableIndex { FreeableIndex(reviewables: cachedReviewables, totals: selectedTotals) }
}

/// Violet stripes clipped to a path, for Canvas drawing.
enum HatchFill {
    static func draw(_ path: Path, in rect: CGRect, context: inout GraphicsContext,
                     ground: Color = DiskMapTheme.accentWash, stripe: Color = DiskMapTheme.accent.opacity(0.55)) {
        context.fill(path, with: .color(ground))
        var lines = Path()
        var x = rect.minX - rect.height
        while x < rect.maxX {
            lines.move(to: CGPoint(x: x, y: rect.maxY))
            lines.addLine(to: CGPoint(x: x + rect.height, y: rect.minY))
            x += 7
        }
        context.drawLayer { layer in
            layer.clip(to: path)
            layer.stroke(lines, with: .color(stripe), lineWidth: 1.5)
        }
    }
}
