import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Duplicates (redesign board 07): which copy stays, swapping it, and the words.
@Suite("Duplicates page")
struct DuplicatesPageTests {
    private let group = DuplicateGroup(hash: "h", fileIDs: [1, 2, 3], sizeEach: 100, cloneSets: [])
    private let days: [Int32: Int32] = [1: 300, 2: 100, 3: 200]
    private let lengths: [Int32: Int] = [1: 10, 2: 40, 3: 20]

    private func keeper(_ policy: DuplicatesView.KeepPolicy) -> Int32? {
        DuplicatesView.keeper(group, policy: policy, day: { days[$0] ?? 0 }, pathLength: { lengths[$0] ?? 0 })
    }

    @Test func eachPolicyKeepsTheRightCopy() {
        #expect(keeper(.oldest) == 2)
        #expect(keeper(.newest) == 1)
        #expect(keeper(.shortestPath) == 1)
    }

    @Test func tiesNeverFlicker() {
        let tied = DuplicateGroup(hash: "t", fileIDs: [5, 4], sizeEach: 1, cloneSets: [])
        let k = DuplicatesView.keeper(tied, policy: .newest, day: { _ in 7 }, pathLength: { _ in 3 })
        #expect(k == 4, "same day and length: the lowest id")
    }

    @Test func keptCopyIsListedFirst() {
        #expect(DuplicatesView.ordered(group, keeper: 3) == [3, 1, 2])
        #expect(DuplicatesView.ordered(group, keeper: nil) == [1, 2, 3])
    }

    @Test func tickingTheKeptCopySwapsWhichStays() {
        // Kept 2; 1 and 3 ticked. Ticking 2 keeps 1 instead.
        let result = DuplicatesView.swap(group, keeper: 2, checked: [1, 3])
        #expect(result?.keeper == 1)
        #expect(result?.checked == [2, 3])
        // An unticked copy is preferred as the new keeper.
        let preferUnticked = DuplicatesView.swap(group, keeper: 2, checked: [1])
        #expect(preferUnticked?.keeper == 3)
        #expect(preferUnticked?.checked == [1, 2])
        #expect(DuplicatesView.swap(DuplicateGroup(hash: "x", fileIDs: [9], sizeEach: 1, cloneSets: []), keeper: 9, checked: []) == nil)
    }

    @Test func swappingNeverTicksEveryCopy() {
        for keeper in group.fileIDs {
            let others = Set(group.fileIDs.filter { $0 != keeper })
            let result = DuplicatesView.swap(group, keeper: keeper, checked: others)
            #expect(result.map { !group.fileIDs.allSatisfy($0.checked.contains) } == true)
        }
    }

    @Test func kindsAndWords() {
        #expect(DuplicatesView.kind(of: "IMG_2044.HEIC") == .photos)
        #expect(DuplicatesView.kind(of: "Interview raw.wav") == .audio)
        #expect(DuplicatesView.kind(of: "Holiday.mov") == .videos)
        #expect(DuplicatesView.kind(of: "Design assets.zip") == .archives)
        #expect(DuplicatesView.dateText(day: 19_734) == "12 Jan 2024")
        #expect(DuplicatesView.dateText(day: 0) == "—")
        #expect(DuplicatesView.place("/tmp/scan/Movies/a.mov", root: "/tmp/scan") == "scan/Movies/a.mov")
        #expect(DuplicatesView.place("/tmp/scanner/a.mov", root: "/tmp/scan") == "/tmp/scanner/a.mov", "a sibling with the same prefix is not inside")
        #expect(DuplicatesView.place(NSHomeDirectory() + "/Movies/a.mov", root: "/") == "~/Movies/a.mov")
    }
}
