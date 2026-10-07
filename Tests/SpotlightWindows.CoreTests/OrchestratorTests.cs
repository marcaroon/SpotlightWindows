using System.Diagnostics;
using SpotlightWindows.Core.Interfaces;
using SpotlightWindows.Core.Models;
using SpotlightWindows.Core.Services;
using static SpotlightWindows.CoreTests.TestRunner;

namespace SpotlightWindows.CoreTests;

public static class OrchestratorTests
{
    /// <summary>Configurable fake provider. Always returns NEW SearchResult objects (provider contract).</summary>
    private sealed class FakeProvider : ISearchProvider
    {
        public string Name { get; }
        public bool IsEnabled => true;
        private readonly (string Id, string Title, double Score)[] _items;
        private readonly int _delayMs;
        private readonly bool _blockSynchronously;
        private readonly bool _throw;

        public FakeProvider(string name, (string, string, double)[] items, int delayMs = 0, bool blockSynchronously = false, bool @throw = false)
        {
            Name = name; _items = items; _delayMs = delayMs; _blockSynchronously = blockSynchronously; _throw = @throw;
        }

        public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            if (_blockSynchronously) Thread.Sleep(_delayMs);   // worst case: synchronous work, ignores the token
            else if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            if (_throw) throw new InvalidOperationException("boom");

            return _items.Select(i => new SearchResult
            {
                Id = i.Id, Title = i.Title, ResultType = SearchResultType.File, Score = i.Score
            }).ToList();
        }
    }

    private static (SearchOrchestrator Orch, List<SearchSnapshot> Snaps, List<long> Times, Stopwatch Clock) Setup()
        => (new SearchOrchestrator(), new List<SearchSnapshot>(), new List<long>(), Stopwatch.StartNew());

    public static void Run()
    {
        ProgressiveDelivery();
        WeightAppliedOnce();
        DedupeIsOrderIndependent();
        TimeoutAbandonsSlowProvider();
        SyncProviderDoesNotBlockCaller();
        FailingProviderIsIsolated();
        CancellationStopsPublishing();
        EmptyQueryAndTrimming();
    }

    private static void ProgressiveDelivery()
    {
        var (orch, snaps, times, clock) = Setup();
        orch.RegisterProvider(new FakeProvider("fast", new[] { ("a", "Alpha", 900.0) }, delayMs: 10));
        orch.RegisterProvider(new FakeProvider("slow", new[] { ("b", "Beta", 400.0) }, delayMs: 400));

        orch.SearchProgressiveAsync("x", s => { snaps.Add(s); times.Add(clock.ElapsedMilliseconds); }, CancellationToken.None).Wait();

        Check("progressive: two snapshots (one per provider)", snaps.Count == 2, $"count={snaps.Count}");
        Check("progressive: first snapshot has only fast results and is NOT complete",
            snaps.Count == 2 && !snaps[0].IsComplete && snaps[0].Results.Count == 1 && snaps[0].Results[0].Id == "a");
        Check("progressive: first snapshot arrives well before the slow provider finishes", times.Count == 2 && times[0] < 300, $"t0={times.FirstOrDefault()}ms");
        Check("progressive: final snapshot is complete and merged + ranked",
            snaps.Count == 2 && snaps[1].IsComplete && snaps[1].Results.Select(r => r.Id).SequenceEqual(new[] { "a", "b" }));
    }

    private static void WeightAppliedOnce()
    {
        var (orch, snaps, _, _) = Setup();
        orch.RegisterProvider(new FakeProvider("half", new[] { ("a", "Alpha", 1000.0) }, delayMs: 10), scoreMultiplier: 0.5);
        orch.RegisterProvider(new FakeProvider("late", new[] { ("b", "Beta", 100.0) }, delayMs: 150));

        orch.SearchProgressiveAsync("x", snaps.Add, CancellationToken.None).Wait();

        double firstSnapshotScore = snaps[0].Results.First(r => r.Id == "a").Score;
        double finalScore = snaps[^1].Results.First(r => r.Id == "a").Score;
        Check("weight: applied (1000 x 0.5 = 500)", firstSnapshotScore == 500, firstSnapshotScore.ToString());
        Check("weight: NOT re-applied when later snapshots re-merge", finalScore == 500, finalScore.ToString());
    }

    private static void DedupeIsOrderIndependent()
    {
        // Same Id from both providers; B scores higher. B must win whether B is fast or slow.
        foreach (var bIsFast in new[] { true, false })
        {
            var (orch, snaps, _, _) = Setup();
            orch.RegisterProvider(new FakeProvider("A", new[] { ("same", "from-A", 100.0) }, delayMs: bIsFast ? 150 : 10));
            orch.RegisterProvider(new FakeProvider("B", new[] { ("same", "from-B", 200.0) }, delayMs: bIsFast ? 10 : 150));
            orch.SearchProgressiveAsync("x", snaps.Add, CancellationToken.None).Wait();

            var final = snaps[^1].Results;
            Check($"dedupe: higher score wins (B {(bIsFast ? "fast" : "slow")})", final.Count == 1 && final[0].Title == "from-B");
        }

        // Equal scores: the provider registered first wins, regardless of arrival order
        foreach (var firstIsFast in new[] { true, false })
        {
            var (orch, snaps, _, _) = Setup();
            orch.RegisterProvider(new FakeProvider("first", new[] { ("same", "from-first", 300.0) }, delayMs: firstIsFast ? 10 : 150));
            orch.RegisterProvider(new FakeProvider("second", new[] { ("same", "from-second", 300.0) }, delayMs: firstIsFast ? 150 : 10));
            orch.SearchProgressiveAsync("x", snaps.Add, CancellationToken.None).Wait();

            Check($"dedupe: tie -> registration order wins (first {(firstIsFast ? "fast" : "slow")})",
                snaps[^1].Results.Count == 1 && snaps[^1].Results[0].Title == "from-first");
        }
    }

    private static void TimeoutAbandonsSlowProvider()
    {
        var (orch, snaps, _, clock) = Setup();
        orch.RegisterProvider(new FakeProvider("ok", new[] { ("a", "Alpha", 500.0) }, delayMs: 10));
        // Blocks a thread for 1.5s and never looks at the token, so only a hard timeout can cope with it
        orch.RegisterProvider(new FakeProvider("hung", new[] { ("h", "Hung", 999.0) }, delayMs: 1500, blockSynchronously: true), timeoutMs: 200);

        orch.SearchProgressiveAsync("x", snaps.Add, CancellationToken.None).Wait();
        long elapsed = clock.ElapsedMilliseconds;

        Check("timeout: search finishes near the timeout, not after the hung provider", elapsed < 1000, $"{elapsed}ms");
        Check("timeout: final snapshot complete, hung provider's results dropped",
            snaps[^1].IsComplete && snaps[^1].Results.Count == 1 && snaps[^1].Results[0].Id == "a");
    }

    private static void SyncProviderDoesNotBlockCaller()
    {
        var orch = new SearchOrchestrator();
        orch.RegisterProvider(new FakeProvider("sync", new[] { ("a", "Alpha", 100.0) }, delayMs: 500, blockSynchronously: true));

        var sw = Stopwatch.StartNew();
        var task = orch.SearchProgressiveAsync("x", _ => { }, CancellationToken.None);
        sw.Stop();

        // This is the Phase 2/3 first-search UI freeze: the old code ran the provider's synchronous
        // prefix on the calling (UI) thread. The call must hand back control immediately.
        Check("caller thread: call returns immediately even if a provider blocks synchronously", sw.ElapsedMilliseconds < 150 && !task.IsCompleted, $"{sw.ElapsedMilliseconds}ms, completed={task.IsCompleted}");
        task.Wait();
    }

    private static void FailingProviderIsIsolated()
    {
        var (orch, snaps, _, _) = Setup();
        orch.RegisterProvider(new FakeProvider("bad", Array.Empty<(string, string, double)>(), @throw: true));
        orch.RegisterProvider(new FakeProvider("good", new[] { ("a", "Alpha", 500.0) }));

        orch.SearchProgressiveAsync("x", snaps.Add, CancellationToken.None).Wait();

        Check("failure: throwing provider does not break the search", snaps[^1].IsComplete && snaps[^1].Results.Count == 1);
    }

    private static void CancellationStopsPublishing()
    {
        var orch = new SearchOrchestrator();
        orch.RegisterProvider(new FakeProvider("slow", new[] { ("a", "Alpha", 500.0) }, delayMs: 300));
        var snaps = new List<SearchSnapshot>();
        using var cts = new CancellationTokenSource();

        var task = orch.SearchProgressiveAsync("x", snaps.Add, cts.Token);
        Thread.Sleep(50);
        cts.Cancel();

        bool threwCancel = false;
        try { task.Wait(); }
        catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { threwCancel = true; }

        Thread.Sleep(400); // give the abandoned provider time to finish; it must not publish
        Check("cancellation: search throws OperationCanceledException", threwCancel);
        Check("cancellation: nothing is published for a cancelled search", snaps.Count == 0, $"snapshots={snaps.Count}");
    }

    private static void EmptyQueryAndTrimming()
    {
        var (orch, snaps, _, _) = Setup();
        orch.MaxResults = 3;
        orch.RegisterProvider(new FakeProvider("many", Enumerable.Range(1, 8).Select(i => ($"id{i}", $"T{i}", (double)i * 10)).ToArray()));

        orch.SearchProgressiveAsync("   ", snaps.Add, CancellationToken.None).Wait();
        Check("empty query: one complete empty snapshot", snaps.Count == 1 && snaps[0].IsComplete && snaps[0].Results.Count == 0);

        snaps.Clear();
        orch.SearchProgressiveAsync("x", snaps.Add, CancellationToken.None).Wait();
        Check("MaxResults: trimmed to 3, best first", snaps[^1].Results.Count == 3 && snaps[^1].Results[0].Id == "id8");

        var viaSearchAsync = orch.SearchAsync("x", CancellationToken.None).Result;
        Check("SearchAsync wrapper returns the final merged list", viaSearchAsync.Count == 3 && viaSearchAsync[0].Id == "id8");
    }
}
