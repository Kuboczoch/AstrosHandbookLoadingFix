using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AstrosHandbookLoadingFix;

/// <summary>Publishes accepted pages on the GUI thread; producer work stays off it.</summary>
public sealed class HandbookPageLoader
{
    private const int MaxBatchSize = 256;
    private const double HandOverIntervalMs = 200;
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, List<GuiHandbookPage>> AllPages =
        AccessTools.FieldRefAccess<GuiDialogHandbook, List<GuiHandbookPage>>("allHandbookPages");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, Dictionary<string, int>> PageNumbers =
        AccessTools.FieldRefAccess<GuiDialogHandbook, Dictionary<string, int>>("pageNumberByPageCode");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, bool> Loading =
        AccessTools.FieldRefAccess<GuiDialogHandbook, bool>("loadingPagesAsync");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, GuiComposer> Overview =
        AccessTools.FieldRefAccess<GuiDialogHandbook, GuiComposer>("overviewGui");
    private static readonly ConditionalWeakTable<GuiDialogHandbook, LoadState> States = new();
    [ThreadStatic] private static HandbookPageLoader? current;

    private sealed class LoadState
    {
        public int Generation;
        public volatile bool Disposed;
        public bool InProgress;
        public bool Failed;
        public readonly object ProducerLock = new();
    }

    private readonly GuiDialogHandbook handbook;
    private readonly ICoreClientAPI capi;
    private readonly LoadState state;
    private readonly int generation;
    private readonly object gate = new();
    private readonly Queue<GuiHandbookPage> pending = new();
    private readonly List<GuiHandbookPage> published = [];
    private List<GuiHandbookPage>? result;
    private bool finished;
    private bool failed;
    private volatile bool abandoned;
    private bool drainQueued;
    private long lastHandOver = Stopwatch.GetTimestamp();
    private int customPageCount = -1;

    private HandbookPageLoader(GuiDialogHandbook handbook, ICoreClientAPI capi, LoadState state, int generation)
    {
        this.handbook = handbook;
        this.capi = capi;
        this.state = state;
        this.generation = generation;
    }

    public static void Restart(GuiDialogHandbook handbook)
    {
        var state = States.GetOrCreateValue(handbook);
        Interlocked.Increment(ref state.Generation);
        state.InProgress = true;
        state.Failed = false;
    }

    /// <summary>Capture ownership before queueing, not when the worker eventually starts.</summary>
    public static void Schedule(Action action, string name)
    {
        if (action.Target is not GuiDialogHandbook handbook)
            throw new InvalidOperationException("Unexpected handbook worker target.");
        var state = States.GetOrCreateValue(handbook);
        int generation = Volatile.Read(ref state.Generation);
        TyronThreadPool.QueueTask(() =>
        {
            // Reloads must not concurrently mutate recipe caches belonging to this dialog.
            lock (state.ProducerLock)
            {
                if (state.Disposed || generation != Volatile.Read(ref state.Generation)) return;
                var api = (ICoreClientAPI)AccessTools.Field(typeof(GuiDialog), "capi").GetValue(handbook)!;
                var producer = (OnCreatePagesDelegate)AccessTools.Field(typeof(GuiDialogHandbook), "createPageHandlerAsync").GetValue(handbook)!;
                Run(handbook, api, producer, state, generation);
            }
        }, name);
    }

    /// <summary>Custom guides already exist synchronously. Make them usable immediately.</summary>
    public static void GuidesReady(GuiDialogHandbook handbook)
    {
        NumberAllPages(handbook);
        Loading(handbook) = false;
        UpdateStatus(handbook);
        RefreshOverview(handbook);
    }

    public static void Dispose(GuiDialogHandbook handbook)
    {
        var state = States.GetOrCreateValue(handbook);
        state.Disposed = true;
        Interlocked.Increment(ref state.Generation);
    }

    public static void DisposeAll()
    {
        foreach (var entry in States) Dispose(entry.Key);
    }

    private static void Run(GuiDialogHandbook handbook, ICoreClientAPI api, OnCreatePagesDelegate makePages,
        LoadState state, int generation)
    {
        var loader = new HandbookPageLoader(handbook, api, state, generation);
        var previous = current;
        current = loader;
        try { loader.result = makePages(); }
        catch (OperationCanceledException) when (loader.IsOutdated() || loader.abandoned)
        {
            // Cooperative cancellation at an accepted-page boundary, never mid-constructor.
        }
        catch (Exception error)
        {
            loader.failed = true;
            api.Logger.Error("[Astro's Handbook Loading Fix] Page generation failed; keeping completed pages. {0}", error);
        }
        finally
        {
            current = previous;
            lock (loader.gate)
            {
                loader.finished = true;
                loader.QueueDrain();
            }
        }
    }

    /// <summary>Called only after the producer has fully initialized and accepted a page.</summary>
    public static void AddAcceptedPage(List<GuiHandbookPage> pages, GuiHandbookPage page)
    {
        pages.Add(page);
        var loader = current;
        if (loader == null) return;
        if (loader.IsOutdated() || loader.abandoned) throw new OperationCanceledException("Handbook load superseded or disposed.");
        lock (loader.gate)
        {
            loader.pending.Enqueue(page);
            if (loader.pending.Count >= MaxBatchSize || Stopwatch.GetElapsedTime(loader.lastHandOver).TotalMilliseconds >= HandOverIntervalMs)
                loader.QueueDrain();
        }
    }

    // Caller holds gate. Coalescing prevents a backlog of main-thread callbacks.
    private void QueueDrain()
    {
        if (drainQueued) return;
        drainQueued = true;
        lastHandOver = Stopwatch.GetTimestamp();
        capi.Event.EnqueueMainThreadTask(Drain, "astroshandbookloadingfix-pages");
    }

    private void Drain()
    {
        lock (gate)
        {
            drainQueued = false;
            if (IsOutdated() || abandoned) { pending.Clear(); result = null; return; }
            try
            {
                var allPages = AllPages(handbook);
                if (customPageCount < 0) customPageCount = allPages.Count;
                int count = Math.Min(MaxBatchSize, pending.Count);
                for (int i = 0; i < count; i++)
                {
                    var page = pending.Peek();
                    var code = page.PageCode ?? throw new InvalidOperationException("Handbook page returned a null code.");
                    pending.Dequeue();
                    page.PageNumber = allPages.Count;
                    PageNumbers(handbook)[code] = page.PageNumber;
                    allPages.Add(page);
                    published.Add(page);
                }
                if (finished && pending.Count == 0)
                {
                    bool replace = !failed && result != null && !published.SequenceEqual(result);
                    var finalPages = replace ? allPages.Take(customPageCount).Concat(result!).ToList() : allPages;
                    NumberAllPages(handbook, finalPages); // Validate before replacing the live list/index.
                    if (replace)
                    {
                        allPages.RemoveRange(customPageCount, allPages.Count - customPageCount);
                        allPages.AddRange(result!);
                    }
                    state.InProgress = false;
                    state.Failed = failed;
                    result = null;
                }
                else if (pending.Count > 0)
                {
                    drainQueued = true;
                    capi.Event.RegisterCallback(_ => Drain(), (int)HandOverIntervalMs);
                }
                Loading(handbook) = false;
                UpdateStatus(handbook);
                RefreshOverview(handbook);
            }
            catch (Exception error)
            {
                abandoned = true;
                pending.Clear();
                result = null;
                state.InProgress = false;
                state.Failed = true;
                Loading(handbook) = false;
                capi.Logger.Error("[Astro's Handbook Loading Fix] Publication failed; keeping committed pages. {0}", error);
                try { UpdateStatus(handbook); RefreshOverview(handbook); }
                catch (Exception refreshError) { capi.Logger.Error("[Astro's Handbook Loading Fix] Status/overview refresh failed. {0}", refreshError); }
            }
        }
    }

    private bool IsOutdated() => state.Disposed || capi.IsShuttingDown || generation != Volatile.Read(ref state.Generation);

    private static void NumberAllPages(GuiDialogHandbook handbook, List<GuiHandbookPage>? pages = null)
    {
        pages ??= AllPages(handbook);
        var next = new Dictionary<string, int>();
        for (int i = 0; i < pages.Count; i++)
            next[pages[i].PageCode ?? throw new InvalidOperationException("Handbook page returned a null code.")] = i;
        // A bad getter must not destroy the last successfully committed lookup.
        var numbers = PageNumbers(handbook);
        numbers.Clear();
        foreach (var pair in next) numbers.Add(pair.Key, pair.Value);
        for (int i = 0; i < pages.Count; i++) pages[i].PageNumber = i;
    }

    private static void RefreshOverview(GuiDialogHandbook handbook)
    {
        if (!handbook.IsOpened()) return;
        var scrollbar = Overview(handbook)?.GetScrollbar("scrollbar");
        float offset = scrollbar?.CurrentYPosition ?? 0;
        handbook.FilterItems();
        if (scrollbar == null) return;
        float total = (float)AccessTools.Field(typeof(GuiElementScrollbar), "totalHeight").GetValue(scrollbar)!;
        float visible = (float)AccessTools.Field(typeof(GuiElementScrollbar), "visibleHeight").GetValue(scrollbar)!;
        scrollbar.CurrentYPosition = Math.Clamp(offset, 0, Math.Max(0, total - visible));
        scrollbar.TriggerChanged();
    }

    public static void UpdateStatus(GuiDialogHandbook handbook)
    {
        if (!States.TryGetValue(handbook, out var state) || state.Disposed) return;
        var input = Overview(handbook)?.GetTextInput("searchField");
        if (input == null) return;
        string text = state.Failed ? Lang.Get("astroshandbookloadingfix:failed")
            : state.InProgress ? Lang.Get("astroshandbookloadingfix:loading", AllPages(handbook).Count)
            : Lang.Get("Search...");
        input.SetPlaceHolderText(text);
    }
}
