using System.Diagnostics;
using Memory2;

namespace Memory2.PerformanceLab;

/// <summary>
/// Final release proof layer. This suite intentionally uses public high-level query APIs.
/// It measures how key entropy and long query composition affect the native algebra,
/// and fails loudly when execution allocates, falls back, materializes models, or sorts traditionally.
/// </summary>
internal static class FinalReleaseLab
{
    private const int ValueCount = 4096;
    private const int Iterations = 2000;

    public static void Run(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("Final release proof suite");
        Console.WriteLine("=========================");
        PrintRuntimeEnvironment();
        RunEntropyMatrix();
        RunCompositionMatrix();
        RunScalingMatrix();
        Console.WriteLine("Final release proof: PASS");
    }

    private static void PrintRuntimeEnvironment()
    {
        Console.WriteLine();
        Console.WriteLine("Runtime environment");
        Console.WriteLine("-------------------");
        Console.WriteLine($"Framework:                 {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS:                        {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture:              {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Processors:                {Environment.ProcessorCount:N0}");
        Console.WriteLine($"64-bit process:            {Environment.Is64BitProcess}");
        Console.WriteLine($"Server GC:                 {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"Latency mode:              {System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"Stopwatch frequency:       {Stopwatch.Frequency:N0}");
        Console.WriteLine($"Debugger attached:         {Debugger.IsAttached}");
    }

    private static void RunEntropyMatrix()
    {
        Console.WriteLine();
        Console.WriteLine("Entropy sensitivity matrix");
        Console.WriteLine("--------------------------");
        Console.WriteLine("Workload                    entropy  distinct   distinct ns/item   group ns/item   rank ns/item   engine B/op");

        int[] low = new int[ValueCount];
        int[] medium = new int[ValueCount];
        int[] high = new int[ValueCount];
        int[] collision = new int[ValueCount];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < ValueCount; i++)
        {
            low[i] = i & 7;
            medium[i] = i & 255;
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            high[i] = unchecked((int)state);
            collision[i] = i << 13; // identical low table bits: adversarial open-addressing input
        }

        bool pass = true;
        pass &= MeasureEntropyCase("Low / 8 symbols", low, 8);
        pass &= MeasureEntropyCase("Medium / 256 symbols", medium, 256);
        pass &= MeasureEntropyCase("High / xorshift", high, null);
        pass &= MeasureEntropyCase("Adversarial low bits", collision, ValueCount);
        if (!pass) throw new InvalidOperationException("Entropy matrix failed a semantic or zero-overhead invariant.");
    }

    private static bool MeasureEntropyCase(string name, int[] values, int? expectedDistinct)
    {
        var diagnostics = new LawfulQueryDiagnostics();
        var source = new LawfulValueQuery<int>(values, diagnostics);
        var prepared = source.Prepare(values.Length);

        int distinct = prepared.DistinctInto();
        int groups = prepared.GroupCount(x => x);
        if (groups != distinct || (expectedDistinct.HasValue && distinct != expectedDistinct.Value))
            throw new InvalidOperationException($"Entropy semantic mismatch for {name}: distinct={distinct}, groups={groups}.");

        for (int i = 0; i < 2048; i++)
        {
            prepared.DistinctInto();
            prepared.GroupCount(x => x);
            prepared.OrderByRankInto(x => x);
        }

        Func<int, int> keyIdentity = static x => x;
        Func<int, long> rankIdentity = static x => x;
        Action distinctAction = () => prepared.DistinctInto();
        Action groupAction = () => prepared.GroupCount(keyIdentity);
        Action rankAction = () => prepared.OrderByRankInto(rankIdentity);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double distinctNs = MeasureNsPerItem(distinctAction, values.Length, Iterations);
        double groupNs = MeasureNsPerItem(groupAction, values.Length, Iterations);
        double rankNs = MeasureNsPerItem(rankAction, values.Length, 500);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double bytesPerOp = allocated / (double)(Iterations * 2 + 500);
        double entropy = ShannonEntropy(values);
        bool entropyPerformance = !name.StartsWith("Adversarial", StringComparison.Ordinal) ||
            (distinctNs < 250.0 && groupNs < 300.0);
        bool invariant = diagnostics.FallbackExecutions == 0 && diagnostics.ModelMaterializations == 0 &&
            diagnostics.TraditionalSortExecutions == 0 && allocated == 0 && entropyPerformance;

        Console.WriteLine($"{name,-27} {entropy,7:N2} {distinct,9:N0} {distinctNs,18:N1} {groupNs,15:N1} {rankNs,14:N1} {bytesPerOp,13:N3} {(invariant ? "PASS" : "FAIL")}");
        if (!entropyPerformance)
            Console.WriteLine("  failed: adversarial hashing exceeded the hardened-table budget (distinct <250 ns/item, group <300 ns/item).");
        GC.KeepAlive(source);
        return invariant;
    }

    private static void RunCompositionMatrix()
    {
        Console.WriteLine();
        Console.WriteLine("Adversarial composition matrix");
        Console.WriteLine("------------------------------");
        Console.WriteLine("Every row is a complete terminal-specific plan; method count is not execution-stage count.");

        int[] values = new int[ValueCount];
        for (int i = 0; i < values.Length; i++) values[i] = i & 255;

        bool pass = true;
        var failures = new List<string>();

        var d1 = new LawfulQueryDiagnostics();
        var bounded = new LawfulValueQuery<int>(values, d1).Compose()
            .Where(x => x >= 32).Where(x => x < 224).Distinct()
            .OrderByRank(x => x).Skip(3).Take(16).Prepare().PrepareSum(x => x * 3L);
        long expectedBounded = 0; for (int x = 35; x < 51; x++) expectedBounded += x * 3L;
        Record("filters+distinct+rank+window+sum", MeasureComposition("filters+distinct+rank+window+sum", bounded.Execute, expectedBounded, d1, 19, 0, 100_000));

        var d2 = new LawfulQueryDiagnostics();
        var count = new LawfulValueQuery<int>(values, d2).Compose()
            .Where(x => x >= 32).Distinct().OrderByRank(x => x).Reverse().Skip(10).Take(20).Prepare();
        Record("rank erased by Count", MeasureComposition("rank erased by Count", () => count.ExecuteCount(), 20, d2, 0, 0, 80_000));

        var d3 = new LawfulQueryDiagnostics();
        var sum = new LawfulValueQuery<int>(values, d3).Compose()
            .Where(x => x >= 32).Where(x => x < 64).OrderByRank(x => x).Prepare();
        long expectedSum = 0; for (int x = 32; x < 64; x++) expectedSum += x * 16L;
        Record("order erased by full Sum", MeasureComposition("order erased by full Sum", () => sum.ExecuteSum(x => x), expectedSum, d3, 0, 0, 80_000));

        var d4 = new LawfulQueryDiagnostics();
        var skipped = new LawfulValueQuery<int>(values, d4).Compose()
            .Distinct().OrderByRank(x => x).Skip(2).Prepare();
        long expectedSkipped = 0; for (int x = 2; x < 256; x++) expectedSkipped += x;
        Record("unbounded ranked Skip+Sum", MeasureComposition("unbounded ranked Skip+Sum", () => skipped.ExecuteSum(x => x), expectedSkipped, d4, 2, 0, 100_000));

        var d5 = new LawfulQueryDiagnostics();
        var terminal = new LawfulValueQuery<int>(values, d5).Compose()
            .Where(x => (x & 1) == 0).Where(x => x > 10).Skip(5).Take(7).Prepare();
        long expectedTerminal = 0; int seen = 0; int taken = 0;
        for (int i = 0; i < values.Length && taken < 7; i++) if ((values[i] & 1) == 0 && values[i] > 10) { if (seen++ < 5) continue; expectedTerminal += values[i]; taken++; }
        Record("pure terminal fusion", MeasureComposition("pure terminal fusion", () => terminal.ExecuteSum(x => x), expectedTerminal, d5, 0, 0, 80_000));

        if (!pass) throw new InvalidOperationException("Composition matrix failed: " + string.Join("; ", failures));

        void Record(string name, bool result)
        {
            pass &= result;
            if (!result) failures.Add(name);
        }
    }

    private static bool MeasureComposition(string name, Func<long> execute, long expected, LawfulQueryDiagnostics diagnostics,
        long maximumRetainedWrites, long maximumRankPasses, double targetNs)
    {
        long warm = 0;
        for (int i = 0; i < 4096; i++) warm ^= execute();
        long beforeWrites = diagnostics.IntermediateWrites;
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        const int iterations = 10_000;
        long checksum = 0;
        for (int i = 0; i < iterations; i++) checksum += execute();
        long stop = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        long writes = (diagnostics.IntermediateWrites - beforeWrites) / iterations;
        double ns = (stop - start) * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
        bool semantic = checksum == expected * iterations;
        bool allocation = allocated == 0;
        bool retained = writes <= maximumRetainedWrites;
        bool rank = diagnostics.RankPasses <= maximumRankPasses;
        bool nativeOnly = diagnostics.FallbackExecutions == 0 && diagnostics.ModelMaterializations == 0 && diagnostics.TraditionalSortExecutions == 0;
        bool timing = ns <= targetNs;
        bool timingAuthoritative = !Debugger.IsAttached;
        bool ok = semantic && allocation && retained && rank && nativeOnly && (timing || !timingAuthoritative);
        string status = ok ? (timing ? "PASS" : "PASS*" ) : "FAIL";
        Console.WriteLine($"{name,-38} {ns,10:N1} ns  writes={writes,3}  alloc={allocated,4}  rank={diagnostics.RankPasses,2}  {status}");
        if (!ok)
            Console.WriteLine($"  failed: semantic={!semantic}, allocation={!allocation}, retained={!retained}, rank={!rank}, native={!nativeOnly}, timing={!timing} (target {targetNs:N0} ns)");
        else if (!timing && !timingAuthoritative)
            Console.WriteLine("  * latency threshold is informational while a debugger is attached; all semantic and zero-overhead invariants passed.");
        GC.KeepAlive(warm);
        return ok;
    }

    private static void RunScalingMatrix()
    {
        Console.WriteLine();
        Console.WriteLine("Composition scaling matrix");
        Console.WriteLine("--------------------------");
        Console.WriteLine("Population     ns/item   retained/window   allocation   result");
        int[] sizes = { 256, 1024, 4096, 16384 };
        bool pass = true;
        foreach (int size in sizes)
        {
            int[] values = new int[size];
            for (int i = 0; i < size; i++) values[i] = i & 255;
            var diagnostics = new LawfulQueryDiagnostics();
            var terminal = new LawfulValueQuery<int>(values, diagnostics).Compose()
                .Where(x => x >= 16).Distinct().OrderByRank(x => x).Skip(4).Take(12).Prepare().PrepareSum(x => x);
            for (int i = 0; i < 4096; i++) terminal.Execute();
            long beforeWrites = diagnostics.IntermediateWrites;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            const int iterations = 2000;
            long checksum = 0;
            for (int i = 0; i < iterations; i++) checksum += terminal.Execute();
            long stop = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long writes = (diagnostics.IntermediateWrites - beforeWrites) / iterations;
            double nsItem = (stop - start) * (1_000_000_000.0 / Stopwatch.Frequency) / (iterations * (double)size);
            bool ok = allocated == 0 && writes == 16 && diagnostics.RankPasses == 0 && diagnostics.FallbackExecutions == 0;
            pass &= ok;
            Console.WriteLine($"{size,10:N0} {nsItem,11:N2} {writes,17:N0} {allocated,12:N0}   {(ok ? "PASS" : "FAIL")}");
            GC.KeepAlive(checksum);
        }
        if (!pass) throw new InvalidOperationException("Composition scaling proof failed.");
    }

    private static double MeasureNsPerItem(Action action, int items, int iterations)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) action();
        long stop = Stopwatch.GetTimestamp();
        return (stop - start) * (1_000_000_000.0 / Stopwatch.Frequency) / (iterations * (double)items);
    }

    private static double ShannonEntropy(int[] values)
    {
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < values.Length; i++) counts[values[i]] = counts.TryGetValue(values[i], out int count) ? count + 1 : 1;
        double entropy = 0;
        foreach (int count in counts.Values)
        {
            double p = count / (double)values.Length;
            entropy -= p * Math.Log(p, 2);
        }
        return entropy;
    }
}
