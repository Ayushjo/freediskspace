import Foundation
import Testing
@testable import DiskMapApp
@testable import DiskMapCore

/// Explore → List (redesign board 05): the token search, the filters and the row words.
@Suite("Explore list")
struct ExploreListTests {
    @Test func tokensAreUnderstoodAndTheRestIsText() {
        let q = ListQuery.parse("size>1GB kind:video changed>1y in:Downloads name:Holiday trip")
        #expect(q.minSize == 1_000_000_000)
        #expect(q.kind == .video)
        #expect(q.olderThanDays == 365)
        #expect(q.inFolder == "downloads")
        #expect(q.words == ["holiday", "trip"])
        #expect(q.tokens == ["size>1GB", "kind:video", "changed>1y", "in:Downloads", "name:Holiday"])
    }

    @Test func sizeAndAgeSpellings() {
        #expect(ListQuery.parse("size:500mb").minSize == 500_000_000, "size: means at least")
        #expect(ListQuery.parse("size<2.5g").maxSize == 2_500_000_000)
        #expect(ListQuery.parse("opened>6m").olderThanDays == 180, "opened is accepted for changed")
        #expect(ListQuery.parse("modified<2w").newerThanDays == 14)
        #expect(ListQuery.bytes("1tb") == 1_000_000_000_000)
        #expect(ListQuery.bytes("12") == 12)
        #expect(ListQuery.days("3x") == nil)
    }

    @Test func unrecognisedTokensAreSearchedNotDropped() {
        let q = ListQuery.parse("size>lots kind:spaceship sizeable")
        #expect(q.tokens.isEmpty)
        #expect(q.words == ["size>lots", "kind:spaceship", "sizeable"])
        #expect(ListQuery.parse("   ").isEmpty)
    }

    @Test func matchingAppliesEveryPart() {
        let q = ListQuery.parse("size>1GB kind:video changed>1y in:movies holiday")
        let text = "holiday 2023.mov\n~/movies/holiday 2023.mov"
        #expect(q.matches(searchText: text, folderPath: "movies", size: 7_000_000_000, kind: .video, ageDays: 400))
        #expect(!q.matches(searchText: text, folderPath: "movies", size: 900_000_000, kind: .video, ageDays: 400), "too small")
        #expect(!q.matches(searchText: text, folderPath: "movies", size: 7_000_000_000, kind: .archive, ageDays: 400), "wrong kind")
        #expect(!q.matches(searchText: text, folderPath: "movies", size: 7_000_000_000, kind: .video, ageDays: 30), "too recent")
        #expect(!q.matches(searchText: text, folderPath: "movies", size: 7_000_000_000, kind: .video, ageDays: nil), "unknown age never counts as old")
        #expect(!q.matches(searchText: text, folderPath: "desktop", size: 7_000_000_000, kind: .video, ageDays: 400), "wrong folder")
    }

    private func entry(_ id: Int32, _ name: String, _ size: Int64, kind: FileKind, day: Int32, path: String) -> BiggestFilesView.Entry {
        BiggestFilesView.Entry(id: id, name: name, absolutePath: path, parent: "p", kind: kind, size: size, modifiedDay: day,
                               searchText: name.lowercased(), folderText: "p",
                               category: StorageClassifier.classify(path: path, isDirectory: false))
    }

    @Test func filterIsLargestFirstAndHonoursKindAgeAndFolder() {
        let items = [entry(1, "a.zip", 10, kind: .archive, day: 900, path: "/s/a.zip"),
                     entry(2, "b.mov", 30, kind: .video, day: 100, path: "/s/x/b.mov"),
                     entry(3, "c.mov", 20, kind: .video, day: 0, path: "/s/x/c.mov")]
        let all = BiggestFilesView.filter(items, query: ListQuery(), kind: nil, age: .any, folder: nil, category: nil, today: 1_000)
        #expect(all.map(\.id) == [2, 3, 1])
        let videos = BiggestFilesView.filter(items, query: ListQuery(), kind: .video, age: .any, folder: nil, category: nil, today: 1_000)
        #expect(videos.map(\.id) == [2, 3])
        let old = BiggestFilesView.filter(items, query: ListQuery(), kind: nil, age: .sixMonths, folder: nil, category: nil, today: 1_000)
        #expect(old.map(\.id) == [2], "a file with no date is not old")
        let inX = BiggestFilesView.filter(items, query: ListQuery(), kind: nil, age: .any, folder: "/s/x", category: nil, today: 1_000)
        #expect(inX.map(\.id) == [2, 3])
        let notPrefix = BiggestFilesView.filter(items, query: ListQuery(), kind: nil, age: .any, folder: "/s/x/b", category: nil, today: 1_000)
        #expect(notPrefix.isEmpty, "/s/x/b is not a folder of /s/x/b.mov")
    }

    @Test func everyTrySuggestionParsesToSomething() {
        for s in BiggestFilesView.suggestions(compact: false) {
            #expect(!ListQuery.parse(s.query).tokens.isEmpty, "\(s.title) uses tokens the box understands")
        }
        #expect(BiggestFilesView.suggestions(compact: true).count == 2)
    }

    @Test func rowWords() {
        #expect(BiggestFilesView.badge("Holiday.mov", kind: .video) == "MOV")
        #expect(BiggestFilesView.badge("Photos Library.photoslibrary", kind: .other) == "PHO")
        #expect(BiggestFilesView.badge("data", kind: .other) == "FILE")
        #expect(BiggestFilesView.trimmedPlace("Downloads/misc/") == "Downloads/misc")
        #expect(BiggestFilesView.trimmedPlace("") == "/")
        #expect(ListFileRow.plain("Run `docker system prune`") == "Run docker system prune")
        let other = StorageClass(id: "other", title: "Other", colorHex: "", blurb: "")
        #expect(BiggestFilesView.kindText(.other, other) == "File")
        #expect(BiggestFilesView.kindText(.diskImage, other) == "Disk image")
    }

    @Test func canBeFreedNote() {
        let docker = StorageClassifier.classify(path: NSHomeDirectory() + "/Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw",
                                                isDirectory: false)
        #expect(BiggestFilesView.freeNote(freeable: true, bytes: 6_010_000_000, verdict: docker) == .accent("6.01 GB rebuilds by itself"),
                "what Clean up can free wins")
        if case .review(let instead) = BiggestFilesView.freeNote(freeable: false, bytes: 1, verdict: docker) {
            #expect(instead?.contains("prune") == true)
        } else {
            Issue.record("Docker's disk is a look-first item")
        }
        let plain = StorageClassifier.classify(path: "/tmp/notes.txt", isDirectory: false)
        #expect(BiggestFilesView.freeNote(freeable: false, bytes: 1, verdict: plain) == .none)
    }
}

@Suite("Explore list folders")
struct ExploreListFolderTests {
    @Test func folderWords() {
        #expect(BiggestFoldersView.contents(files: 1, folders: 0) == "1 file")
        #expect(BiggestFoldersView.contents(files: 1_204, folders: 31) == "1,204 files · 31 folders")
        #expect(BiggestFoldersView.footerCount(folders: 8, files: 0) == "8 folders")
        #expect(BiggestFoldersView.footerCount(folders: 3, files: 1) == "3 folders, 1 file")
        #expect(BiggestFoldersView.footerCount(folders: 0, files: 0) == "0 files")
        #expect(BiggestFoldersView.folderKind(StorageClass(id: "other", title: "Other", colorHex: "", blurb: "")) == "Folder")
    }

    @Test func folderNoteSaysHowMuchIsInside() {
        let plain = StorageClassifier.classify(path: "/tmp/x", isDirectory: true)
        #expect(BiggestFoldersView.folderNote(contained: true, freeBytes: 5, size: 9_000_000_000, verdict: plain) == .accent("9.00 GB rebuilds by itself"))
        #expect(BiggestFoldersView.folderNote(contained: false, freeBytes: 45_500_000_000, size: 1, verdict: plain) == .accent("45.5 GB can be freed"))
        #expect(BiggestFoldersView.folderNote(contained: false, freeBytes: 0, size: 1, verdict: plain) == .none)
    }
}

@Suite("Find anything")
struct FindAnythingTests {
    @Test func paletteShowsTheRealShortcuts() {
        #expect(CommandPalette.shortcut(for: .overview) == "⌘1")
        #expect(CommandPalette.shortcut(for: .cleanSafe) == "⌘2")
        #expect(CommandPalette.shortcut(for: .biggestFiles) == nil, "pages outside the sidebar have no number")
    }

    @Test func matchesAreMarkedAndPathsAreTheParent() {
        let marked = CommandPalette.highlight("com.docker.docker", query: "dock")
        let runs = marked.runs.filter { $0.foregroundColor != nil }
        #expect(runs.count == 2, "every occurrence is marked")
        #expect(CommandPalette.highlight("Docker", query: "size>1GB").runs.allSatisfy { $0.foregroundColor == nil }, "query tokens are not words")
        #expect(CommandPalette.parentPath("~/Library/Containers/com.docker.docker") == "~/Library/Containers")
    }
}
