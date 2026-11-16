using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using AstrosHandbookLoadingFix;

static class Program
{
    static readonly Queue<Action> Workers = new();
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> GuiQueue = new();
    static int assertions;
    static readonly Dictionary<GuiDialogHandbook, Action> OpenRefresh = new();
    static bool IsOpened(GuiDialog __instance, ref bool __result) { if (__instance is GuiDialogHandbook h && OpenRefresh.ContainsKey(h)) { __result = true; return false; } return true; }
    static bool Filter(GuiDialogHandbook __instance) { if (!OpenRefresh.TryGetValue(__instance, out var action)) return true; action(); return false; }
    static bool SkipRecompose() => false;
    static bool StateFlag(GuiDialogHandbook h, string name) { var table = AccessTools.Field(typeof(HandbookPageLoader), "States").GetValue(null)!; object?[] args = [h, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args); return (bool)AccessTools.Field(args[1]!.GetType(), name).GetValue(args[1])!; }
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static void Test(string name, Action test) { Workers.Clear(); GuiQueue.Clear(); test(); Console.WriteLine("PASS " + name); }
    static List<GuiHandbookPage> Pages(GuiDialogHandbook h) => (List<GuiHandbookPage>)AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages").GetValue(h)!;
    static Dictionary<string,int> Index(GuiDialogHandbook h) => (Dictionary<string,int>)AccessTools.Field(typeof(GuiDialogHandbook), "pageNumberByPageCode").GetValue(h)!;
    static void Pump() { while (GuiQueue.TryDequeue(out var action)) action(); }
    static void Produce() => Workers.Dequeue()();
    static bool QueueWorker(Action callback) { Workers.Enqueue(callback); return false; }
    static GuiDialogHandbook New(OnCreatePagesDelegate producer)
    {
        var api = DispatchProxy.Create<ICoreClientAPI, Proxy>();
        var events = DispatchProxy.Create<IClientEventAPI, Proxy>();
        var logger = DispatchProxy.Create<ILogger, Proxy>();
        ((Proxy)(object)events).Handler = (method, args) => { if (method.Name == "EnqueueMainThreadTask") GuiQueue.Enqueue((Action)args![0]!); else if (method.Name == "RegisterCallback") GuiQueue.Enqueue(() => ((Action<float>)args![0]!)(0)); return null; };
        ((Proxy)(object)api).Handler = (method, _) => method.Name switch { "get_Event" => events, "get_Logger" => logger, _ => null };
        var h = (GuiDialogHandbook)RuntimeHelpers.GetUninitializedObject(typeof(GuiDialogHandbook));
        AccessTools.Field(typeof(GuiDialog), "capi").SetValue(h, api);
        AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages").SetValue(h, new List<GuiHandbookPage> { new Page("guide") });
        AccessTools.Field(typeof(GuiDialogHandbook), "pageNumberByPageCode").SetValue(h, new Dictionary<string,int>());
        AccessTools.Field(typeof(GuiDialogHandbook), "createPageHandlerAsync").SetValue(h, producer);
        return h;
    }
    static void Start(GuiDialogHandbook h)
    {
        HandbookPageLoader.Restart(h);
        HandbookPageLoader.Schedule((Action)AccessTools.Method(typeof(GuiDialogHandbook), "LoadPages_Async").CreateDelegate(typeof(Action), h), "regression");
        HandbookPageLoader.GuidesReady(h);
    }
    static List<GuiHandbookPage> Accepted(params GuiHandbookPage[] pages)
    {
        var result = new List<GuiHandbookPage>();
        foreach (var page in pages) HandbookPageLoader.AddAcceptedPage(result, page);
        return result;
    }
    static void GuiQueueEntry()
    {
        var harmony = new Harmony("regression.handbook");
        // Apply every production patch to real game binaries, including strict transpiler matching.
        harmony.PatchAll(typeof(HandbookPageLoader).Assembly);
        harmony.Patch(AccessTools.Method(typeof(Vintagestory.API.Common.TyronThreadPool), "QueueTask", [typeof(Action), typeof(string)]), prefix: new HarmonyMethod(typeof(Program), nameof(QueueWorker)));
        harmony.Patch(AccessTools.Method(typeof(GuiDialog), "IsOpened"), prefix: new HarmonyMethod(typeof(Program), nameof(IsOpened)));
        harmony.Patch(AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems"), prefix: new HarmonyMethod(typeof(Program), nameof(Filter)));
        harmony.Patch(AccessTools.Method(typeof(GuiElementScrollbar), "RecomposeHandle"), prefix: new HarmonyMethod(typeof(Program), nameof(SkipRecompose)));
        Console.WriteLine("Patched real game assemblies: " + typeof(GuiDialogHandbook).Assembly.GetName().Version);
        Test("guides indexed before worker starts", () => {
            var h = New(() => Accepted(new Page("item"))); Start(h);
            Check(Index(h)["guide"] == 0, "guide missing before worker");
            Check(!(bool)AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync").GetValue(h)!, "guides blocked");
            Produce(); Pump(); Check(Index(h)["item"] == 1, "item missing");
        });
        Test("old queued generation skipped", () => {
            int calls = 0; var h = New(() => { calls++; return Accepted(new Page("item")); });
            Start(h); Start(h); Produce(); Produce(); Pump();
            Check(calls == 1 && Pages(h).Count == 2, "old queued producer executed");
        });
        Test("old queued GUI callbacks rejected", () => {
            var h = New(() => Accepted(new Page("item"))); Start(h); Produce();
            Start(h); Pump(); Check(Pages(h).Count == 1, "stale callback published");
            Produce(); Pump(); Check(Pages(h).Count == 2, "current callback lost");
        });
        Test("constructor-only pages never published", () => {
            var h = New(() => { _ = new Page("discarded"); return Accepted(new Page("real")); });
            Start(h); Produce(); Pump(); Check(!Index(h).ContainsKey("discarded") && Pages(h).Count == 2, "discarded page leaked");
        });
        Test("failure before first page finalizes", () => {
            var h = New(() => throw new InvalidOperationException("expected")); Start(h); Produce(); Pump();
            Check(Pages(h).Count == 1 && Index(h).ContainsKey("guide"), "failure lost guides");
        });
        Test("failure keeps completed pages", () => {
            var h = New(() => { Accepted(new Page("ready")); throw new Exception("expected"); });
            Start(h); Produce(); Pump(); Check(Index(h).ContainsKey("ready"), "completed page lost");
        });
        Test("final index reflects mutated page codes", () => {
            var p = new Page("temporary"); var h = New(() => { var r = Accepted(p); p.Code = "final"; return r; });
            Start(h); Produce(); Pump(); Check(Index(h).ContainsKey("final") && !Index(h).ContainsKey("temporary"), "stale key");
        });
        Test("last final page wins collisions", () => {
            var h = New(() => Accepted(new Page("guide"), new Page("guide")));
            Start(h); Produce(); Pump(); Check(Index(h)["guide"] == 2, "wrong collision order");
        });
        Test("untracked custom producers reconcile final list", () => {
            var h = New(() => [new Page("external")]); Start(h); Produce(); Pump();
            Check(Index(h)["external"] == 1, "fallback lost custom page");
        });
        Test("bounded batches and one pending GUI callback", () => {
            var h = New(() => Accepted(Enumerable.Range(0, 1000).Select(i => (GuiHandbookPage)new Page("p"+i)).ToArray()));
            Start(h); Produce(); Check(GuiQueue.Count == 1, "callback backlog"); GuiQueue.TryDequeue(out var tick); tick!();
            Check(Pages(h).Count == 257 && GuiQueue.Count == 1, "batch not bounded"); Pump(); Check(Pages(h).Count == 1001, "pages missing");
        });
        Test("dispose rejects queued worker", () => {
            int calls = 0; var h = New(() => { calls++; return Accepted(new Page("x")); });
            Start(h); HandbookPageLoader.Dispose(h); Produce(); Pump(); Check(calls == 0, "disposed worker ran");
        });
        Test("dispose rejects queued GUI callback", () => {
            var h = New(() => Accepted(new Page("x"))); Start(h); Produce(); HandbookPageLoader.Dispose(h); Pump();
            Check(Pages(h).Count == 1, "disposed dialog mutated");
        });
        Test("final indexing reconciles already-published mutable keys and order", () => {
            var p = new Page("temporary");
            var h = New(() => {
                var r = Accepted(new[] { p }.Concat(Enumerable.Range(0, 300).Select(i => new Page("r"+i))).ToArray());
                Pump(); // Deterministic GUI interleaving before producer returns its final list.
                p.Code = "guide"; r.Reverse(); return r;
            });
            Start(h); Produce(); Pump();
            Check(!Index(h).ContainsKey("temporary") && Index(h)["guide"] == 301 && ReferenceEquals(Pages(h)[301], p), "final order or keys stale");
        });
        Test("cancel stale producer at next accepted page", () => {
            GuiDialogHandbook h = null!; int after = 0;
            h = New(() => {
                HandbookPageLoader.Restart(h);
                Accepted(new Page("stale")); after++; return [];
            });
            Start(h); Produce(); Pump(); Check(after == 0 && Pages(h).Count == 1, "stale producer continued");
        });
        Test("reload producers never overlap", () => {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int active = 0, maxActive = 0, calls = 0;
            var h = New(() => {
                int running = Interlocked.Increment(ref active); Interlocked.Exchange(ref maxActive, Math.Max(maxActive, running));
                if (Interlocked.Increment(ref calls) == 1) { entered.Set(); if (!release.Wait(5000)) throw new Exception("test timeout"); }
                Interlocked.Decrement(ref active); return Accepted(new Page("latest"));
            });
            Start(h); var first = Workers.Dequeue(); var a = new Thread(() => first()); a.Start();
            Check(entered.Wait(5000), "first worker did not start");
            Start(h); var second = Workers.Dequeue(); var b = new Thread(() => second()); b.Start();
            release.Set(); Check(a.Join(5000) && b.Join(5000), "producer deadlock"); Pump();
            Check(calls == 2 && maxActive == 1 && Pages(h).Count == 2, "producer overlap or stale publication");
        });
        Test("open overview receives guides before item worker", () => {
            var h = New(() => Accepted(new Page("item"))); int refreshes = 0;
            OpenRefresh[h] = () => refreshes++;
            Start(h); Check(refreshes == 1 && Index(h)["guide"] == 0, "open guides not refreshed");
            Produce(); Pump(); OpenRefresh.Remove(h);
        });
        Test("scroll offset preserved and clamped across refresh", () => {
            var h = New(() => []);
            var api = (ICoreClientAPI)AccessTools.Field(typeof(GuiDialog), "capi").GetValue(h)!;
            var bounds = ElementBounds.Fixed(0, 0, 20, 100); bounds.CalcWorldBounds();
            float observed = -1;
            var scrollbar = new GuiElementScrollbar(api, x => observed = x, bounds);
            scrollbar.SetHeights(100, 1000); scrollbar.CurrentYPosition = 150;
            var composer = (GuiComposer)RuntimeHelpers.GetUninitializedObject(typeof(GuiComposer));
            AccessTools.Field(typeof(GuiComposer), "interactiveElements").SetValue(composer, new Dictionary<string,GuiElement> { ["scrollbar"] = scrollbar });
            AccessTools.Field(typeof(GuiComposer), "staticElements").SetValue(composer, new Dictionary<string,GuiElement>());
            AccessTools.Field(typeof(GuiDialogHandbook), "overviewGui").SetValue(h, composer);
            OpenRefresh[h] = () => scrollbar.SetHeights(100, 2000);
            Start(h); Check(Math.Abs(observed - 150) < 0.01f, "scroll jumped on growth");
            OpenRefresh[h] = () => scrollbar.SetHeights(100, 120);
            HandbookPageLoader.GuidesReady(h); Check(Math.Abs(observed - 20) < 0.01f, "scroll not clamped");
            OpenRefresh.Remove(h);
        });
        foreach (bool throwing in new[] { false, true }) Test(throwing ? "throwing page code contained on GUI thread" : "null page code contained on GUI thread", () => {
            var bad = new Page(null!) { Throw = throwing };
            var h = New(() => Accepted(new Page("valid"), bad)); Start(h); Produce(); Pump();
            Check(Pages(h).Count == 2 && Index(h)["valid"] == 1, "committed pages lost");
            Check(StateFlag(h, "Failed") && !StateFlag(h, "InProgress"), "publication failure stranded loading");
        });
        Test("final-index getter failure preserves previous lookup", () => {
            var p = new Page("valid"); var h = New(() => {
                var r = Accepted(new[] { p }.Concat(Enumerable.Range(0, 256).Select(i => new Page("f"+i))).ToArray());
                Pump(); p.Throw = true; return r;
            });
            Start(h); Produce(); Pump(); Check(Index(h)["guide"] == 0 && Index(h)["valid"] == 1 && StateFlag(h,"Failed"), "failed final index destroyed committed lookup");
        });
        harmony.UnpatchAll(harmony.Id);
        Console.WriteLine($"PASS all assertions: {assertions}");
    }
    public static int Main(string[] args) { try { GuiQueueEntry(); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
}

public class Proxy : DispatchProxy
{
    public System.Func<MethodInfo, object?[]?, object?>? Handler;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var result = Handler?.Invoke(targetMethod!, args);
        return result ?? (targetMethod!.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null);
    }
}
public sealed class Page(string code) : GuiHandbookPage
{
    public string Code = code;
    public bool Throw;
    public override string PageCode => Throw ? throw new InvalidOperationException("expected getter failure") : Code;
    public override string CategoryCode => "test";
    public override bool IsDuplicate => false;
    public override float SearchWeightOffset => 0;
    public override PageText GetPageText() => new() { Title = Code, Text = Code };
    public override void Dispose() { }
    public override void RenderListEntryTo(ICoreClientAPI capi, float dt, double x, double y, double cellWidth, double cellHeight) { }
    public override void ComposePage(GuiComposer gui, ElementBounds bounds, ItemStack[] stacks, ActionConsumable<string> open) { }
}
