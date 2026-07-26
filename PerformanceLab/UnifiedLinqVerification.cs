using System;
using System.Diagnostics;
using Memory2;

namespace Memory2.PerformanceLab;

internal static class UnifiedLinqVerification
{
    internal static bool Run(LawfulMemoryStore<MixedRoot> store)
    {
        Console.WriteLine();
        Console.WriteLine("Memory2-native LINQ verification matrix");
        Console.WriteLine("---------------------------------------");

        const int valueCount = 4096;
        int[] values = new int[valueCount];
        int[] otherValues = new int[valueCount];
        int[] projected = new int[valueCount];
        int[] zipped = new int[valueCount];
        for (int i = 0; i < valueCount; i++)
        {
            values[i] = i & 255;
            otherValues[i] = 128 + (i & 255);
        }

        var diagnostics = new LawfulQueryDiagnostics();
        var source = new LawfulValueQuery<int>(values, diagnostics);
        var prepared = source.Prepare(valueCount);
        long sink = 0;
        bool allPass = true;

        void Verify(string name, Action action, int iterations, double divisor, double targetNs)
        {
            for (int i = 0; i < Math.Min(1000, iterations); i++) action();
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) action();
            long end = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            double ns = (end - start) * (1_000_000_000.0 / Stopwatch.Frequency) / (iterations * divisor);
            bool pass = ns <= targetNs && allocated == 0;
            allPass &= pass;
            Console.WriteLine($"{name,-29} {ns,9:N1} ns/{(divisor == 1 ? "call" : "item"),-4} {allocated / (double)iterations,10:N3} B/op  {(pass ? "PASS" : "FAIL")}");
        }

        Verify("Any", () => { if (source.Any(x => x == 0)) sink++; }, 200_000, 1, 150);
        Verify("All", () => { if (!source.All(x => x < 128)) sink++; }, 200_000, 129, 15);
        Verify("Count/LongCount", () => sink += source.LongCount(), 1_000_000, 1, 50);
        Verify("First/Last", () => sink += source.First() + source.Last(), 1_000_000, 1, 60);
        Verify("ElementAt", () => sink += source.ElementAt(2048), 1_000_000, 1, 50);
        Verify("Contains", () => { if (source.Contains(127)) sink++; }, 50_000, 128, 15);
        Verify("Where prepared", () => sink += prepared.WhereInto(x => (x & 1) == 0), 5_000, valueCount, 25);
        Verify("Select prepared", () => sink += prepared.SelectInto(x => x + 1, projected), 5_000, valueCount, 20);
        Verify("Distinct prepared", () => sink += prepared.DistinctInto(), 5_000, valueCount, 45);
        Verify("Union prepared", () => sink += prepared.UnionInto(otherValues), 3_000, valueCount * 2.0, 50);
        Verify("Intersect prepared", () => sink += prepared.IntersectInto(otherValues), 3_000, valueCount * 2.0, 60);
        Verify("Except prepared", () => sink += prepared.ExceptInto(otherValues), 3_000, valueCount * 2.0, 60);
        Verify("Rank prepared", () => sink += prepared.OrderByRankInto(x => x), 1_000, valueCount, 120);
        Verify("Skip/Take/Reverse", () => sink += prepared.SliceReverseCount(10, 1000), 1_000_000, 1, 60);
        Verify("Min/Max", () => sink += source.Min() + source.Max(), 5_000, valueCount, 15);
        Verify("Aggregate", () => sink += source.Aggregate(0L, (a, x) => a + x), 5_000, valueCount, 15);
        Verify("GroupBy prepared", () => sink += prepared.GroupCount(x => x), 2_000, valueCount, 60);
        Verify("Join prepared", () => sink += prepared.JoinCount(otherValues, x => x, x => x), 1_000, valueCount * 2.0, 80);
        Verify("Zip prepared", () => sink += prepared.ZipInto(otherValues, (a, b) => a + b, zipped), 5_000, valueCount, 25);
        Verify("SequenceEqual", () => { if (source.SequenceEqual(source)) sink++; }, 5_000, valueCount, 15);
        Verify("Concat/Append/Prepend", () => sink += prepared.ConcatAppendPrependCount(otherValues), 1_000_000, 1, 60);
        Verify("DefaultIfEmpty", () => sink += source.DefaultIfEmpty().Count, 1_000_000, 1, 60);

        long beforeModels = store.Materializations;
        var descendantPlan = store.From<MixedEvent>().PrepareWhereSelectCount(
            x => x.Accepted && x.Amount >= 0,
            x => x.Amount);
        Verify("Descendant fused terminal", () => sink += descendantPlan.ExecuteCount(), 20,
            Math.Max(1, store.From<MixedEvent>().Count), 60);

        var composedDiagnostics = new LawfulQueryDiagnostics();
        var composed = new LawfulValueQuery<int>(values, composedDiagnostics)
            .Compose()
            .Where(x => x >= 32)
            .Where(x => x < 224)
            .Distinct()
            .OrderByRank(x => x)
            .Skip(3)
            .Take(16)
            .Prepare()
            .PrepareSum(x => x * 3L);

        long expected = 0;
        for (int x = 35; x < 51; x++) expected += x * 3L;
        for (int i = 0; i < 4096; i++) composed.Execute();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long beforeWrites = composedDiagnostics.IntermediateWrites;
        long checksum = 0;
        const int executions = 10_000;
        for (int i = 0; i < executions; i++) checksum += composed.Execute();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        long writesPerExecution = (composedDiagnostics.IntermediateWrites - beforeWrites) / executions;
        bool compositionPass = checksum == expected * executions && allocated == 0 &&
            composedDiagnostics.FallbackExecutions == 0 &&
            composedDiagnostics.ModelMaterializations == 0 &&
            composedDiagnostics.TraditionalSortExecutions == 0 &&
            composedDiagnostics.PlannerCompilations == 1 && writesPerExecution <= 64;
        allPass &= compositionPass;
        Console.WriteLine($"Composed optimizer chain                  {allocated / (double)executions,10:N3} B/op  {(compositionPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"  planner/fused/eliminated:       {composedDiagnostics.PlannerCompilations:N0}/{composedDiagnostics.FusedStages:N0}/{composedDiagnostics.EliminatedStages:N0}");
        Console.WriteLine($"  retained writes/execution:     {writesPerExecution:N0}");

        var terminalDiagnostics = new LawfulQueryDiagnostics();
        var terminal = new LawfulValueQuery<int>(values, terminalDiagnostics)
            .Compose().Where(x => x >= 32).Where(x => x < 224).Skip(3).Take(16).Prepare();
        long terminalExpected = 0;
        for (int x = 35; x < 51; x++) terminalExpected += x;
        bool fusionPass = terminal.ExecuteSum(x => x) == terminalExpected && terminalDiagnostics.IntermediateWrites == 0;
        allPass &= fusionPass;
        Console.WriteLine($"Terminal fusion zero writes:     {(fusionPass ? "PASS" : "FAIL")}");

        bool invariants = diagnostics.FallbackExecutions == 0 && diagnostics.ModelMaterializations == 0 &&
            diagnostics.TraditionalSortExecutions == 0 && store.Materializations == beforeModels;
        allPass &= invariants;
        Console.WriteLine($"Zero fallback/models/sorts:      {(invariants ? "PASS" : "FAIL")}");
        Console.WriteLine($"Operator checksum:               {sink:N0}");
        Console.WriteLine($"LINQ matrix result:              {(allPass ? "PASS" : "FAIL")}");
        return allPass;
    }
}
