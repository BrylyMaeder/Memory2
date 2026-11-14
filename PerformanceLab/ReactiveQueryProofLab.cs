using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Memory2.PerformanceLab;

internal sealed class ReactiveProofRow
{
    public long Id { get; set; }
    public int Score { get; set; }
    public bool Active { get; set; }
}

internal static class ReactiveQueryProofLab
{
    internal static int Run(string[] args)
    {
        int subscriptionCount = ReadInt(args, "--subscriptions", 500);
        int operations = ReadInt(args, "--operations", 1_000_000);
        if (subscriptionCount < 2 || operations < 10_000)
            throw new ArgumentOutOfRangeException("The reactive proof requires at least 2 subscriptions and 10,000 operations.");

        using var store = LawfulMemoryStore.Create<ReactiveProofRow>(4, schema => schema
            .IncludeScalar<ReactiveProofRow, long>(x => x.Id)
            .IncludeScalar<ReactiveProofRow, int>(x => x.Score)
            .IncludeScalar<ReactiveProofRow, bool>(x => x.Active));
        ReactiveProofRow row = store.Set().Create(new ReactiveProofRow { Score = subscriptionCount + 1, Active = false });
        var subscriptions = new List<IDisposable>(subscriptionCount);
        try
        {
            for (int i = 0; i < subscriptionCount - 1; i++)
            {
                int threshold = i;
                subscriptions.Add(store.From<ReactiveProofRow>()
                    .Where(x => x.Score >= threshold)
                    .Select(x => x.Id)
                    .Subscribe(_ => { }));
            }

            long entered = 0;
            subscriptions.Add(store.From<ReactiveProofRow>()
                .Where(x => x.Active)
                .Select(x => x.Id)
                .Subscribe(_ => entered++));

            for (int i = 0; i < 10_000; i++) { row.Active = !row.Active; store.Save(row); }
            long evaluationsBefore = store.SubscriptionEvaluations;
            long probesBefore = store.SubscriptionRouteProbes;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < operations; i++)
            {
                row.Active = !row.Active;
                store.Save(row);
            }
            watch.Stop();

            long evaluations = store.SubscriptionEvaluations - evaluationsBefore;
            long probes = store.SubscriptionRouteProbes - probesBefore;
            long formerEvaluations = checked((long)subscriptionCount * 2L * operations);
            double reduction = formerEvaluations / (double)evaluations;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Console.WriteLine("Memory2 reactive LINQ routing proof");
            Console.WriteLine("===================================");
            Console.WriteLine($"Subscriptions:              {subscriptionCount:N0}");
            Console.WriteLine($"Tracked writes:             {operations:N0}");
            Console.WriteLine($"Elapsed:                    {watch.Elapsed.TotalSeconds:N3} s");
            Console.WriteLine($"Throughput:                 {operations / watch.Elapsed.TotalSeconds:N0} writes/sec");
            Console.WriteLine($"Naive scan evaluations:     {formerEvaluations:N0}");
            Console.WriteLine($"Dependency-routed evals:    {evaluations:N0}");
            Console.WriteLine($"Dependency route probes:    {probes:N0}");
            Console.WriteLine($"Evaluation reduction:       {reduction:N1}x");
            Console.WriteLine($"Allocated on measured path: {allocated:N0} bytes");
            Console.WriteLine($"GC collections 0/1/2:      {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}");
            Console.WriteLine($"Entered consequences:       {entered:N0}");

            bool pass = evaluations == operations * 2L && probes == operations && allocated == 0 &&
                entered == (10_000L + operations) / 2L;
            Console.WriteLine($"Reactive routing proof:     {(pass ? "PASS" : "FAIL")}");
            return pass ? 0 : 1;
        }
        finally
        {
            for (int i = subscriptions.Count - 1; i >= 0; i--) subscriptions[i].Dispose();
        }
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int value))
                return value;
        return fallback;
    }
}
