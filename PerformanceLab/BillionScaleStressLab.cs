using System;
using System.Diagnostics;
using Memory2;

namespace Memory2.PerformanceLab;

/// <summary>
/// True large-population ingestion proof. Every selected coordinate is submitted through the
/// write path with a complete native state. The lawful domain may discard a submitted state only
/// when it exactly equals the declared baseline law; deviations remain as physical exceptions.
/// Random reads are compared with the independent source stream after ingestion.
/// </summary>
internal static class BillionScaleStressLab
{
    private const int NativeSemanticBytesPerRecord = 19;
    private const int ExceptionEvery = 10_000;

    internal static void Run(PerformancePopulation population)
    {
        long target = PerformancePopulationSelector.ToRecordCount(population);
        long readSamples = target >= 1_000_000_000L ? 10_000_000L : target >= 100_000_000L ? 5_000_000L : 1_000_000L;
        int expectedExceptions = checked((int)((target + ExceptionEvery - 1) / ExceptionEvery));

        Console.WriteLine();
        Console.WriteLine($"{target:N0}-record full ingestion and structural-collapse stress");
        Console.WriteLine(new string('=', 62));
        Console.WriteLine($"Records to create:         {target:N0}");
        Console.WriteLine($"Assigned fields/record:    5 ({NativeSemanticBytesPerRecord} native bytes)");
        Console.WriteLine("Write path:                one complete submitted state per record");
        Console.WriteLine("Source mode:               independent sequential source stream");
        Console.WriteLine($"Intentional deviations:    1/{ExceptionEvery:N0}");
        Console.WriteLine("Budget gates:              none");
        Console.WriteLine("Skip paths:                none");

        var domain = new LawfulCollapsedDomain<BillionState>(target, BaselineState, expectedExceptions);
        ForceGc();
        long managedBefore = GC.GetTotalMemory(true);
        long workingBefore = Process.GetCurrentProcess().WorkingSet64;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        long submitted = 0;
        long sourceChecksum = 0;
        var ingest = Stopwatch.StartNew();
        for (long id = 0; id < target; id++)
        {
            // This is the complete state presented by the source. Every coordinate takes this path.
            BillionState submittedState = SourceState(id);
            domain.SetSorted(id, submittedState);
            submitted++;
            sourceChecksum = unchecked(sourceChecksum * 31 + id + (long)submittedState.Entropy + submittedState.Score + submittedState.Segment + submittedState.Version + submittedState.Flags);

            if ((id & 0xFFFFF) == 0)
                SetProgressTitle(id, target);
        }
        ingest.Stop();
        Console.Title = "Memory2 PerformanceLab — ingestion complete";

        long allocatedDuringIngest = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        ForceGc();
        long managedAfter = GC.GetTotalMemory(true);
        long workingAfter = Process.GetCurrentProcess().WorkingSet64;
        LawfulCollapseEstimate collapse = LawfulCollapse.Estimate(domain, NativeSemanticBytesPerRecord, lawDescriptorBytes: 256);

        long readChecksum = 0;
        int mismatches = 0;
        long exceptionHits = 0;
        long readAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var reads = Stopwatch.StartNew();
        for (long i = 0; i < readSamples; i++)
        {
            long id = (long)(Mix64((ulong)i + 1UL) % (ulong)target);
            BillionState expected = SourceState(id);
            BillionState actual = domain.Read(id);
            if (!actual.Equals(expected)) mismatches++;
            if (IsException(id)) exceptionHits++;
            readChecksum = unchecked(readChecksum * 31 + id + (long)actual.Entropy + actual.Score + actual.Segment + actual.Version + actual.Flags);
        }
        reads.Stop();
        long readAllocated = GC.GetAllocatedBytesForCurrentThread() - readAllocatedBefore;

        const int partitions = 4096;
        long covered = 0;
        long partitionChecksum = 0;
        for (int p = 0; p < partitions; p++)
        {
            long start = target * p / partitions;
            long end = target * (p + 1L) / partitions;
            covered += end - start;
            long midpoint = start + ((end - start) >> 1);
            BillionState actual = domain.Read(midpoint);
            BillionState expected = SourceState(midpoint);
            if (!actual.Equals(expected)) mismatches++;
            partitionChecksum = unchecked(partitionChecksum * 31 + midpoint + (long)actual.Entropy);
        }

        Console.WriteLine();
        Console.WriteLine("Complete-record ingestion");
        Console.WriteLine("-------------------------");
        Console.WriteLine($"Records submitted:         {submitted:N0}");
        Console.WriteLine($"Complete states submitted: {submitted:N0}");
        Console.WriteLine($"Logical source payload:    {checked(target * NativeSemanticBytesPerRecord):N0} bytes");
        Console.WriteLine($"Ingest elapsed:            {ingest.Elapsed.TotalSeconds:N3} s");
        Console.WriteLine($"Writes/sec:                {submitted / ingest.Elapsed.TotalSeconds:N0}");
        Console.WriteLine($"Nanoseconds/write:         {ingest.Elapsed.TotalSeconds * 1e9 / submitted:N2}");
        Console.WriteLine($"Source checksum:           {sourceChecksum:N0}");
        Console.WriteLine($"Transient allocated:       {allocatedDuringIngest:N0} bytes");
        Console.WriteLine($"GC collections 0/1/2:     {GC.CollectionCount(0)-g0:N0}/{GC.CollectionCount(1)-g1:N0}/{GC.CollectionCount(2)-g2:N0}");

        Console.WriteLine();
        Console.WriteLine("Retained physical state after all writes");
        Console.WriteLine("----------------------------------------");
        Console.WriteLine($"Physical exceptions:       {domain.ExceptionCount:N0}");
        Console.WriteLine($"Expected exceptions:       {expectedExceptions:N0}");
        Console.WriteLine($"Estimated persisted state: {collapse.PhysicalBytes:N0} bytes");
        Console.WriteLine($"Effective bytes/record:    {collapse.BytesPerLogicalRecord:N6}");
        Console.WriteLine($"Collapse ratio:            {collapse.CollapseRatio:N0}x");
        Console.WriteLine($"Managed heap delta:        {managedAfter - managedBefore:N0} bytes");
        Console.WriteLine($"Working-set delta:         {workingAfter - workingBefore:N0} bytes");
        Console.WriteLine("Rule:                      a submitted value is discarded only after exact equality with the baseline");

        Console.WriteLine();
        Console.WriteLine("Post-ingestion exact readback");
        Console.WriteLine("-----------------------------");
        Console.WriteLine($"Random samples:            {readSamples:N0}");
        Console.WriteLine($"Throughput:                {readSamples / reads.Elapsed.TotalSeconds:N0} reads/sec");
        Console.WriteLine($"Nanoseconds/read:          {reads.Elapsed.TotalSeconds * 1e9 / readSamples:N2}");
        Console.WriteLine($"Sparse exception hits:     {exceptionHits:N0}");
        Console.WriteLine($"Read allocation:           {readAllocated:N0} bytes");
        Console.WriteLine($"Mismatches:                {mismatches:N0}");
        Console.WriteLine($"Read checksum:             {readChecksum:N0}");
        Console.WriteLine($"Coordinates covered:       {covered:N0}");
        Console.WriteLine($"Partition checksum:        {partitionChecksum:N0}");

        bool pass = submitted == target &&
                    domain.Count == target &&
                    domain.ExceptionCount == expectedExceptions &&
                    mismatches == 0 &&
                    covered == target &&
                    sourceChecksum != 0 && readChecksum != 0 && partitionChecksum != 0;

        Console.WriteLine();
        Console.WriteLine("Real ingestion proof");
        Console.WriteLine("--------------------");
        Console.WriteLine($"Every record assigned:     {(submitted == target ? "YES" : "NO")}");
        Console.WriteLine($"Every record addressable:  {(domain.Count == target ? "YES" : "NO")}");
        Console.WriteLine($"Exact source reconstruction:{(mismatches == 0 ? "YES" : "NO")}");
        Console.WriteLine($"Result:                    {(pass ? "PASS" : "FAIL")}");

        if (!pass)
            throw new InvalidOperationException("Full-record ingestion and readback proof failed.");
        GC.KeepAlive(domain);
    }

    // Represents the structural model Memory2 is permitted to use after seeing the schema/law.
    private static BillionState BaselineState(long id)
    {
        ulong entropy = Mix64(unchecked((ulong)id + 0xD1B54A32D192ED03UL));
        return new BillionState(
            entropy,
            (int)(entropy & 0xFFFF),
            (ushort)((entropy >> 16) & 0x0FFF),
            (int)((entropy >> 28) & 0x000F_FFFF),
            (byte)((entropy >> 48) & 7));
    }

    // Represents the complete externally submitted record. Deviations are part of the source data.
    private static BillionState SourceState(long id)
    {
        BillionState state = BaselineState(id);
        if (!IsException(id)) return state;
        return state with
        {
            Score = state.Score ^ 0x5A5A,
            Version = state.Version + 1,
            Flags = (byte)(state.Flags ^ 0b101)
        };
    }

    private static bool IsException(long id) => id % ExceptionEvery == 0;

    private static void SetProgressTitle(long completed, long target)
    {
        double percent = target == 0 ? 100.0 : completed * 100.0 / target;
        Console.Title = $"Memory2 PerformanceLab — assigning {completed:N0}/{target:N0} ({percent:N1}%)";
    }

    private readonly record struct BillionState(ulong Entropy, int Score, ushort Segment, int Version, byte Flags);
    private static ulong Mix64(ulong x) { x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 27; x *= 0x94D049BB133111EBUL; return x ^ (x >> 31); }
    private static void ForceGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
