# Where the space goes: research and plan (Mac + Windows)

Written 2026-10-21. It answers the user pain points collected from HN, Apple's forums, MacRumors, Microsoft Q&A and similar places:

- "System Data" that nobody can explain
- the app's numbers not matching About This Mac
- macOS itself filling the disk
- Xcode simulators
- iCloud and OneDrive files showing different sizes
- leftovers from deleted apps
- Messages and phone backups
- wanting a free, offline tool

Five research tracks were run. Each claim has a source, and each item is marked:

- **[verified]**: from an official doc, a man page, source code, or reproduced on this Mac
- **[source]**: read in a third-party repository
- **[weak]**: a forum post, a single report, or a vendor blog

The raw track reports are kept outside the repo, at `scratchpad/research/1-5`.

Rules that apply to everything below (AGENTS.md):

- Removal only goes through `CleanupQueue` (Trash or Recycle Bin).
- No networking.
- Any change to an excluded-paths list must be called out on its own.
- No force-unwraps in Core.

For anything the OS owns, we **show it, explain it, and send the user to the official removal**. We never delete it ourselves.

---

## 0. Bugs the research found in our own code (fix first)

These come before any new feature, because they make today's numbers or matches wrong. The first four were re-checked in the source on 2026-10-21.

| # | Bug | Where | Evidence | Fix |
|---|---|---|---|---|
| B1 | "Used" is the whole APFS container, not the Data volume. About 49 GB on this Mac (System, Preboot, VM/swap, Recovery) gets blamed on "snapshots or purgeable". | `VolumeStats.swift:40-44`; copy at `OverviewView.swift:571` | **[verified]** `statfs` returns the same blocks and free space for `/`, Data, VM and Preboot. `getattrlist(ATTR_VOL_SPACEUSED)` gives per-volume figures (Data 346.44 GB, VM 23.62, System 12.57, Preboot 10.75), matching `diskutil apfs list`. | Read per-volume `ATTR_VOL_SPACEUSED` (no root needed) and feed the breakdown in §1. Fix the misleading sentence. |
| B2 | An evicted iCloud file is counted at its full cloud size as "on disk". | `BulkScan.swift:381` (`allocated > 0 ? allocated : logical`) runs for dataless files too (`dataless` is computed at `:364` but not used in this condition) | **[verified in code]** Contradicts TASK-002 ("allocated 0 is the truthful on-disk size"). | Keep allocated at 0 when `dataless`. Replay `Tests/Fixtures/evicted-ubiquitous-item.json`. |
| B3 | Developer view hides DerivedData, DeviceSupport, Archives and Previews. | `developer-rules.json:108` "Xcode" rule (keep), plus dedup at `DeveloperCatalog.swift:345-366` | **[verified]** `diskmap dev <fixture> --json` returned only `Library/Developer/Xcode` and `CoreSimulator`. | Make "Xcode" a container that never wins the dedup, or drop it. Add a nested-children regression test. |
| B4 | App leftover matching is prefix-only. `org.mozilla.firefox` claims Firefox Developer Edition's data, and `com.google.Chrome` claims Canary's. | `AppLeftoverFinder.swift:149` `item.hasPrefix(bid)` | **[verified in code]** | Exact ID, or ID plus `.`/`-` with a known suffix list only. Tests for both pairs. |
| B5 | Single-file leftovers (every `.plist`) show 0 bytes. | `AppLeftoverFinder.swift:157-169`: `enumerator` yields nothing for a file | **[verified]** in the agent's test (5 KB file → 0) | Size the file itself when it isn't a directory. |
| B6 | iPhone backups are never recognised. | `FileKind.swift:66` looks for "mobilebackup", or "backup" plus ".backup"/"iphone". Real backups live at `MobileSync/Backup/<UDID>/xx/<sha1>`. | **[verified in code]** | Match `*/MobileSync/Backup/<dir>` (see §6). |
| B7 | Windows: two different attributes share the value 0x40000 (`RECALL_ON_OPEN` = `FILE_ATTRIBUTE_EA`), and the incremental scan tests only 0x400000, unlike the full scan. | `windows/.../IncrementalScan.cs:268-269` vs `ScanEngine.cs:293-295` | **[verified]** Microsoft says RECALL_ON_OPEN appears only in directory-enumeration classes. Whether the MFT path misflags WSL extended-attribute files is **[weak]**, so test it. | Share one `DecideFromAttributes` and add a table test that includes the EA collision. |
| B8 | Windows leftover matching is a substring match: "Steam" matches "SteamVR", and any publisher token matches. | `windows/.../AppLeftoverFinder.cs:156-163` | **[verified in code]** | Whole-word tokens, plus a same-publisher penalty (§7). |
| B9 | Windows: Recall's store `%LOCALAPPDATA%\CoreAIPlatform.00` can be staged for the Recycle Bin. | Missing from `CleanupQueue.cs:303-320` | **[verified in code]**; the path itself is **[weak]** (third-party) | **Rule 3 change, needs maintainer sign-off:** add it to the excluded prefixes and classify it Never, with Recall's Settings path. |
| B10 | WSL advice recommends `--set-sparse true` without a warning. | `StorageClassifier.cs:108-109` | **[verified]** WSL 2.5.x required `--allow-unsafe` because of a data-corruption warning (WSL #13075, Jun 2025) | Add the warning, and lead with compaction steps instead (§4). |
| B11 | `CanonicalPath` doesn't list the `/System/Volumes/Data/System/Library/AssetsV2` firmlink twin. | `CanonicalPath.swift:10-26` | **[weak]**, might double-count on a "/" scan with cross-mount on | Add a `ScanIdentityTests` case first; fix if it reproduces. |
| B12 | A "/" scan walked into other volumes: swap (`/System/Volumes/VM`, 24.7 GB here), Preboot and every drive under `/Volumes`. | `BulkScan.swift` descent check uses `ATTR_CMN_DEVID`, which `getattrlistbulk` reports with the parent's device for a mount point | **[verified]** found 2026-10-22 while testing §1: "/" totalled 427.84 GB and `/System` 41.31 GB; after the fix 400.43 GB and 16.6 GB, and `/Volumes` stops at all 3 drives | **Fixed:** `ForeignMounts` reads the mount table once (devices via `lstat`; remote/autofs never touched) and the walk and quick rescan stop there. |

---

## 1. "Where's the rest?" — explaining System Data and the gap (Mac + Windows)

### What is true

- **[verified]** Apple calls System Data a catch-all ([Apple 102624](https://support.apple.com/en-us/102624)). Storage settings reach it as a remainder: used space minus everything they could classify. Snapshots, caches and misfiled user data all end up there ([Eclectic Light 2025-02-11](https://eclecticlight.co/2025/02/11/what-is-system-data-in-storage-settings/), [Tahoe explainer 2025-12-20](https://eclecticlight.co/2025/12/20/explainer-storage-settings/)).
  - No Apple document lists what goes in. **Our copy must never claim to "show System Data".** We can only explain the gap between what the disk uses and what our scan found.
- **[verified]** Purgeable space is what CacheDelete can free on demand. It purges caches first and Time Machine snapshots last; third-party snapshots are never purged ([Eclectic Light 2023-04-19](https://eclecticlight.co/2023/04/19/ventura-space-management-what-gets-purged-and-how/)).
  - Apps can read it as `volumeAvailableCapacityForImportantUsage` minus statfs free. That was about 12.3 GB on this Mac.
  - It lags behind deletions ([Eclectic Light 2026-08-24](https://eclecticlight.co/2026/08/24/arent-snapshots-purgeable/)).
  - We already compute `purgeableBytes` (`VolumeStats.swift:63-65`) but never show it.
- **[verified]** Local snapshots can be listed without root (`tmutil listlocalsnapshots /`, `diskutil apfs listSnapshots`, `fs_snapshot_list`). There is **no public way to get a snapshot's size** ([forum 81171](https://developer.apple.com/forums/thread/81171)). Apple counts snapshot space as available storage ([Apple 102154](https://support.apple.com/102154)).
- **[verified]** Deleting a snapshot through `fs_snapshot_*` needs root plus an entitlement Apple only grants to backup apps ([thread 786595](https://developer.apple.com/forums/thread/786595)).
  - Whether `tmutil deletelocalsnapshots` works for a normal user is **unverified**. Test it on a real Mac before promising it.
- **[verified]** User reports of large System Data: 410 GB ([MacRumors](https://forums.macrumors.com/threads/system-data-huge-410gb-on-m3-mba.2476264/)), about 500 GB of snapshots ([Apple Community](https://discussions.apple.com/thread/256298936)), and a 100 GB Spotlight index ([mjtsai 2025-07-21](https://mjtsai.com/blog/2025/07/21/spotlight-indexing-running-wild)). All are **[weak]** as single reports.
- **Windows:**
  - "System & reserved" is a Settings category ([Microsoft](https://support.microsoft.com/help/4027321/)).
  - Pagefile size is readable through `Win32_PageFileUsage` ([Learn](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pagefileusage)).
  - WinSxS looks inflated because of hard links; the true size comes from `DISM /AnalyzeComponentStore`, which needs admin ([Learn](https://learn.microsoft.com/windows-hardware/manufacture/desktop/determine-the-actual-size-of-the-winsxs-folder), [Raymond Chen](https://devblogs.microsoft.com/oldnewthing/20160322-00/?p=93183)).
  - Shadow copies are in `Win32_ShadowStorage` ([Learn](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/vsswmi/win32-shadowstorage)). Non-admin access is unverified (likely denied).
  - Reserved storage is about 7 GB **[weak]**, and reading it needs admin.

### What we build

**A "Where's the rest?" card** replaces the one-line "This scan accounts for…" and its causes list.

Mac buckets, each with a confidence label:

| Bucket | Source (offline, no root) | Confidence |
|---|---|---|
| macOS (sealed System volume) | `ATTR_VOL_SPACEUSED` on the System volume | Exact |
| Swap (VM volume) | `ATTR_VOL_SPACEUSED` on VM, plus `vm.swapusage` | Exact |
| Preboot, Recovery and APFS metadata | per-volume figures; container used minus the mounted volumes | Exact (remainder) |
| Data volume outside the scanned folder | Data used minus scan total (when the root isn't `/`) | Exact |
| Purgeable (system estimate) | ImportantUsage minus free | Estimate; can lag |
| Time Machine local snapshots | count and dates from `tmutil`/`fs_snapshot_list`; size shown as "part of purgeable" | Count exact, size unknown |
| Other apps' snapshots | `diskutil apfs listSnapshots -plist`; "not purgeable, kept by <app>" | Count exact |
| Folders we couldn't read | count, plus a Full Disk Access button | Size unknown |
| **Still unexplained** | what's left, always shown on its own | — |

- **Header:** lead with **Data volume used**, as Disk Utility does. Keep container free space for the sidebar bar.
- **Edge cases:** non-APFS or external roots (hide the volume and snapshot rows); FileVault-locked volumes; purgeable lag; a scan total bigger than used because of clones (clamp, and explain why).
- **Windows card:**
  - pagefile, hiberfil and swapfile (from the scan, cross-checked with WMI)
  - System Volume Information, unsized without admin. An optional "Measure (admin)" runs only a read-only `vssadmin list shadowstorage`.
  - Reserved storage, about 7 GB, managed by Windows
  - WinSxS, explaining why Explorer shows more
  - OneDrive and compressed files (logical vs on disk)
  - NTFS metadata and the remainder
  - We never delete restore points; we open `SystemPropertiesProtection.exe` instead.
- **Snapshot removal: built (D1).** `TimeMachineSnapshotRemoval` + a confirm in the sheet; recorded in AGENTS.md rule 1. Measured 2026-10-22: `tmutil localsnapshot` and `tmutil deletelocalsnapshots <date>` both work without root; a non-date argument is read as a disk (would delete every snapshot), so only validated dates are passed.
- **Tests:**
  - injected volume figures: buckets sum to used or less, and the remainder is 0 or more
  - `tmutil` and `diskutil` plist parser fixtures
  - a live smoke test that Data's `ATTR_VOL_SPACEUSED` is no more than container used
  - Windows popover text per bucket, with fake WMI values

**Effort:** M (Mac), M (Windows).

---

## 2. Storage macOS and Windows fill themselves

### What is true

| Item | Path | Size evidence | Can we read it? | Official removal | Comes back? |
|---|---|---|---|---|---|
| Apple Intelligence, Siri, dictation and translation models | `/System/Library/AssetsV2/com_apple_MobileAsset_UAF_*` and `…LinguisticData` | 4.7 GB on this Mac **[verified locally]**. Up to 14 GB on macOS 27, some Macs over 30 GB ([MacRumors 2026-09-23](https://macrumors.com/2026/09/23/apple-intelligence-30gb-some-macs-macos-27)). HN thread with 775 points and 559 comments ([item 49957116](https://news.ycombinator.com/item?id=49957116)) **[verified thread]**; individual user numbers are **[weak]** | Mostly (`du` worked; one subfolder denied). SIP-protected, so it can't be deleted. | macOS 26: turn off Apple Intelligence in System Settings, and macOS purges the models later. macOS 27: no supported master switch **[weak, press]** | Yes |
| Aerial wallpaper and screensaver videos | Tahoe: `~/Library/Application Support/com.apple.wallpaper/aerials/videos`; earlier: `/Library/Application Support/com.apple.idleassetsd/Customer` | 457 MB here; about 450 MB per 4K video **[weak]** | Yes | Choose a non-aerial wallpaper | Yes |
| Wallpaper agent cache bug | `~/Library/Containers/com.apple.wallpaper.agent/…/com.apple.wallpaper.caches` | Apple's forums confirm the bug (FB13536275, back on Tahoe as FB20636593, [thread 804419](https://developer.apple.com/forums/thread/804419)); "100 GB+" is one developer's claim **[weak]** | Yes | **Apple DTS called deleting it "a risky choice"; it's unsupported.** No official route. | Regrows |
| Installer apps | `/Applications/Install macOS *.app` | 12–17 GB **[weak estimate]** | Yes | **Trash is safe** | No |
| Pending updates | `/Library/Updates`, `AssetsV2/*SoftwareUpdate*` | 244 MB here | Yes | Don't delete; install them, or let `softwareupdated` clean up | — |
| Unified log, sleep image, swap, Spotlight index | `/private/var/db/diagnostics`, `/private/var/vm`, `.Spotlight-V100` | 2.6 GB of logs here | Mostly | **Never.** logd caps itself ([Eclectic Light](https://eclecticlight.co/2023/02/08/log-literacy-all-about-the-log/)). For Spotlight: Privacy list or `sudo mdutil -E` | — |
| Windows.old | `C:\Windows.old` | — | Likely admin-only | Settings › Storage › Temporary files ([MS](https://support.microsoft.com/en-us/windows/free-up-drive-space-in-windows-85529ccb-c365-490d-b548-831022bc9b32)); auto-removed after about 10 days | No |
| WinSxS | `C:\Windows\WinSxS` | — | — | **Never delete files** ([MS Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)). `DISM /StartComponentCleanup`, or Disk Cleanup's "Windows Update Cleanup" | Grows |
| Delivery Optimization and update downloads | system drive (exact DO path **[weak]**); `SoftwareDistribution\Download` | DO capped at 20% of disk, cleared automatically ([Learn](https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference)) | — | Temporary files | Yes |
| Recall snapshots | `%LOCALAPPDATA%\CoreAIPlatform.00\…` **[weak path]** | about 25 GB in a 2024 preview **[weak]** | — | **Only** Settings › Privacy & security › Recall ([MS](https://support.microsoft.com/en-gb/windows/manage-your-recall-snapshots-and-disk-space-2c35b596-5a96-4090-b791-c27fae75f660)) | Grows |
| Windows AI components | path **[weak]** | over 2.5 GB **[weak]** | — | Uninstall exists only in preview builds | — |
| Installer cache | `C:\Windows\Installer` | — | — | **Never** | — |

We should **not** recommend or bundle third-party "remove Apple Intelligence" tools such as RemoveMacAI. They work through configuration-profile hacks and break Siri and Writing Tools.

### What we build

**A "Storage your system manages" card**, driven by a new data file per platform (`system-storage.json`). Patterns stay as data, per AGENTS.md.

- **Entry fields:**
  - `id`, `title`, `paths[]`
  - `group`: AssetsV2 prefixes are grouped as "Apple Intelligence & Siri", "Language & dictation" and "Pending updates"
  - `action`: `never` | `guide` | `trash`
  - `steps` and `settingsURL` (`x-apple.systempreferences:` or `ms-settings:`)
  - an optional copy-only `command`
  - `regrows`, `sources[]`, `minOS`/`maxOS`
- **Measuring:** a Core function sizes the listed paths directly, so the card works even after scanning only the home folder. Anything it can't read shows "needs Full Disk Access / admin", never 0. WinSxS size comes only from the user running AnalyzeComponentStore.
- **What can reach `CleanupQueue`:**
  - `Install macOS *.app`
  - on Windows, only items already outside the excluded list
  - aerial videos and the wallpaper cache stay `guide` (Apple calls deleting them unsupported, and they come back)
- **Copy:** honest. On macOS 27 there is no supported off switch, so we explain where the space went and link Apple's settings.
- **Tests:**
  - the JSON loads
  - every `never`/`guide` path is refused by `stage()`
  - Mac AssetsV2 firmlink twin (B11)
- **Rule 3 changes, decision D2:** add `CoreAIPlatform.00` on Windows (B9). Optionally add `/Library/Updates` on Mac.

**Status (2026-10-22): Mac built.** `system-storage.json` + `SystemStorageCatalog`, shown as "What macOS keeps" in the Where's the rest? sheet and as `diskmap system`. Measured here: logs 2.89 GB, Apple Intelligence & Siri 2.23 GB, language & voices 2.01 GB, aerials 467 MB, pending updates 256 MB, wallpaper cache 97 MB; models match `du` within 2%. Settings pane ids checked against /System/Library/ExtensionKit. Only installers can reach Cleanup. **Rule 3 (2026-10-22, maintainer approved):** aerial videos (both locations) and the wallpaper cache added to the Mac excluded list. **Windows (2026-10-23): built** as "What Windows keeps" in the same sheet (`system-storage-windows.json`, `SystemStorageCatalog.Measure`): Windows.old, upgrade files, update downloads and Delivery Optimization, Recall, WinSxS (never sized), the Installer cache and logs. Sized from the scan where it covers the path, else by a bounded scan-engine walk (hard links once; a denied folder shows "+" or "Can't read"). Nothing goes to the Recycle Bin: no Windows entry is `trash`. D2 (`CoreAIPlatform.00` excluded) landed earlier, in b539054. Untested on a real Windows machine.

**Effort:** M per platform.

---

## 3. Developer storage: simulator runtimes, DeviceSupport, Docker and WSL

### What is true

- **Simulator runtimes moved several times:**
  - Xcode 13 and earlier: `.simruntime` bundles in `CoreSimulator/Profiles/Runtimes`
  - Xcode 14–15: DMGs in `CoreSimulator/Images`, mounted under `/Volumes` ([forum 746361](https://developer.apple.com/forums/thread/746361))
  - Now: SIP-protected MobileAssets in `/System/Library/AssetsV2/com_apple_MobileAsset_iOSSimulatorRuntime/*.asset`, about 9 GB each, plus a 7 GB `CoreSimulator/Caches/dyld` that rebuilds itself ([forum 842603](https://developer.apple.com/forums/thread/842603), [forum 812992](https://developer.apple.com/forums/thread/812992)) **[verified forums]**
- **Removing runtimes:**
  - Supported routes are Xcode › Settings › Components or `xcrun simctl runtime delete`. They're unreliable: runtimes come back after reboot, and orphaned ones aren't listed ([837906](https://developer.apple.com/forums/thread/837906)).
  - Apple DTS says manual deletion isn't supported. **Never advise turning off SIP.**
  - Today our scanner skips mounts and `/system` is "never", so **runtimes are invisible**.
- **`simctl runtime list -j` fields:** `identifier`, `version`, `build`, `kind`, `sizeBytes`, `lastUsedAt`, `deletable`, `state`, `platformIdentifier`. **[weak]**: these come from Chromium's test mock, not Apple's docs, and `--notUsedSinceDays`/`--dry-run` are unverified. Must be checked on a Mac with Xcode (this one only has Command Line Tools).
- **DeviceSupport:**
  - `~/Library/Developer/Xcode/{iOS,watchOS,tvOS,visionOS} DeviceSupport/<version>`, 11–47 GB reported ([forum 84973](https://developer.apple.com/forums/thread/84973))
  - recreated the next time a device on that OS connects; the cost is losing symbolication of old crash logs
  - safe to Trash per version
- **Previews:** `UserData/Previews` grows to 80–165 GB. Apple recommends `xcrun simctl --set previews delete all`; deleting the folder by hand has broken Previews ([forum 682439](https://developer.apple.com/forums/thread/682439)).
- **Simulator devices:** `simctl delete unavailable`. Trashing the folders leaves stale entries behind.
- **Docker on Mac:** `Docker.raw` is sparse (2.3 GB on disk out of a 64 GB maximum, [Docker FAQ](https://docs.docker.com/desktop/troubleshoot-and-support/faqs/macfaqs/)). We already show the on-disk size correctly.
- **WSL:**
  - `ext4.vhdx` never shrinks by itself. Its path is in `HKCU\…\Lxss\<guid>\BasePath` ([Learn](https://learn.microsoft.com/en-us/windows/wsl/disk-space)).
  - `sparseVhd` only applies to new disks.
  - Compaction: `wsl --shutdown`, then `Optimize-VHD` (admin, Hyper-V module) or diskpart `compact vdisk`.
- **Docker Desktop on Windows:** `docker_data.vhdx`. Pruning frees space inside the disk only, and compaction results vary ([Docker forum, May 2026](https://forums.docker.com/t/docker-desktop-wsl2-vhdx-grows-by-1-5gb-after-every-update-no-way-to-reclaim-space/151729)).

### What we build

- **Fix B3 first.**
- **Simulator runtimes panel (Mac):**
  - Columns: platform, version and build, size, last used, kind, and an "orphaned" flag.
  - Source A: `simctl runtime list -j`. Run it only when `xcode-select -p` points into an `Xcode*.app`; otherwise the `xcrun` stub can pop up the Command Line Tools installer. It's local and read-only.
  - Source B: size the asset, image and profile folders directly, as a fallback and cross-check.
  - Actions: copy `xcrun simctl runtime delete <id>`, or open Xcode › Settings › Components. Orphaned runtimes get "an Apple bug: remove via Components or reinstall Xcode". Bundled runtimes are marked not deletable.
- **DeviceSupport:** one row per version, removed by Trash through `CleanupQueue`. Suggest every version except the newest per platform, with a note about losing crash-log symbolication.
- **Split CoreSimulator into three:**
  - Devices: recipe only (`delete unavailable`); not Trash-safe
  - Caches: Trash OK
  - Previews: recipe only (`--set previews delete all`)
- **New rules:** Archives (point to Organizer; dSYM warning), visionOS DeviceSupport, SwiftPM cache.
- **Docker and WSL (Windows):**
  - List each distro from the registry with its vhdx size and a copyable "used inside" command.
  - Compaction checklist; `--set-sparse` only with its warning (B10).
  - Docker steps: `docker system df -v` → prune → compact.
- **Tests:**
  - nested Xcode children (B3)
  - `simctl` JSON parser fixture (fields optional, unknown keys ignored)
  - DeviceSupport "keep newest" selection
  - Windows Lxss registry parser fixture
- **Not researched in depth yet:** Ollama, Hugging Face, Gradle, Maven, bun, conda, Unity, Unreal, Visual Studio caches, Android image sizes. They need their own pass before we add rules.

**Status (2026-10-22): Mac built** as Developer › Xcode (`developer-storage.json`, same loader as §2): device support per version with the newest per platform kept (versions read from folder names; a folder with no version keeps everything), simulator caches and the Swift package cache to Cleanup, simulator devices / previews / runtimes / archives as commands or Xcode steps only. Runtimes are sized from their folders; `simctl runtime list -j` is **not** used yet — its fields are unverified and this Mac has no Xcode. Verified with fixtures and a fake DeviceSupport in the real home (then trashed). Windows WSL/Docker: not started.

**Effort:** M (Mac runtimes and DeviceSupport), M (Windows WSL/Docker), S (rules and B3).

---

## 4. iCloud and OneDrive files: "on this Mac" vs "in the cloud"

### What is true

- **[verified]** On Mac, every iCloud and File Provider file that's been evicted is "dataless".
  - Detect it with `SF_DATALESS` ([TN3150](https://developer.apple.com/documentation/technotes/tn3150-getting-ready-for-data-less-files)). `st_size` is the cloud size and the allocated size is the local footprint.
  - `setiopolicy_np(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, …OFF)` makes a thread fail with EDEADLK rather than download anything (TN3150; `sys/resource.h`). **We don't use it today.**
  - Since Sonoma there are no `.icloud` stub files any more ([Eclectic Light](https://eclecticlight.co/2023/10/25/macos-sonoma-has-changed-icloud-drive-radically/)).
- **[verified]** On Windows, the cloud-file attributes are RECALL_ON_DATA_ACCESS (0x400000), RECALL_ON_OPEN (0x40000), PINNED and UNPINNED ([Learn](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants)). `CfGetPlaceholderStateFromFindData` reads the state without opening the file.
- **[verified]** Microsoft documents why OneDrive's sizes don't match ([MS](https://support.microsoft.com/en-us/office/onedrive-sizes-and-dates-don-t-match-c42f93ad-f565-45e1-a91c-2491ef0dd138)). On Mac, the disagreements between Finder, System Settings and DaisyDisk/OmniDiskSweeper are **[weak]** (forums).
- **Official ways to free the local copy:**
  - Mac: Finder Remove Download, `brctl evict`, `evictUbiquitousItem`
  - Windows: Free up space, or `attrib +u -p`
  - Our own earlier attempt to evict (TASK-002) failed on a file that had never been uploaded.

### What we build

- **Fix B2 first.** Turn the no-download policy on for scan threads, and treat EDEADLK as "cloud only, skipped".
- **Columns:** **On this Mac** (allocated) and **In the cloud** (logical minus allocated, for flagged files and anything under `Mobile Documents`, `CloudStorage` or OneDrive roots).
- **State chip:** Cloud only / Downloaded (can be freed) / Always kept (Windows PINNED).
- **Filters:** `is:cloud` (exists) and the new `is:evictable`.
- **Explainer:** "Finder shows the cloud size; this shows what's on this disk."
- **Version 1 only guides:** Reveal, the official steps, and how much would come back. We don't evict files ourselves; that's decision D3. Evicting is never a delete, so it can **never** go through `CleanupQueue`.
- **Tests:**
  - fixture replay: an evicted file stays at 0 allocated and isn't marked "estimated"
  - flags-to-state as a pure function
  - Windows attribute table test, including the EA collision (B7)
  - live EDEADLK test that skips itself when there are no dataless files
  - previews and Duplicates never open a cloud-only file

**Effort:** S (bug fixes), M (columns and UI per platform).

---

## 5. Leftovers from apps you already deleted

### What is true

- **Where data lives:**
  - `~/Library`: Application Support, Caches, Preferences, Containers/`<bundle ID>`, Group Containers/`<team>.<group>`, Saved Application State, HTTPStorages, WebKit, Logs, LaunchAgents
  - `/Library`: the same, plus PrivilegedHelperTools
- **Pearcleaner** (about 14.9k stars) has been **on hold** since the end of 2025 ([README](https://github.com/alienator88/Pearcleaner)) **[verified]**.
  - It treats anything not claimed by an installed app as an orphan.
  - That produced documented false positives: Zen Browser data deleted (#143), almost every app (#477), installed Steam and Unity games (#380), Little Snitch (#330), printer settings (#546, #610) **[source]**.
- **Mole** (about 72k stars) is the model to follow **[source]** (`lib/clean/apps.sh`):
  - exact match against installed bundle IDs from many places (Applications folders, Homebrew Caskroom, Setapp)
  - running apps; it aborts if that check fails
  - nothing modified in the last 30 days
  - a never-touch list (password managers, keychain, ssh, gpg)
  - `mdfind` fallback, where a timeout means "keep"
- **BCUninstaller** (Apache-2.0) scores each candidate with weighted reasons and only shows "Good" or "Very good" ones (`Junk/Confidence`) **[source]**.
- **Our gaps:**
  - No orphan detection exists today.
  - Mac app discovery only reads the top level of `/Applications` (`AppLeftoverFinder.swift:79-91`). It misses subfolders, Setapp, the Caskroom and external drives, which would turn real apps into "orphans".

### What we build

Orphans are **opt-in, conservative, and never pre-ticked**.

1. **Known apps** = LaunchServices (`NSWorkspace.urlsForApplications(withBundleIdentifier:)`) ∪ a recursive `/Applications` walk (depth 3) ∪ `~/Applications` ∪ Setapp ∪ Caskroom ∪ mounted volumes' Applications ∪ running apps ∪ loaded LaunchAgent labels. **If any of these sources fails, show nothing.**
2. **Candidates:** reverse-DNS names (3+ labels), or Containers whose metadata ID can be read, under `~/Library` only in v1. The exact ID must be unknown to every source.
3. **Never shown:**
   - `com.apple.*` and `group.com.apple.*`
   - anything with the same vendor prefix as an installed app
   - password managers, keychain, ssh, gpg
   - anything modified in the last 30 days
   - name-only matches and UUID-named folders
   - Group Containers whose team ID belongs to an installed app
4. **Score:**
   - +4 exact ID unknown everywhere; +4 container metadata names a missing app; +2 older than 180 days; +2 caches or logs only
   - −7 same vendor installed; −4 contains executables or a launchd plist; −3 contents look like large user documents
   - Show only Good and Very good, each with its reasons.
5. **Removal:** Trash through `CleanupQueue.stage`. **No change to the excluded list.**
6. **Windows:** the same model over `%AppData%`, `%LocalAppData%`, `%ProgramData%` and the top level of Program Files.
   - Whole-word matching against Uninstall entries.
   - Penalties for executables, services and scheduled tasks.
   - Never eligible for the permanent-delete exception.
7. **Tests** (real temporary Library trees):
   - Firefox vs Firefox Developer Edition, Chrome vs Canary (B4)
   - an app inside a vendor subfolder isn't an orphan
   - recent files are excluded
   - Apple and Group Containers are excluded
   - a failing source produces an empty result
   - fixed score levels
   - Windows: Steam vs SteamVR (B8)

**Effort:** L (Mac), M (Windows; the registry inputs already exist).

---

## 6. Phone backups and Messages

### What is true

- **[verified]** Where iPhone backups are kept ([Apple 108809](https://support.apple.com/108809)):
  - Mac: `~/Library/Application Support/MobileSync/Backup/`
  - Windows: `%USERPROFILE%\Apple\MobileSync\Backup`, or `%AppData%\Apple Computer\MobileSync\Backup`
- Apple's removal route is Finder or Apple Devices › Manage Backups, which needs the device connected. Older macOS Storage settings listed backups under "iOS Files" and removed them without the device.
- Both folders need Full Disk Access on Mac (confirmed here: "Operation not permitted").
- **[verified]** Backup metadata keys, from libimobiledevice's source:
  - `Info.plist`: Device Name, Display Name, Last Backup Date, Product Type, Product Version
  - `Manifest.plist`: `IsEncrypted`
  - **Never read or show** IMEI, ICCID, MEID, Phone Number or Serial Number, which are in the same file.
- **[verified]** Messages: "Keep messages" (30 days / 1 year / Forever) removes conversations including their attachments ([Apple guide](https://support.apple.com/en-euro/guide/messages/icht1035/mac)). With Messages in iCloud, deletions sync to every device, and there's no export ([mjtsai 2022](https://mjtsai.com/blog/2022/06/22/limiting-imessages-disk-usage/)).
  - **No Apple source says deleting files from `~/Library/Messages/Attachments` directly is safe**, so we treat it as unsafe.
- **Chat apps on Windows:**
  - New Teams cache: `…\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams` (quit Teams first). Deleting the whole package breaks Teams.
  - WhatsApp's `transfers` folder is the user's media, not a cache.
  - Both paths are **[weak]**, third-party.

### What we build

**Phone backups (Mac + Windows):**
- **Rows:** device name, iOS version, backup date, size, lock icon if encrypted, an "older than 1 year" badge.
- **Device names:** a bundled table maps Product Type codes to names.
- **Without Full Disk Access:** "Turn on Full Disk Access to see phone backups", never "none found".
- **Main action:** Apple's own route, with steps (and Storage › iOS Files where it exists).
- **Second action:** move the **whole** `<UDID>` folder to the Trash through `CleanupQueue`.
  - Staging anything *inside* a backup is blocked, because a partial removal corrupts it.
  - Extra warnings for encrypted backups (they hold Health and Keychain data) and for the newest backup of each device.
  - Windows: never eligible for permanent deletion. If the Recycle Bin refuses, fall back to Apple Devices.
- **Blocking partial staging is a rule 3 change.** Decision D4.

**Messages and chat apps (guidance only, nothing goes to the Trash):**
- Show the sizes of Messages `Attachments` and chat.db.
- Buttons open Storage › Messages and Messages › Settings › Keep messages.
- Copy: shortening "Keep messages" deletes across all iCloud devices; there's no export; deleting attachment files directly isn't supported.
- `~/Library/Messages` is marked never-stage.
- WhatsApp: "delete in WhatsApp › Storage". Teams: only `LocalCache\Microsoft\MSTeams` is a reviewable cache.

**Fix B6.**

**Tests:**
- `Info.plist` and `Manifest.plist` fixtures, including binary plists
- sensitive keys are never surfaced
- staging inside a backup is refused
- the "newest per device" warning

**Effort:** M (backups, per platform), S (Messages guidance).

---

## 7. Positioning: claims we can make, claims to avoid

**We can truthfully say:**
- free and open source
- no account
- scans and cleanup work offline; the only network use is an update check that's off unless you turn it on
- everything goes to the Trash or Recycle Bin with Put Back (Windows has one confirmed exception, for rebuildable items too big for the Recycle Bin)
- no subscription and no fake "issues found" scan
- for backups and Messages we send you to Apple's own tools

**Facts with dates and sources we can cite:**
- **CleanMyMac** pricing since July 2025: Basic $47.50/yr or $119.95 one-time ([Macworld](https://www.macworld.com/article/352922/cleanmymac-x-review-macos.html)).
- **DaisyDisk:** the free version can't delete; the full version is $9.99 lifetime ([vendor](https://daisydiskapp.com/guide/full-vs-trial)). Calling it a subscription is wrong.
- **MacKeeper:** a 2015 class action (ZeoBIT) settled for $2M ([AppleInsider](https://appleinsider.com/articles/15/08/10/mackeeper-to-pay-out-2m-in-proposed-class-action-settlement)). It was not the FTC.
- **CCleaner:** version 5.33 shipped with a backdoor in 2017, affecting about 2.27M users ([The Register](https://www.theregister.com/2017/09/18/tainted_ccleaner_downloads)).
- **Avast** (CCleaner's parent): an FTC order in Feb 2024 to pay $16.5M ([FTC](https://ftc.gov/node/82647)).
- **Pearcleaner** is on hold.

**Avoid:**
- "never deletes anything permanently" (Windows has the exception)
- "zero network"
- "CleanMyMac is scareware"
- "CCleaner spies on you"; use the dated facts instead
- calling WizTree or SpaceSniffer open source (they're freeware)

---

## 8. Order of work

| Phase | What | Platforms | Effort | Why this order |
|---|---|---|---|---|
| **P0** | Bugs B1–B8, B10, B11 (B9 after D2) | both | S–M | Wrong numbers and wrong matches undermine everything after |
| **P1** | §1 "Where's the rest?" | Mac, then Windows | M | The #1 complaint; needs B1 |
| **P2** | §2 system-managed storage card | Mac, then Windows | M | Very topical (Apple Intelligence on macOS 27) |
| **P3** | §3 simulator runtimes, DeviceSupport, WSL/Docker | both | M | Large sizes, strong developer audience |
| **P4** | §6 phone backups, Messages guidance | both | M | Fixes B6, clear wins, mostly read-only |
| **P5** | §4 cloud columns | both | M | Needs B2 and B7 |
| **P6** | §5 orphaned leftovers | Mac, then Windows | L | Highest risk of false positives, so it goes last, behind B4, B5 and B8 |

**Every phase:**
- `scripts/test.sh` (Mac) and `dotnet test` (Windows)
- new Swift Testing and xUnit tests as listed
- renders through the snapshot harness
- `NetworkPolicyTests` still green
- excluded-paths changes only through D2 and D4, called out in the PR

## 9. Decisions (answered by the maintainer, 2026-10-21)

- **D1 → delete.** The app may delete Apple Time Machine local snapshots directly (the documented rule 1 exception from 2026-10-16). Only `com.apple.TimeMachine.*`, one confirmation, one file, a policy test, and AGENTS.md updated.
- **D2 → add.** `%LOCALAPPDATA%\CoreAIPlatform.00` goes on the Windows excluded list (rule 3 change, called out in its PR).
- **D3 → build the button.** Version 1 ships a real "Remove download" action (per-file, confirmed, never through `CleanupQueue`), tested properly so it isn't a gimmick.
- **D4 → whole folder only.** The maintainer left it to us: a whole backup folder may go to the Trash as the secondary action; staging anything inside a backup is refused.

Original questions, for reference:

- **D1 — Time Machine snapshots.** Your memory notes this as an OK'd rule 1 exception, but AGENTS.md doesn't say so and no code exists. Options:
  - (a) list-only, plus "open Time Machine settings"
  - (b) implement deletion of `com.apple.TimeMachine.*` snapshots only, after checking whether `tmutil deletelocalsnapshots` even works without root, and record the exception in AGENTS.md
- **D2 — Windows Recall store.** Add `%LOCALAPPDATA%\CoreAIPlatform.00` to the Windows excluded list (B9). Optionally add `/Library/Updates` on Mac.
- **D3 — Cloud eviction.** Guide only (recommended for v1), or later add an explicit per-file "Remove download" action (never through `CleanupQueue`).
- **D4 — Phone backups.** Allow moving a whole backup folder to the Trash as a secondary action, and block staging anything inside one (a rule 3 change).

## 10. Evidence we still lack

- Reddit was not readable during research.
- The `simctl runtime list -j` fields and `--notUsedSinceDays` need a Mac with full Xcode.
- Windows paths for Delivery Optimization, AI components and Recall subfolders are from third-party sources, and Windows readability is untested; a real Windows machine is needed (Fahmin?).
- These need a dedicated pass before rules: wallpaper "100 GB+", the full aerial set size, installer app sizes, Android image sizes, Unity/Unreal/Visual Studio caches.
- ~~Whether `tmutil deletelocalsnapshots` needs root~~: it doesn't (measured 2026-10-22). `thinlocalsnapshots` untested and unused.
