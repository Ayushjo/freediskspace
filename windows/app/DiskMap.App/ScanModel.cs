using System.Collections.ObjectModel;
using System.IO;
using DiskMap.Core;

namespace DiskMap.App;

/// <summary>
/// App-wide scan state — the Windows counterpart of the macOS app's
/// shared ScanModel. One scan feeds every page; totals and item counts
/// are rolled up once and reused. Views read Tree/Totals directly and
/// subscribe to changes.
///
/// Anything proportional to the tree (the scan itself, rollups) runs off
/// the UI thread; only the finished values are published here.
/// </summary>
public sealed class ScanModel : ViewModelBase
{
    public static ScanModel Shared { get; } = new();

    private readonly ScanEngine _engine = new();

    private ScanModel()
    {
        // A landed measurement changes the staged figures; refresh the
        // badge + any open page on the UI thread.
        Cleanup.Measured += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(RefreshStaged);
    }

    private FileTree? _tree;
    public FileTree? Tree { get => _tree; private set { if (Set(ref _tree, value)) Changed(); } }

    private long[] _totals = [];
    public long[] Totals { get => _totals; private set { if (Set(ref _totals, value)) Changed(); } }

    /// <summary>Both rollup bases, kept so the size toggle and the AnalysisSnapshot don't re-walk.</summary>
    private long[] _totalsLogical = [];
    private long[] _totalsAllocated = [];

    /// <summary>Both rollups for one node — the inspector's logical-vs-on-disk line.</summary>
    public (long Logical, long Allocated)? DualTotals(int id) =>
        Tree is { Count: > 0 } t && _totalsLogical.Length == t.Count && _totalsAllocated.Length == t.Count
            && id >= 0 && id < t.Count
            ? (_totalsLogical[id], _totalsAllocated[id]) : null;

    /// <summary>The post-scan summary Overview reads (volume reconciliation, categories, top hits).</summary>
    private AnalysisSnapshot? _snapshot;
    public AnalysisSnapshot? Snapshot { get => _snapshot; private set { if (Set(ref _snapshot, value)) Changed(); } }

    /// <summary>Per-root scan history (one JSON file per root under %LOCALAPPDATA%\DiskMap\History).</summary>
    private readonly StorageHistory _history = new();

    /// <summary>"What grew this week" for the current root, if history allows.</summary>
    public StorageHistory.Comparison? HistoryComparison
    {
        get
        {
            if (RootPath is not { } root || _totalsAllocated.Length == 0) return null;
            var entries = _history.Entries(root);
            var latest = entries.LastOrDefault();
            if (latest is null) return null;
            return StorageHistory.Compare(entries, latest);
        }
    }

    /// <summary>(files, folders) descendant counts per node — "N items" labels.</summary>
    private (int[] Files, int[] Folders) _counts = ([], []);
    public (int[] Files, int[] Folders) Counts => _counts;

    private string? _rootPath;
    public string? RootPath { get => _rootPath; private set { if (Set(ref _rootPath, value)) Changed(); } }

    private int _zoomedNode = 0;
    public int ZoomedNode { get => _zoomedNode; set { if (Set(ref _zoomedNode, value)) Changed(); } }

    /// <summary>
    /// The item the inspector shows — set by row clicks and tile taps.
    /// Falls back to the zoomed folder when nothing is selected.
    /// </summary>
    private int _selectedNode = -1;
    public int SelectedNode
    {
        get => _selectedNode;
        set { if (Set(ref _selectedNode, value)) Changed(); }
    }

    /// <summary>Inspector target: the selection, else the zoomed folder.</summary>
    public int InspectedNode =>
        _selectedNode >= 0 && _selectedNode < (Tree?.Count ?? 0) ? _selectedNode : _zoomedNode;

    private SizeBasis _sizeBasis = Harness.Basis ?? SizeBasis.Allocated;
    public SizeBasis SizeBasis
    {
        get => _sizeBasis;
        set { if (Set(ref _sizeBasis, value)) _ = RerollAsync(); }
    }

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    /// <summary>"walking" while directories list, "summarizing" during the rollup pass.</summary>
    private string _scanPhase = "walking";
    public string ScanPhase { get => _scanPhase; private set => Set(ref _scanPhase, value); }

    private int _scanProgress;
    public int ScanProgress { get => _scanProgress; private set => Set(ref _scanProgress, value); }

    /// <summary>Live scan telemetry — items/s, bytes found, top-level attribution.</summary>
    private ScanEngine.ScanProgress? _scanStats;
    public ScanEngine.ScanProgress? ScanStats { get => _scanStats; private set => Set(ref _scanStats, value); }

    private CancellationTokenSource? _scanCts;

    /// <summary>
    /// Cancels the in-flight scan. The last finished scan's tree stays —
    /// a cancelled walk never publishes a partial tree (PARITY WIN-004).
    /// </summary>
    public void CancelScan() => _scanCts?.Cancel();

    private int _itemCount;
    public int ItemCount { get => _itemCount; private set => Set(ref _itemCount, value); }

    private double _elapsed;
    public double Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }

    private int _notDownloaded;
    public int NotDownloaded { get => _notDownloaded; private set => Set(ref _notDownloaded, value); }

    private string _backend = "";
    public string Backend { get => _backend; private set => Set(ref _backend, value); }

    /// <summary>Why the fast MFT path was skipped ("needs administrator", …); null when it ran or wasn't wanted.</summary>
    private string? _fallbackReason;
    public string? FallbackReason { get => _fallbackReason; private set => Set(ref _fallbackReason, value); }

    /// <summary>A quick update's replayed change count (Backend "usn"); the Overview footer's line.</summary>
    public int ChangedRecords { get; private set; }

    /// <summary>When the scan in flight started — the scanning board's clock.</summary>
    public DateTimeOffset? ScanStartedAt { get; private set; }

    /// <summary>
    /// Directories that refused to list in the last scan — their bytes are
    /// missing from every total above them, so the UI says so rather than
    /// silently under-reporting (PARITY WIN-003).
    /// </summary>
    private IReadOnlyList<int> _deniedDirectories = [];
    public IReadOnlyList<int> DeniedDirectories { get => _deniedDirectories; private set { if (Set(ref _deniedDirectories, value)) Changed(); } }

    /// <summary>Directories that failed to list for other reasons (raced deletes etc.) — counted, not listed.</summary>
    private int _failedDirectoryCount;
    public int FailedDirectoryCount { get => _failedDirectoryCount; private set => Set(ref _failedDirectoryCount, value); }

    /// <summary>Capacity of the scanned volume — the sidebar drive card.</summary>
    private VolumeInfo? _volume;
    public VolumeInfo? Volume { get => _volume; private set { if (Set(ref _volume, value)) Changed(); } }

    private SpaceAccount? _spaceAccount;
    /// <summary>"Where's the rest?" — the drive's used space in parts that add up (WIN-113). Nil until read.</summary>
    public SpaceAccount? SpaceAccount { get => _spaceAccount; private set { if (Set(ref _spaceAccount, value)) Changed(); } }

    private async Task ReadSpaceAccountAsync(string path, long scannedOnDiskBytes, int unreadable)
    {
        var volume = Volume;
        var account = await Task.Run(() => Harness.SpaceAccountFor(volume, path, scannedOnDiskBytes, unreadable));
        if (RootPath == path) SpaceAccount = account;
    }

    private IReadOnlyList<SystemStorageCatalog.Item>? _systemStorage;
    /// <summary>"What Windows keeps" (WIN-116, P2; Mac: systemStorage). Null until measured.</summary>
    public IReadOnlyList<SystemStorageCatalog.Item>? SystemStorage { get => _systemStorage; private set { if (Set(ref _systemStorage, value)) Changed(); } }

    /// <summary>
    /// Measures "What Windows keeps" off the UI thread: from this scan where it covers the path
    /// (allocated sizes), else by a bounded walk of the path itself. Kept only while the scan
    /// it was measured for is still the current one.
    /// </summary>
    private async Task ReadSystemStorageAsync(string path, FileTree tree, long[] allocated, IReadOnlyList<int> denied)
    {
        var items = await Task.Run(() => Harness.SystemStorageFor(tree, path, allocated, denied));
        if (RootPath == path && ReferenceEquals(Tree, tree)) SystemStorage = items;
    }

    /// <summary>WIN-066: non-null when the opt-in ReFS block-clone pass ran on the last scan.</summary>
    public BlockClones.Report? CloneProfile { get; private set; }

    /// <summary>What clone dedup removed from the allocated totals, if the tree was profiled.</summary>
    public FileTree.SharingCorrection CloneCorrection =>
        Tree?.GetSharingCorrection() ?? FileTree.SharingCorrection.None;

    /// <summary>Which chart mode the Visualize page shows.</summary>
    private string _visualizeMode = "Treemap";
    public string VisualizeMode { get => _visualizeMode; set { if (Set(ref _visualizeMode, value)) Changed(); } }

    /// <summary>The top-bar search box's committed query.</summary>
    /// <summary>WIN-048: chart coloring — "type" (dominant file kind), "age" (forgotten-band), "folder" (per-sibling hue).</summary>
    private string _coloringMode = "type";
    public string ColoringMode { get => _coloringMode; set { if (Set(ref _coloringMode, value)) Changed(); } }

    /// <summary>WIN-048: slice-collapse threshold — the depth slider's value (fraction of the parent, default 0.5%).</summary>
    private double _chartDepth = ChartLayout.OtherFraction;
    public double ChartDepth { get => _chartDepth; set { if (Set(ref _chartDepth, value)) Changed(); } }

    private string _searchQuery = "";
    public string SearchQuery { get => _searchQuery; set { if (Set(ref _searchQuery, value)) Changed(); } }

    private List<DuplicateGroup> _duplicates = [];
    public List<DuplicateGroup> Duplicates { get => _duplicates; set { if (Set(ref _duplicates, value)) Changed(); } }

    /// <summary>True once the duplicate finder has run on this scan.</summary>
    private bool _duplicatesRan;

    /// <summary>
    /// Ids of files inside a duplicate group — null until a duplicate
    /// search actually ran, so queries can say "not searched yet" instead
    /// of matching nothing.
    /// </summary>
    public HashSet<int>? DuplicateFileIDs =>
        _duplicatesRan ? Duplicates.SelectMany(g => g.FileIDs).ToHashSet() : null;

    /// <summary>The duplicate finder completed (possibly finding nothing).</summary>
    public void MarkDuplicatesSearched() { _duplicatesRan = true; Changed(); }

    /// <summary>True once the duplicate finder has run on this scan (the sidebar's figure).</summary>
    public bool DuplicatesSearched => _duplicatesRan;

    /// <summary>When the current scan finished — the page bar's "Scanned 2 minutes ago".</summary>
    private DateTimeOffset? _lastScanFinishedAt;
    public DateTimeOffset? LastScanFinishedAt { get => _lastScanFinishedAt; private set => Set(ref _lastScanFinishedAt, value); }

    /// <summary>Quick-wins hits, built once per scan (sidebar pages share it).</summary>
    private List<QuickWins.Hit>? _quickWins;
    public List<QuickWins.Hit>? QuickWins { get => _quickWins; set { if (Set(ref _quickWins, value)) Changed(); } }

    /// <summary>
    /// What Clean up and Overview offer — app caches grouped per app, then quick-win folders —
    /// measured on the current size basis. Null until a scan is summarized (Mac: cachedReviewables).
    /// </summary>
    private List<ReviewableTarget>? _reviewables;
    public List<ReviewableTarget>? Reviewables { get => _reviewables; private set { if (Set(ref _reviewables, value)) Changed(); } }

    /// <summary>Files in Downloads, oldest first in spirit (Mac: cachedOldDownloads). Null until summarized.</summary>
    private OldDownloadsCatalogResult? _oldDownloads;
    public OldDownloadsCatalogResult? OldDownloads { get => _oldDownloads; private set { if (Set(ref _oldDownloads, value)) Changed(); } }

    /// <summary>Big videos, photos, audio and media projects (Mac: cachedLargeMedia). Null until summarized.</summary>
    private MediaCatalogResult? _largeMedia;
    public MediaCatalogResult? LargeMedia { get => _largeMedia; private set { if (Set(ref _largeMedia, value)) Changed(); } }

    /// <summary>The scan's catalogs on one basis: Clean up, Overview and the review pages read these.</summary>
    private static (List<ReviewableTarget>, OldDownloadsCatalogResult, MediaCatalogResult) BuildCatalogs(
        FileTree tree, string root, long[] totals, IReadOnlyList<QuickWins.Hit> quickWins) =>
        (ReviewableCatalog.Build(tree, root, totals, quickWins).Targets,
         OldDownloadsCatalog.Build(tree, root, totals),
         MediaCatalog.Build(tree, root, totals));

    /// <summary>What Clean up ticks for you: the targets that rebuild by themselves.</summary>
    public long SafeReclaimableBytes => Reviewables?.Where(t => t.IsGenerallySafe).Sum(t => t.Bytes) ?? 0;

    public List<ReviewableTarget> SafeReclaimables =>
        Reviewables?.Where(t => t.IsGenerallySafe).OrderByDescending(t => t.Bytes).ToList() ?? [];

    /// <summary>Build output and dependencies that come back with a rebuild or reinstall, left unticked.</summary>
    public long ReviewFirstReclaimableBytes => Reviewables?.Where(t => t.IsReviewFirst).Sum(t => t.Bytes) ?? 0;

    /// <summary>Overview's line when nothing is ticked for you.</summary>
    public string NothingTickedLine
    {
        get
        {
            long review = ReviewFirstReclaimableBytes;
            return review > 0
                ? $"Nothing is ticked for you here. {ByteFormat.Format(review)} of build files and dependencies comes back with a rebuild or reinstall — check it in Clean up."
                : "Nothing here rebuilds by itself right now. Clean up still lists old downloads and big files to review.";
        }
    }

    // ---- WIN-052: chart multi-selection ----

    /// <summary>
    /// Ctrl+click toggles a node in this set; plain click selects the one
    /// (and clears this). Esc clears. Exposed as the shared chart-select
    /// set every chart control reads.
    /// </summary>
    public HashSet<int> MultiSelection { get; } = [];

    public void ToggleMulti(int nodeId)
    {
        if (!MultiSelection.Remove(nodeId)) MultiSelection.Add(nodeId);
        Changed();
    }

    public void ClearMulti()
    {
        if (MultiSelection.Count == 0) return;
        MultiSelection.Clear();
        Changed();
    }

    /// <summary>
    /// Bytes across the selection counting a folder and its contents once
    /// — a selected dir's selected descendants don't double up.
    /// </summary>
    public long MultiSelectionBytes()
    {
        if (Tree is null || MultiSelection.Count == 0) return 0;
        long bytes = 0;
        foreach (int id in MultiSelection)
        {
            bool covered = false;
            for (int p = Tree.Parent[id]; p > 0; p = Tree.Parent[p])
                if (MultiSelection.Contains(p)) { covered = true; break; }
            if (!covered && id < _totals.Length) bytes += _totals[id];
        }
        return bytes;
    }

    public CleanupQueue Cleanup { get; } = new();

    public ObservableCollection<CleanupQueue.StagedItem> StagedItems { get; } = [];

    /// <summary>Latest reclaim figure — recomputed by the queue's own measurements.</summary>
    private CleanupQueue.ReclaimEstimate _reclaimEstimate = CleanupQueue.ReclaimEstimate.Empty;
    public CleanupQueue.ReclaimEstimate ReclaimEstimate { get => _reclaimEstimate; private set => Set(ref _reclaimEstimate, value); }

    /// <summary>Pages re-render on this — tree, totals, zoom, basis all funnel through it.</summary>
    public event EventHandler? StateChanged;
    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);

    private (FileTree Tree, long[] Totals, DeveloperCatalogResult Result)? _developer;

    /// <summary>
    /// The Developer catalog for the current scan and size basis, built in the background after
    /// each scan (Mac: cachedDeveloper, prewarmed so the sidebar can show its total). Nil until ready.
    /// </summary>
    public DeveloperCatalogResult? Developer =>
        _developer is { } d && ReferenceEquals(d.Tree, _tree) && ReferenceEquals(d.Totals, _totals) ? d.Result : null;

    private async void PrewarmDeveloper()
    {
        if (_tree is not { } tree || _rootPath is not { } root || _totals.Length != tree.Count) return;
        var totals = _totals;
        if (_developer is { } d && ReferenceEquals(d.Tree, tree) && ReferenceEquals(d.Totals, totals)) return;
        try
        {
            var result = await Task.Run(() => DeveloperCatalog.Build(tree, root, totals));
            if (!ReferenceEquals(tree, _tree) || !ReferenceEquals(totals, _totals)) return;
            _developer = (tree, totals, result);
            Changed();
        }
        catch { /* the page builds its own when opened */ }
    }

    /// <summary>Raised by pages/inspector to switch the main navigation (e.g. "View in File Browser").</summary>
    public event Action<string>? PageRequested;
    public void ShowPage(string name) => PageRequested?.Invoke(name);

    /// <summary>Raised to open the basket (Mac: isCleanupQueuePresented); the window shows it.</summary>
    public event Action? BasketRequested;
    public void OpenBasket() => BasketRequested?.Invoke();

    /// <summary>Raised when a page wants a folder picked (it can't show the dialog itself).</summary>
    public event Action? ScanRequested;
    public void RequestScan() => ScanRequested?.Invoke();

    /// <summary>Raised to reopen "what do you do / what do you use" (the window owns the dialog).</summary>
    public event Action? OnboardingRequested;
    public void RequestOnboarding() => OnboardingRequested?.Invoke();

    /// <summary>Drill + navigate to Visualize, used by "Visualize this folder".</summary>
    public void Visualize(int nodeId)
    {
        DrillTo(nodeId);
        VisualizeMode = "Treemap";
        ShowPage("Visualize");
    }

    private async Task RerollAsync()
    {
        if (_tree is not { } tree || _rootPath is not { } root) return;
        var basis = _sizeBasis;
        var snapshot = await Task.Run(() =>
        {
            var totals = basis == SizeBasis.Logical ? _totalsLogical : _totalsAllocated;
            return (AnalysisSnapshot.Build(tree, root, _totalsLogical, _totalsAllocated, basis, QuickWins, volumeOf: Harness.VolumeFor),
                BuildCatalogs(tree, root, totals, QuickWins ?? []));
        });
        // A newer toggle or scan may have landed while this one ran.
        if (ReferenceEquals(tree, _tree) && basis == _sizeBasis)
        {
            Totals = basis == SizeBasis.Logical ? _totalsLogical : _totalsAllocated;
            PrewarmDeveloper();
            Snapshot = snapshot.Item1;
            (Reviewables, OldDownloads, LargeMedia) = snapshot.Item2;
        }
    }

    public async Task ScanAsync(string path)
    {
        if (IsScanning) return;
        ScanStartedAt = DateTimeOffset.Now;
        IsScanning = true;
        ScanPhase = "walking";
        ScanProgress = 0;
        ScanStats = null;
        using var cts = _scanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ScanEngine.ScanProgress>(p =>
            {
                ScanProgress = p.Items;
                ScanStats = p;
            });
            var basis = _sizeBasis;
            var settings = AppSettings.Load();
            var scanned = await Task.Run(
                () => _engine.ScanAsync(path, progress, cts.Token, settings.CloneAccounting),
                cts.Token);
            ScanPhase = "summarizing";
            var (logical, allocated, counts, quickWins, snapshot, reviewables) = await Task.Run(() =>
            {
                var l = scanned.Tree.RollUpSizes(SizeBasis.Logical);
                // The demo PC's files are sparse (they take no space), so deterministic renders
                // count them at their logical size on disk too, as the Mac's real demo files are.
                var a = Harness.Deterministic ? l : scanned.Tree.RollUpSizes(SizeBasis.Allocated);
                var c = scanned.Tree.RollUpCounts();
                var qw = Core.QuickWins.Find(scanned.Tree, path, Core.QuickWins.BundledPatterns());
                var snap = AnalysisSnapshot.Build(scanned.Tree, path, l, a, basis, qw, volumeOf: Harness.VolumeFor);
                var rv = BuildCatalogs(scanned.Tree, path, basis == SizeBasis.Logical ? l : a, qw);
                return (l, a, c, qw, snap, rv);
            }, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            // History record — one JSON line per scan, feeds "what grew".
            var volume = Harness.VolumeFor(path);
            // A render-harness run never writes the user's scan history.
            if (settings.KeepHistory && !Harness.IsActive)
            {
                var folders = StorageHistory.Folders(scanned.Tree, allocated);
                _history.Record(new StorageHistory.Entry(
                    DateTimeOffset.Now,
                    volume?.FreeBytes ?? 0,
                    volume?.TotalBytes ?? 0,
                    allocated.Length > 0 ? allocated[0] : 0,
                    scanned.FailedDirectoryCount,
                    scanned.Tree.Sharing.IsEmpty
                        ? StorageHistory.Entry.SharingModeHardLinkDedup
                        : StorageHistory.Entry.SharingModeBlockCloneDedup,
                    folders), path);
            }
            ItemCount = scanned.ItemCount;
            Elapsed = scanned.ElapsedSeconds;
            ChangedRecords = scanned.ChangedRecords;
            NotDownloaded = scanned.NotDownloadedCount;
            Backend = scanned.Backend;
            FallbackReason = scanned.FallbackReason;
            DeniedDirectories = scanned.DeniedDirectoryIds;
            FailedDirectoryCount = scanned.FailedDirectoryCount;
            Duplicates = [];
            _duplicatesRan = false;
            QuickWins = quickWins;
            (Reviewables, OldDownloads, LargeMedia) = reviewables;
            Snapshot = snapshot;
            // Totals before Tree: a view refreshed by the Tree change must
            // never pair the new tree with the old totals.
            _totalsLogical = logical;
            _totalsAllocated = allocated;
            _totals = basis == SizeBasis.Logical ? logical : allocated;
            _counts = counts;
            _zoomedNode = 0;
            _selectedNode = -1;
            MultiSelection.Clear();   // ids from the old tree name other files in the new one
            _zoomBack.Clear();
            _zoomForward.Clear();
            CloneProfile = scanned.CloneProfile;
            RootPath = path;
            Volume = Harness.VolumeFor(path);
            // The harness pins "Scanned 2 minutes ago", as the Mac's --deterministic does.
            LastScanFinishedAt = Harness.Deterministic ? DateTimeOffset.Now.AddSeconds(-120) : DateTimeOffset.Now;
            SpaceAccount = null;
            SystemStorage = null;
            Tree = scanned.Tree;
            PrewarmDeveloper();
            _ = ReadSpaceAccountAsync(path, snapshot.ScannedOnDiskBytes, scanned.DeniedDirectoryIds.Count);
            _ = ReadSystemStorageAsync(path, scanned.Tree, allocated, scanned.DeniedDirectoryIds);
            // WIN-013: let staging measure from this tree when the journal
            // proves a staged folder's subtree is unchanged since the scan.
            Cleanup.SetScanContext(scanned.Tree, path, scanned.ScanMarker,
                scanned.DeniedDirectoryIds);
            if (basis != _sizeBasis) await RerollAsync();
        }
        catch (OperationCanceledException)
        {
            // Keep the previous scan — a cancelled walk publishes nothing.
        }
        finally
        {
            IsScanning = false;
            ScanPhase = "walking";
            _scanCts = null;
        }
    }

    /// <summary>Rescan the current root — the top bar's button.</summary>
    public async Task RescanAsync()
    {
        if (RootPath is { } path) await ScanAsync(path);
    }

    /// <summary>Adopt a loaded snapshot as the current scan — no rescan.</summary>
    public async Task LoadSnapshot(DiskSnapshot snapshot)
    {
        var basis = _sizeBasis;
        var (logical, allocated, counts, quickWins, snap, reviewables) = await Task.Run(() =>
        {
            var l = snapshot.Tree.RollUpSizes(SizeBasis.Logical);
            var a = Harness.Deterministic ? l : snapshot.Tree.RollUpSizes(SizeBasis.Allocated);
            var c = snapshot.Tree.RollUpCounts();
            var qw = Core.QuickWins.Find(snapshot.Tree, snapshot.RootPath, Core.QuickWins.BundledPatterns());
            var s = AnalysisSnapshot.Build(snapshot.Tree, snapshot.RootPath, l, a, basis, qw, volumeOf: Harness.VolumeFor);
            var rv = BuildCatalogs(snapshot.Tree, snapshot.RootPath, basis == SizeBasis.Logical ? l : a, qw);
            return (l, a, c, qw, s, rv);
        });
        ItemCount = snapshot.Tree.Count;
        Elapsed = 0;
        NotDownloaded = 0;
        Backend = "snapshot";
        FallbackReason = null;
        DeniedDirectories = [];
        FailedDirectoryCount = 0;
        CloneProfile = null;
        Duplicates = [];
        _duplicatesRan = false;
        QuickWins = quickWins;
        (Reviewables, OldDownloads, LargeMedia) = reviewables;
        Snapshot = snap;
        _totalsLogical = logical;
        _totalsAllocated = allocated;
        _totals = basis == SizeBasis.Logical ? logical : allocated;
        _counts = counts;
        _zoomedNode = 0;
        _selectedNode = -1;
        MultiSelection.Clear();
        _zoomBack.Clear();
        _zoomForward.Clear();
        RootPath = snapshot.RootPath;
        Volume = Harness.VolumeFor(snapshot.RootPath);
        SpaceAccount = null;
        SystemStorage = null;
        Tree = snapshot.Tree;
        PrewarmDeveloper();
        _ = ReadSpaceAccountAsync(snapshot.RootPath, snap.ScannedOnDiskBytes, 0);
        _ = ReadSystemStorageAsync(snapshot.RootPath, snapshot.Tree, allocated, []);
    }

    // Zoom navigation history for the breadcrumb ‹ › buttons.
    private readonly List<int> _zoomBack = [];
    private readonly List<int> _zoomForward = [];

    public void DrillTo(int nodeId)
    {
        if (Tree is not null && nodeId >= 0 && nodeId < Tree.Count && Tree.IsDirectory[nodeId]
            && nodeId != ZoomedNode)
        {
            _zoomBack.Add(ZoomedNode);
            _zoomForward.Clear();
            ZoomedNode = nodeId;
        }
    }

    public void DrillToAncestor(int nodeId)
    {
        if (Tree is not null && nodeId >= 0 && nodeId < Tree.Count)
            ZoomedNode = nodeId;
    }

    public bool CanGoBack => _zoomBack.Count > 0;
    public bool CanGoForward => _zoomForward.Count > 0;

    public void GoBack()
    {
        if (_zoomBack.Count == 0) return;
        _zoomForward.Add(ZoomedNode);
        ZoomedNode = _zoomBack[^1];
        _zoomBack.RemoveAt(_zoomBack.Count - 1);
    }

    public void GoForward()
    {
        if (_zoomForward.Count == 0) return;
        _zoomBack.Add(ZoomedNode);
        ZoomedNode = _zoomForward[^1];
        _zoomForward.RemoveAt(_zoomForward.Count - 1);
    }

    public void Select(int nodeId)
    {
        if (Tree is not null && nodeId >= 0 && nodeId < Tree.Count)
            SelectedNode = nodeId;
    }

    public List<int> Breadcrumbs() =>
        Tree is null ? [] : Tree.AncestorIds(ZoomedNode);

    public string PathOf(int nodeId) =>
        Tree is null || RootPath is null ? "" : Tree.PathOf(nodeId, RootPath);

    /// <summary>"~"-style display path: the scanned root shown as its name.</summary>
    public string DisplayPath(int nodeId)
    {
        string path = PathOf(nodeId);
        if (RootPath is { } root && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string rest = path[root.Length..].TrimStart('\\', '/');
            string name = Tree!.NameOf(0);
            return rest.Length == 0 ? name : $"{name}/{rest}";
        }
        return path;
    }

    /// <summary>Transient confirmations — stage/commit/put-back (WIN-063).</summary>
    public event Action<string>? ToastRequested;
    public void Toast(string text) => ToastRequested?.Invoke(text);
    public void ToastAdded(int count) => Toast(count == 1
        ? "Added 1 item to the basket — Ctrl+Shift+Delete to review"
        : $"Added {count:N0} items to the basket — Ctrl+Shift+Delete to review");

    /// <summary>What this node is and what removing it means (WSL disk, app, cache…).</summary>
    public StorageVerdict VerdictOf(int nodeId) =>
        Tree is null || nodeId < 0 || nodeId >= Tree.Count
            ? new StorageVerdict(StorageClassifier.Other, CleanupAdvice.Fine)
            : StorageClassifier.Classify(PathOf(nodeId), Tree.IsDirectory[nodeId]);

    /// <summary>
    /// Asked before a single risky item (a WSL distro, Docker's disk, an
    /// installed app…) is staged; true = stage anyway. Set by the window —
    /// null means refuse, the safe default.
    /// </summary>
    public Func<string, StorageVerdict, bool>? ConfirmRisky { get; set; }

    /// <summary>
    /// Stages one node. Windows-managed items are refused with the right
    /// alternative; risky ones ask first when <paramref name="notify"/> is
    /// set (an interactive single add) and are refused in silent bulk use —
    /// <see cref="StageMany"/> reports those.
    /// </summary>
    public bool Stage(int nodeId, string reason, string? group = null, int groupCount = 1,
        bool notify = true)
    {
        // The scanned root itself is never a cleanup item — only what's in it.
        if (Tree is null || nodeId <= 0 || nodeId >= Tree.Count) return false;
        var verdict = VerdictOf(nodeId);
        if (verdict.Advice == CleanupAdvice.Never)
        {
            if (notify)
                Toast($"{Tree.NameOf(nodeId)} is protected — {verdict.Instead ?? verdict.Note ?? "it can't be cleaned up here"}");
            return false;
        }
        if (verdict.Advice == CleanupAdvice.Warn
            && !(notify && ConfirmRisky?.Invoke(Tree.NameOf(nodeId), verdict) == true))
            return false;
        string path = PathOf(nodeId);
        long size = nodeId < _totals.Length ? _totals[nodeId] : Tree.LogicalSize[nodeId];
        bool ok = Cleanup.Stage(path, size, reason, group, groupCount);
        if (ok)
        {
            RefreshStaged();
            if (notify) ToastAdded(1);
        }
        return ok;
    }

    /// <summary>
    /// The one bulk-add path behind every "Add Selected to Cleanup": stages
    /// what's safe to stage, skips risky and Windows-managed items (they
    /// need a deliberate single add, with the warning), and says so in one
    /// toast. Returns how many were added.
    /// </summary>
    public int StageMany(IEnumerable<int> nodeIds, string reason) =>
        StageRequests(nodeIds.Select(id => (id, reason)));

    /// <summary><see cref="StageMany"/> with a reason per item ("Developer: node_modules").</summary>
    private int StageRequests(IEnumerable<(int NodeId, string Reason)> requests)
    {
        if (Tree is null) return 0;
        int added = 0, already = 0;
        var skipped = new List<string>();
        foreach (var (id, reason) in requests)
        {
            if (id < 0 || id >= Tree.Count) continue;
            var advice = VerdictOf(id).Advice;
            if (advice is CleanupAdvice.Warn or CleanupAdvice.Never) { skipped.Add(Tree.NameOf(id)); continue; }
            if (Stage(id, reason, notify: false)) added++;
            else already++;
        }
        string addedText = added == 1 ? "Added 1 item" : $"Added {added:N0} items";
        if (skipped.Count > 0)
        {
            string names = string.Join(", ", skipped.Take(2)) + (skipped.Count > 2 ? $" +{skipped.Count - 2}" : "");
            string risky = $"{skipped.Count} risky item{(skipped.Count == 1 ? "" : "s")}";
            Toast(added > 0
                ? $"{addedText} · skipped {risky} ({names})"
                : $"{risky} — add {(skipped.Count == 1 ? "it" : "them")} one at a time to see why");
        }
        else if (added > 0)
        {
            ToastAdded(added);
        }
        else if (already > 0)
        {
            Toast("Already in the basket");
        }
        return added;
    }

    /// <summary>
    /// Shows the bulk confirmation (every item by full path); true = go.
    /// Set by the window; null means refuse — the safe default.
    /// </summary>
    public Func<string, string, IReadOnlyList<Dialogs.ConfirmItem>, string?, bool>? ConfirmBulk { get; set; }

    /// <summary>
    /// <see cref="StageMany"/> behind a confirmation that lists exactly what
    /// will be added — folder by folder, with full paths and sizes.
    /// </summary>
    public int ConfirmStageMany(IEnumerable<int> nodeIds, string reason, string title) =>
        ConfirmStageRequests(nodeIds.Select(id => (id, reason)).ToList(), title);

    /// <summary>
    /// <see cref="ConfirmStageMany"/> with a reason per item, so each keeps its own name in
    /// the basket (Mac: confirmStageMany with CleanupStageRequest reasons).
    /// </summary>
    public int ConfirmStageRequests(IReadOnlyList<(int NodeId, string Reason)> requests, string title)
    {
        if (Tree is null) return 0;
        var seen = new HashSet<int>();
        var unique = requests.Where(r => r.NodeId >= 0 && r.NodeId < Tree.Count && seen.Add(r.NodeId)).ToList();
        var ids = unique.Select(r => r.NodeId).ToList();
        if (ids.Count == 0) return 0;
        var safe = ids.Where(id => VerdictOf(id).Advice is not (CleanupAdvice.Warn or CleanupAdvice.Never)).ToList();
        int risky = ids.Count - safe.Count;
        var items = safe.Select(id => new Dialogs.ConfirmItem(Tree.NameOf(id), PathOf(id),
            id < _totals.Length ? _totals[id] : 0)).ToList();
        if (items.Count > 0)
        {
            var riskyNames = ids.Where(id => VerdictOf(id).Advice is CleanupAdvice.Warn or CleanupAdvice.Never)
                .Take(3).Select(id => Tree.NameOf(id)).ToList();
            string? footnote = risky > 0
                ? $"{risky} risky item{(risky == 1 ? "" : "s")} ({string.Join(", ", riskyNames)}{(risky > 3 ? "…" : "")}) won’t be added — add {(risky == 1 ? "it" : "them")} one at a time to see why."
                : null;
            // The Mac's sheet titles itself by count; the caller's title names the group in the toast.
            bool ok = ConfirmBulk?.Invoke($"Add {items.Count:N0} item{(items.Count == 1 ? "" : "s")} to the basket?",
                "Nothing is deleted yet — you review the basket and confirm again before anything moves to the Recycle Bin.",
                items, footnote) == true;
            if (!ok) return 0;
        }
        return StageRequests(unique);
    }

    /// <summary>Stage a raw path (leftovers, staged folders).</summary>
    public bool StagePath(string path, long size, string reason, bool notify = true)
    {
        bool ok = Cleanup.Stage(path, size, reason);
        if (ok)
        {
            RefreshStaged();
            if (notify) ToastAdded(1);
        }
        return ok;
    }

    /// <summary>True when this exact path is in the basket.</summary>
    public bool IsStaged(string path) =>
        StagedItems.Any(s => string.Equals(System.IO.Path.GetFullPath(s.Path).TrimEnd('\\'),
            System.IO.Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Put Back the last cleanup from the Recycle Bin (Mac: putBackLastCleanup). Restoring is
    /// never destructive: anything already back, or whose place is taken, is left alone and
    /// kept in the record for a later try.
    /// </summary>
    public async Task<PutBackReport?> PutBackLastCleanupAsync()
    {
        if (CleanupRecord.Load() is not { Items.Count: > 0 } record) return null;
        var report = await Task.Run(() => PutBack.Run(record));
        var left = record.Items.Where(i => report.Restored.All(r => r.OriginalPath != i.OriginalPath)).ToList();
        new CleanupRecord(record.Date, left).Save();
        Toast($"Put back {report.Restored.Count} item{(report.Restored.Count == 1 ? "" : "s")}"
            + (report.Skipped.Count > 0 ? $" · {report.Skipped.Count} skipped ({report.Skipped[0].Reason})" : ""));
        RefreshStaged();
        Changed();
        return report;
    }

    public void Unstage(Guid id)
    {
        Cleanup.Unstage(id);
        RefreshStaged();
    }

    /// <summary>Fired when the staged set changes — chrome badges listen on this.</summary>
    public event EventHandler? StagedChanged;

    /// <summary>
    /// Staging touches only the queue, never the tree, so it doesn't raise
    /// StateChanged — that used to rebuild every page on every Stage click.
    /// Chrome that shows the staged count subscribes to StagedChanged.
    /// </summary>
    public void RefreshStaged()
    {
        StagedItems.Clear();
        foreach (var item in Cleanup.AllItems()) StagedItems.Add(item);
        ReclaimEstimate = Cleanup.Estimate();
        StagedChanged?.Invoke(this, EventArgs.Empty);
    }

}
