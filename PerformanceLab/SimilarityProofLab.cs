using System.Diagnostics;
using Memory2.At;

namespace Memory2.PerformanceLab;

/// <summary>
/// Isolates the proposed interlocked numerical-similarity relation from Memory2's
/// storage engine. It measures classification, a conventional postings index, and
/// algebraic relation inversion while accounting for their payload bytes.
/// </summary>
internal static class SimilarityProofLab
{
    private const int GeneratedModulus = 1000;
    private const int GeneratedMultiplier = 731;
    private const int GeneratedOffset = 197;

    internal static int Run(string[] args)
    {
        int population = ReadInt(args, "--population", 5_000_000);
        int generatedPopulation = ReadInt(args, "--generated-population", 10_000_000);
        int queryCount = ReadInt(args, "--queries", 20);
        int samples = ReadInt(args, "--samples", 7);
        int engineScanPopulation = ReadInt(args, "--engine-scan-population", 2_000_000);
        long hugePopulation = ReadLong(args, "--huge-population", 1_000_000_000_000L);
        int hugeOperations = ReadInt(args, "--huge-operations", 100_000);
        int sparsePatches = ReadInt(args, "--sparse-patches", 1000);
        if (population < 100_000 || generatedPopulation < 100_000 || queryCount < 10 || samples < 3)
            throw new ArgumentOutOfRangeException(nameof(args), "Similarity proof sizes are too small for stable measurements.");

        ushort[] neighborhoods = BuildNeighborhoods();
        ushort[] blockSignatures = BuildBlockSignatures(neighborhoods);
        int[][] preparedResidues = BuildPreparedResidues(blockSignatures);
        int[] queries = Enumerable.Range(0, queryCount).Select(i => i % 10).ToArray();

        PrintRelation(neighborhoods, blockSignatures);

        Console.WriteLine();
        Console.WriteLine("Stored random records");
        Console.WriteLine("---------------------");
        var blocks = new ushort[population];
        uint random = 0x9E3779B9u;
        for (int i = 0; i < blocks.Length; i++)
        {
            random = XorShift(random + unchecked((uint)i * 0x85EBCA6Bu));
            blocks[i] = (ushort)(random % 1000u);
        }

        var signatures = new ushort[population];
        var postingCounts = new int[10];
        var buildWatch = Stopwatch.StartNew();
        for (int i = 0; i < blocks.Length; i++)
        {
            ushort signature = blockSignatures[blocks[i]];
            signatures[i] = signature;
            for (int center = 0; center < 10; center++)
                if ((signature & (1 << center)) != 0) postingCounts[center]++;
        }

        var postings = new int[10][];
        for (int center = 0; center < postings.Length; center++) postings[center] = new int[postingCounts[center]];
        Array.Clear(postingCounts, 0, postingCounts.Length);
        for (int id = 0; id < signatures.Length; id++)
        {
            ushort signature = signatures[id];
            for (int center = 0; center < 10; center++)
                if ((signature & (1 << center)) != 0) postings[center][postingCounts[center]++] = id;
        }
        buildWatch.Stop();

        RunRawScan(blocks, blockSignatures, queries);
        RunSignatureScan(signatures, queries);
        RunPostings(postings, queries);

        Measurement rawScan = Measure(samples, queryCount, () => RunRawScan(blocks, blockSignatures, queries));
        Measurement signatureScan = Measure(samples, queryCount, () => RunSignatureScan(signatures, queries));
        Measurement postingSearch = Measure(samples, queryCount, () => RunPostings(postings, queries));
        if (rawScan.Checksum != signatureScan.Checksum || rawScan.Checksum != postingSearch.Checksum)
            throw new InvalidOperationException("Stored similarity search implementations returned different results.");

        long totalPostings = postings.Sum(x => (long)x.Length);
        long rawPayload = blocks.LongLength * sizeof(ushort);
        long signaturePayload = signatures.LongLength * sizeof(ushort);
        long postingPayload = totalPostings * sizeof(int);
        double averageSelectivity = totalPostings / (double)(population * 10L);

        Print("Raw packed-value scan", rawScan);
        Print("Preclassified-signature scan", signatureScan);
        Print("Conventional postings index", postingSearch);
        Console.WriteLine($"Index construction:                 {buildWatch.Elapsed.TotalMilliseconds,12:N2} ms");
        Console.WriteLine($"Average result selectivity:         {averageSelectivity,12:P2}");
        Console.WriteLine($"Raw value payload:                  {rawPayload,12:N0} bytes ({rawPayload / (double)population:N2}/record)");
        Console.WriteLine($"Added signature payload:            {signaturePayload,12:N0} bytes ({signaturePayload / (double)population:N2}/record)");
        Console.WriteLine($"Added postings payload:             {postingPayload,12:N0} bytes ({postingPayload / (double)population:N2}/record)");
        Console.WriteLine($"Indexed/raw payload ratio:          {(rawPayload + signaturePayload + postingPayload) / (double)rawPayload,12:N2}x");
        Console.WriteLine($"Signature scan speedup:             {rawScan.NanosecondsPerQuery / signatureScan.NanosecondsPerQuery,12:N2}x");
        Console.WriteLine($"Postings speedup:                   {rawScan.NanosecondsPerQuery / postingSearch.NanosecondsPerQuery,12:N2}x");

        Console.WriteLine();
        Console.WriteLine("Generated coordinate domain");
        Console.WriteLine("---------------------------");
        Console.WriteLine($"Law: block = ({GeneratedMultiplier} * coordinate + {GeneratedOffset}) mod {GeneratedModulus}");
        RunGeneratedScan(generatedPopulation, blockSignatures, queries);
        RunAlgebraicRelation(generatedPopulation, preparedResidues, queries);
        Measurement generatedScan = Measure(samples, queryCount,
            () => RunGeneratedScan(generatedPopulation, blockSignatures, queries));
        Measurement algebraicSearch = Measure(samples, queryCount,
            () => RunAlgebraicRelation(generatedPopulation, preparedResidues, queries));
        if (generatedScan.Checksum != algebraicSearch.Checksum)
            throw new InvalidOperationException("Algebraic similarity inversion did not match the generated-domain scan.");

        long preparedRelationBytes = preparedResidues.Sum(x => (long)x.Length) * sizeof(int);
        long materializedGeneratedPostingCount = 0;
        for (int center = 0; center < preparedResidues.Length; center++)
        for (int i = 0; i < preparedResidues[center].Length; i++)
        {
            int residue = preparedResidues[center][i];
            if (residue < generatedPopulation)
                materializedGeneratedPostingCount += 1L + (generatedPopulation - 1L - residue) / GeneratedModulus;
        }
        long materializedGeneratedPostingBytes = materializedGeneratedPostingCount * sizeof(int);
        Print("Generated-domain full scan", generatedScan);
        Print("Algebraic relation inversion", algebraicSearch);
        Console.WriteLine($"Prepared relation payload:          {preparedRelationBytes,12:N0} bytes");
        Console.WriteLine($"Equivalent materialized postings:   {materializedGeneratedPostingBytes,12:N0} bytes");
        Console.WriteLine($"Postings/prepared payload ratio:     {materializedGeneratedPostingBytes / (double)preparedRelationBytes,12:N2}x");
        Console.WriteLine($"Algebraic search speedup:           {generatedScan.NanosecondsPerQuery / algebraicSearch.NanosecondsPerQuery,12:N2}x");

        PrintInformationEconomics(blockSignatures);
        RunIntegratedMemory2Proof(blockSignatures, samples, engineScanPopulation, hugePopulation, hugeOperations, sparsePatches);

        bool storedRepresentationWins = signaturePayload + postingPayload < rawPayload;
        double algebraicSpeedup = generatedScan.NanosecondsPerQuery / algebraicSearch.NanosecondsPerQuery;
        Console.WriteLine();
        Console.WriteLine("Decision");
        Console.WriteLine("--------");
        Console.WriteLine($"Similarity as replacement storage:  {(storedRepresentationWins ? "RETAIN" : "REJECT")}");
        Console.WriteLine("Similarity as stored index:         ORDINARY TRADEOFF (speed purchased with postings)");
        Console.WriteLine($"Similarity as an AT relation:       {(algebraicSpeedup >= 1.25 ? "RETAIN FOR DEEPER TESTING" : "REJECT")}");
        Console.WriteLine("The relation is useful only when its topology has domain meaning; random-digit reuse alone is not evidence.");
        return 0;
    }

    private static void RunIntegratedMemory2Proof(ushort[] blockSignatures, int samples, int scanPopulation, long hugePopulation, int hugeOperations, int sparsePatches)
    {
        Console.WriteLine();
        Console.WriteLine("Integrated Memory2 relational AT");
        Console.WriteLine("--------------------------------");
        var acceptedValues = new int[10][];
        for (int center = 0; center < acceptedValues.Length; center++)
        {
            int bit = 1 << center;
            acceptedValues[center] = Enumerable.Range(0, blockSignatures.Length)
                .Where(value => (blockSignatures[value] & bit) != 0)
                .ToArray();
        }

        AtRelation<int> relation = AtRelation<int>.Create("interlocked-numeric", center => acceptedValues[center]);
        var db = MemoryDb.Create(modulus: 4096);
        Collection<IntegratedRelationRow> rows = db.For<IntegratedRelationRow>();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var plans = new PreparedAtRelation<IntegratedRelationRow>[10];
        for (int center = 0; center < plans.Length; center++)
            plans[center] = rows.PrepareAtRelation(x => x.Block, center, relation, AtQueryOptions.StrictOnly);
        long preparedPlanAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

        PreparedAt<IntegratedRelationRow> seed = rows.PrepareAt(x => x.Block == 0, AtQueryOptions.StrictOnly);
        PreparedProjection<IntegratedRelationRow, int> blockProjection = rows.PrepareProjection(seed, x => x.Block);
        var axisSignatures = new ushort[4096];
        Array.Copy(blockSignatures, axisSignatures, blockSignatures.Length);
        int[] centers = Enumerable.Range(0, 10).ToArray();

        long scanChecksum = RunEngineScan(blockProjection, axisSignatures, centers, scanPopulation);
        long enumerationChecksum = RunEngineEnumerations(plans, centers, scanPopulation);
        long relationChecksum = RunEngineRelations(plans, centers, scanPopulation);
        if (scanChecksum != enumerationChecksum || scanChecksum != relationChecksum)
            throw new InvalidOperationException("Integrated relational AT did not match the actual Memory2 generated-data scan.");
        for (int center = 0; center < plans.Length; center++)
        {
            long expectedCount = CountEngineScan(blockProjection, axisSignatures, center, scanPopulation);
            if (expectedCount != plans[center].Count(0, scanPopulation))
                throw new InvalidOperationException("Integrated relational AT count did not match the actual Memory2 data.");
        }

        Measurement engineScan = Measure(samples, centers.Length,
            () => RunEngineScan(blockProjection, axisSignatures, centers, scanPopulation));
        Measurement engineEnumeration = Measure(samples, centers.Length,
            () => RunEngineEnumerations(plans, centers, scanPopulation));
        Measurement engineRelation = Measure(samples, centers.Length,
            () => RunEngineRelations(plans, centers, scanPopulation));
        Print("Actual Memory2 data scan", engineScan);
        Print("Actual relational ID enumeration", engineEnumeration);
        Print("Actual relational checksum", engineRelation);
        Console.WriteLine($"Integrated enumeration speedup:     {engineScan.NanosecondsPerQuery / engineEnumeration.NanosecondsPerQuery,12:N2}x");
        Console.WriteLine($"Integrated aggregate speedup:       {engineScan.NanosecondsPerQuery / engineRelation.NanosecondsPerQuery,12:N2}x");
        Console.WriteLine($"Prepared plan allocation (10):      {preparedPlanAllocation,12:N0} bytes");
        Console.WriteLine($"Per-record relation storage:        {0,12:N0} bytes");
        if (engineScan.NanosecondsPerQuery / engineEnumeration.NanosecondsPerQuery < 1.05)
            throw new InvalidOperationException("Relational AT enumeration did not beat the generated-data scan by the proof threshold.");
        if (engineScan.NanosecondsPerQuery / engineRelation.NanosecondsPerQuery < 1.20)
            throw new InvalidOperationException("Relational AT aggregation did not beat the generated-data scan by the proof threshold.");
        if (engineRelation.AllocatedBytes != 0)
            throw new InvalidOperationException("The prepared relational aggregate allocated on its hot path.");

        var hugeQueries = new int[hugeOperations];
        for (int i = 0; i < hugeQueries.Length; i++) hugeQueries[i] = i % 10;
        RunEngineRelations(plans, hugeQueries, hugePopulation);
        Measurement huge = Measure(samples, hugeQueries.Length,
            () => RunEngineRelations(plans, hugeQueries, hugePopulation));
        long materializedPostingEntries = 0;
        for (int center = 0; center < plans.Length; center++)
            materializedPostingEntries = checked(materializedPostingEntries + plans[center].Count(0, hugePopulation));
        double materializedPostingBytes = materializedPostingEntries * (double)sizeof(long);
        Console.WriteLine();
        Console.WriteLine($"Huge logical population:            {hugePopulation,12:N0}");
        Console.WriteLine($"Closed-form checksum:                {huge.NanosecondsPerQuery,12:N2} ns/query");
        Console.WriteLine($"Huge-query hot allocations:         {huge.AllocatedBytes,12:N0} bytes");
        Console.WriteLine($"Equivalent 64-bit postings:         {materializedPostingBytes,12:N0} bytes");
        Console.WriteLine($"Plan/postings payload advantage:    {materializedPostingBytes / Math.Max(1.0, preparedPlanAllocation),12:N0}x");
        if (huge.AllocatedBytes != 0)
            throw new InvalidOperationException("The huge-population relation allocated on its hot path.");

        if (sparsePatches > 0)
        {
            long expectedPatchedAggregate = RunEngineRelations(plans, centers, hugePopulation);
            long step = Math.Max(1, hugePopulation / (sparsePatches + 1L));
            for (int i = 0; i < sparsePatches; i++)
            {
                long id = step * (i + 1L);
                IntegratedRelationRow row = rows.Get(id);
                int before = row.Block;
                int after = (i & 1) == 0 ? i % 1000 : 3000 + (i % 1000);
                for (int center = 0; center < acceptedValues.Length; center++)
                {
                    bool matchedBefore = Array.BinarySearch(acceptedValues[center], before) >= 0;
                    bool matchedAfter = Array.BinarySearch(acceptedValues[center], after) >= 0;
                    if (matchedBefore == matchedAfter) continue;
                    expectedPatchedAggregate = matchedAfter
                        ? unchecked(expectedPatchedAggregate + id)
                        : unchecked(expectedPatchedAggregate - id);
                }
                row.Block = after;
                rows.Upsert(row);
            }
            long actualPatchedAggregate = RunEngineRelations(plans, centers, hugePopulation);
            if (actualPatchedAggregate != expectedPatchedAggregate)
                throw new InvalidOperationException("Sparse-patch relational AT reconciliation returned an incorrect result.");
            int patchedOperations = Math.Min(1000, Math.Max(100, hugeOperations / 100));
            var patchedQueries = new int[patchedOperations];
            for (int i = 0; i < patchedQueries.Length; i++) patchedQueries[i] = i % 10;
            RunEngineRelations(plans, patchedQueries, hugePopulation);
            Measurement patched = Measure(samples, patchedQueries.Length,
                () => RunEngineRelations(plans, patchedQueries, hugePopulation));
            Console.WriteLine();
            Console.WriteLine($"Sparse physical patches:            {sparsePatches,12:N0}");
            Console.WriteLine($"Patched checksum:                    {patched.NanosecondsPerQuery,12:N2} ns/query");
            Console.WriteLine($"Patched-query allocations:          {patched.AllocatedBytes,12:N0} bytes/sample");
            if (patched.AllocatedBytes != 0)
                throw new InvalidOperationException("Sparse-patch reconciliation allocated on its hot path.");
        }
        Console.WriteLine("Integrated proof gates:             PASS");
    }

    private static long RunEngineScan(
        PreparedProjection<IntegratedRelationRow, int> projection,
        ushort[] signatures,
        int[] centers,
        int population)
    {
        long checksum = 0;
        for (int q = 0; q < centers.Length; q++)
        {
            int bit = 1 << centers[q];
            for (int id = 0; id < population; id++)
                if ((signatures[projection.ProjectCurrentAt(id)] & bit) != 0)
                    checksum = unchecked(checksum + id);
        }
        return checksum;
    }

    private static long CountEngineScan(
        PreparedProjection<IntegratedRelationRow, int> projection,
        ushort[] signatures,
        int center,
        int population)
    {
        long count = 0;
        int bit = 1 << center;
        for (int id = 0; id < population; id++) if ((signatures[projection.ProjectCurrentAt(id)] & bit) != 0) count++;
        return count;
    }

    private static long RunEngineRelations(PreparedAtRelation<IntegratedRelationRow>[] plans, int[] centers, long population)
    {
        long checksum = 0;
        for (int q = 0; q < centers.Length; q++)
        {
            PreparedAtRelation<IntegratedRelationRow> plan = plans[centers[q]];
            checksum = unchecked(checksum + plan.ChecksumIds(0, population));
        }
        return checksum;
    }

    private static long RunEngineEnumerations(PreparedAtRelation<IntegratedRelationRow>[] plans, int[] centers, long population)
    {
        long checksum = 0;
        for (int q = 0; q < centers.Length; q++)
        {
            foreach (long id in plans[centers[q]].ResolveIds(0, population))
                checksum = unchecked(checksum + id);
        }
        return checksum;
    }

    private static ushort[] BuildNeighborhoods()
    {
        var masks = new ushort[10];
        for (int digit = 0; digit < 10; digit++) masks[digit] = (ushort)(1 << digit);

        AddBoard(masks, new[,]
        {
            { 7, 8, 9 },
            { 4, 5, 6 },
            { 1, 2, 3 }
        });
        AddBoard(masks, new[,]
        {
            { 0, 1 },
            { 5, 9 }
        });
        AddBoard(masks, new[,]
        {
            { 21, 22, 23, 24, 25 },
            { 16, 17, 18, 19, 20 },
            { 11, 12, 13, 14, 15 },
            { 6, 7, 8, 9, 10 },
            { 1, 2, 3, 4, 5 }
        });
        return masks;
    }

    private static void AddBoard(ushort[] masks, int[,] board)
    {
        int rows = board.GetLength(0);
        int columns = board.GetLength(1);
        ReadOnlySpan<int> rowOffsets = stackalloc[] { -1, 1, 0, 0 };
        ReadOnlySpan<int> columnOffsets = stackalloc[] { 0, 0, -1, 1 };
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int center = board[row, column];
            if ((uint)center >= 10u) continue;
            for (int direction = 0; direction < 4; direction++)
            {
                int neighborRow = row + rowOffsets[direction];
                int neighborColumn = column + columnOffsets[direction];
                if ((uint)neighborRow >= (uint)rows || (uint)neighborColumn >= (uint)columns) continue;
                int neighbor = board[neighborRow, neighborColumn];
                if ((uint)neighbor < 10u) masks[center] |= (ushort)(1 << neighbor);
            }
        }
    }

    private static ushort[] BuildBlockSignatures(ushort[] neighborhoods)
    {
        var signatures = new ushort[1000];
        for (int value = 0; value < signatures.Length; value++)
        {
            int hundreds = value / 100;
            int tens = (value / 10) % 10;
            int ones = value % 10;
            ushort signature = 0;
            for (int center = 0; center < 10; center++)
            {
                ushort allowed = neighborhoods[center];
                if ((allowed & (1 << hundreds)) != 0 &&
                    (allowed & (1 << tens)) != 0 &&
                    (allowed & (1 << ones)) != 0)
                    signature |= (ushort)(1 << center);
            }
            signatures[value] = signature;
        }
        return signatures;
    }

    private static int[][] BuildPreparedResidues(ushort[] blockSignatures)
    {
        int inverse = ModularInverse(GeneratedMultiplier, GeneratedModulus);
        var residues = new int[10][];
        for (int center = 0; center < residues.Length; center++)
        {
            int bit = 1 << center;
            residues[center] = Enumerable.Range(0, blockSignatures.Length)
                .Where(value => (blockSignatures[value] & bit) != 0)
                .Select(value => PositiveMod(inverse * (value - GeneratedOffset), GeneratedModulus))
                .OrderBy(value => value)
                .ToArray();
        }
        return residues;
    }

    private static long RunRawScan(ushort[] blocks, ushort[] blockSignatures, int[] queries)
    {
        long checksum = 0;
        for (int q = 0; q < queries.Length; q++)
        {
            int bit = 1 << queries[q];
            for (int id = 0; id < blocks.Length; id++)
                if ((blockSignatures[blocks[id]] & bit) != 0) checksum = unchecked(checksum + id + 1L);
        }
        return checksum;
    }

    private static long RunSignatureScan(ushort[] signatures, int[] queries)
    {
        long checksum = 0;
        for (int q = 0; q < queries.Length; q++)
        {
            int bit = 1 << queries[q];
            for (int id = 0; id < signatures.Length; id++)
                if ((signatures[id] & bit) != 0) checksum = unchecked(checksum + id + 1L);
        }
        return checksum;
    }

    private static long RunPostings(int[][] postings, int[] queries)
    {
        long checksum = 0;
        for (int q = 0; q < queries.Length; q++)
        {
            int[] matches = postings[queries[q]];
            for (int i = 0; i < matches.Length; i++) checksum = unchecked(checksum + matches[i] + 1L);
        }
        return checksum;
    }

    private static long RunGeneratedScan(int population, ushort[] blockSignatures, int[] queries)
    {
        long checksum = 0;
        for (int q = 0; q < queries.Length; q++)
        {
            int bit = 1 << queries[q];
            for (int id = 0; id < population; id++)
            {
                int block = (GeneratedMultiplier * (id % GeneratedModulus) + GeneratedOffset) % GeneratedModulus;
                if ((blockSignatures[block] & bit) != 0) checksum = unchecked(checksum + id + 1L);
            }
        }
        return checksum;
    }

    private static long RunAlgebraicRelation(int population, int[][] preparedResidues, int[] queries)
    {
        long checksum = 0;
        for (int q = 0; q < queries.Length; q++)
        {
            int[] residues = preparedResidues[queries[q]];
            for (int r = 0; r < residues.Length; r++)
                for (int id = residues[r]; id < population; id += GeneratedModulus)
                    checksum = unchecked(checksum + id + 1L);
        }
        return checksum;
    }

    private static void PrintInformationEconomics(ushort[] blockSignatures)
    {
        var classes = new Dictionary<ushort, int>();
        for (int value = 0; value < blockSignatures.Length; value++)
        {
            ushort signature = blockSignatures[value];
            classes.TryGetValue(signature, out int count);
            classes[signature] = count + 1;
        }

        double classEntropy = 0;
        double residualEntropy = 0;
        foreach (int count in classes.Values)
        {
            double probability = count / 1000.0;
            classEntropy -= probability * Math.Log2(probability);
            residualEntropy += probability * Math.Log2(count);
        }

        Console.WriteLine();
        Console.WriteLine("Information economics (uniform 000-999)");
        Console.WriteLine("------------------------------------------");
        Console.WriteLine($"Distinct structural signatures:       {classes.Count,12:N0}");
        Console.WriteLine($"Raw entropy:                           {Math.Log2(1000),12:N4} bits/block");
        Console.WriteLine($"Signature entropy:                     {classEntropy,12:N4} bits/block");
        Console.WriteLine($"Residual entropy given signature:      {residualEntropy,12:N4} bits/block");
        Console.WriteLine($"Signature + residual:                  {classEntropy + residualEntropy,12:N4} bits/block");
    }

    private static void PrintRelation(ushort[] neighborhoods, ushort[] blockSignatures)
    {
        Console.WriteLine("Interlocked numerical similarity proof");
        Console.WriteLine("======================================");
        for (int center = 0; center < neighborhoods.Length; center++)
        {
            string members = string.Join(',', Enumerable.Range(0, 10).Where(d => (neighborhoods[center] & (1 << d)) != 0));
            int matchingBlocks = blockSignatures.Count(signature => (signature & (1 << center)) != 0);
            Console.WriteLine($"Center {center}: {{{members}}}, matching blocks {matchingBlocks:N0}/1,000");
        }
    }

    private static Measurement Measure(int samples, int queryCount, Func<long> workload)
    {
        var results = new Measurement[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            long checksum = workload();
            long elapsed = Stopwatch.GetTimestamp() - started;
            results[sample] = new Measurement(
                elapsed * 1_000_000_000.0 / Stopwatch.Frequency / queryCount,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                checksum);
        }
        Array.Sort(results, static (left, right) => left.NanosecondsPerQuery.CompareTo(right.NanosecondsPerQuery));
        return results[results.Length / 2];
    }

    private static void Print(string name, Measurement measurement)
        => Console.WriteLine($"{name,-37}{measurement.NanosecondsPerQuery / 1_000_000.0,12:N3} ms/query  {measurement.AllocatedBytes,8:N0} B");

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int value)) return value;
        return fallback;
    }

    private static long ReadLong(string[] args, string name, long fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && long.TryParse(args[i + 1], out long value)) return value;
        return fallback;
    }

    private static int ModularInverse(int value, int modulus)
    {
        int previousRemainder = modulus;
        int remainder = PositiveMod(value, modulus);
        int previousCoefficient = 0;
        int coefficient = 1;
        while (remainder != 0)
        {
            int quotient = previousRemainder / remainder;
            (previousRemainder, remainder) = (remainder, previousRemainder - quotient * remainder);
            (previousCoefficient, coefficient) = (coefficient, previousCoefficient - quotient * coefficient);
        }
        if (previousRemainder != 1) throw new InvalidOperationException("The generated law is not invertible.");
        return PositiveMod(previousCoefficient, modulus);
    }

    private static int PositiveMod(int value, int modulus)
    {
        int remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static uint XorShift(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private readonly record struct Measurement(double NanosecondsPerQuery, long AllocatedBytes, long Checksum);

    private sealed class IntegratedRelationRow
    {
        public long Id { get; set; }
        public int Block { get; set; }
        public int Group { get; set; }
    }
}
