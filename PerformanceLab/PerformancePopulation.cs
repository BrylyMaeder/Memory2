using System;

namespace Memory2.PerformanceLab;

// Retained internally for compatibility with isolated legacy stress helpers.
// The unified PerformanceLab no longer exposes or selects a population size.
internal enum PerformancePopulation : long
{
    OneHundredMillion = 100_000_000L,
    TenMillion = 10_000_000L,
    OneMillion = 1_000_000L
}

internal enum EntityRichness
{
    Properties48 = 48,
    Properties24 = 24,
    Properties16 = 16,
    Properties8 = 8,
    Properties4 = 4,
    Properties1 = 1
}

internal enum EntropyRate
{
    Percent100 = 100,
    Percent75 = 75,
    Percent50 = 50,
    Percent25 = 25,
    Percent10 = 10,
    Percent1 = 1
}

internal readonly record struct PerformanceConfiguration(
    long SeedCount,
    EntityRichness Richness,
    EntropyRate Entropy)
{
    internal long PopulationCount => SeedCount;
    internal int PropertyCount => (int)Richness;
    internal int EntropyPercent => (int)Entropy;
}

internal static class PerformancePopulationSelector
{
    internal const long UnifiedLivePopulation = 100_000_000L;

    internal static PerformanceConfiguration Select()
    {
        Console.WriteLine("Memory2 PerformanceLab configuration");
        Console.WriteLine("====================================");
        Console.WriteLine("The unified lab always seeds 100,000,000 live multi-model entities.");
        Console.WriteLine("Memory2 has no caller-declared population ceiling; columns grow with live data.");
        Console.WriteLine("All verification and performance tests run against this single seeded population.");

        EntityRichness richness = SelectRichness();
        EntropyRate entropy = SelectEntropy();
        var config = new PerformanceConfiguration(UnifiedLivePopulation, richness, entropy);

        Console.WriteLine();
        Console.WriteLine("Selected configuration");
        Console.WriteLine($"Actually seeded live:     {config.SeedCount:N0}");
        Console.WriteLine($"Physical scalar columns:  {config.PropertyCount:N0}");
        Console.WriteLine($"Independent entropy rate: {config.EntropyPercent:N0}%");
        Console.WriteLine();
        return config;
    }

    private static EntityRichness SelectRichness()
    {
        Console.WriteLine();
        Console.WriteLine("Physical schema richness (scalar columns actually allocated):");
        Console.WriteLine("1 - 48 properties (default)");
        Console.WriteLine("2 - 24 properties");
        Console.WriteLine("3 - 16 properties");
        Console.WriteLine("4 - 8 properties");
        Console.WriteLine("5 - 4 properties");
        Console.WriteLine("6 - 1 property");
        Console.Write("Choose [48 default]: ");
        return (Console.ReadLine() ?? string.Empty).Trim() switch
        {
            "2" => EntityRichness.Properties24,
            "3" => EntityRichness.Properties16,
            "4" => EntityRichness.Properties8,
            "5" => EntityRichness.Properties4,
            "6" => EntityRichness.Properties1,
            _ => EntityRichness.Properties48
        };
    }

    private static EntropyRate SelectEntropy()
    {
        Console.WriteLine();
        Console.WriteLine("Independent entropy rate:");
        Console.WriteLine("1 - 100% (default)");
        Console.WriteLine("2 - 75%");
        Console.WriteLine("3 - 50%");
        Console.WriteLine("4 - 25%");
        Console.WriteLine("5 - 10%");
        Console.WriteLine("6 - 1%");
        Console.Write("Choose [100% default]: ");
        return (Console.ReadLine() ?? string.Empty).Trim() switch
        {
            "2" => EntropyRate.Percent75,
            "3" => EntropyRate.Percent50,
            "4" => EntropyRate.Percent25,
            "5" => EntropyRate.Percent10,
            "6" => EntropyRate.Percent1,
            _ => EntropyRate.Percent100
        };
    }

    internal static long ToRecordCount(PerformancePopulation population) => (long)population;
    internal static string Format(PerformancePopulation population) => ((long)population).ToString("N0");
}
