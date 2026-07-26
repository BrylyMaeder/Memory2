using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests;

[TestClass]
public sealed class MixedStructurePerformanceSurfaceTests
{
    [TestMethod]
    public void Ten_million_style_mixed_shape_uses_only_lawful_coordinates_and_public_plans()
    {
        using var stream = new MemoryStream();
        using var store = LawfulMemoryStore.Create<MixedTestRoot>(1024, schema => schema
            .OwnsOne<MixedTestRoot, MixedTestProfile>(x => x.Profile)
            .OwnsMany<MixedTestRoot, MixedTestEvent>(x => x.Events, 2)
            .OwnsOne<MixedTestEvent, MixedTestMetadata>(x => x.Metadata), stream, leaveOpen: true);

        var roots = new MixedTestRoot[1024];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = new MixedTestRoot
            {
                Entropy = unchecked((long)((uint)i * 2654435761u)),
                Segment = i & 255,
                Active = (i & 7) != 0,
                Profile = new MixedTestProfile { Region = "r-" + (i & 15), Verified = (i & 3) != 0 },
                Events =
                {
                    new MixedTestEvent { Amount = i * 17L, Accepted = true, Metadata = new MixedTestMetadata { Fingerprint = i * 31L, Suspicious = false } },
                    new MixedTestEvent { Amount = i * 19L, Accepted = (i & 1) == 0, Metadata = new MixedTestMetadata { Fingerprint = i * 37L, Suspicious = (i & 63) == 0 } }
                }
            };
        }
        var set = store.Set();
        set.CreateMany(roots);

        var reader = set.PrepareRead(x => new MixedTestPoint(x.Entropy, x.Segment, x.Active));
        var rootPlan = store.From<MixedTestRoot>().PrepareWhereSelectCount(x => x.Active && x.Segment == 7, x => x.Entropy);
        var eventPlan = store.From<MixedTestEvent>().PrepareWhereSelectCount(x => x.Accepted && x.Amount > 100, x => x.Amount);
        var correlated = store.PrepareCorrelatedQuery<MixedTestEvent, MixedTestMetadata>(x => x.Accepted, x => x.Suspicious);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (int i = 0; i < 10_000; i++) checksum ^= reader.ReadRequired((i & 1023) + 1L).Entropy;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(rootPlan.ExecuteCount() > 0);
        Assert.IsTrue(eventPlan.ExecuteCount() > 0);
        Assert.AreNotEqual(0L, correlated.ExecuteChecksum());
        Assert.AreEqual(1024 * 6L, store.LogicalCoordinates);
        Assert.AreEqual(0L, store.PhysicalNodeRecords);
        Assert.AreEqual(0L, store.PhysicalRelationshipRecords);
        Assert.AreNotEqual(0L, checksum);

        MixedTestEvent model = store.Query<MixedTestEvent>().First();
        model.Amount++;
        store.Save(model);
        Assert.AreEqual(1L, store.DirectWrites);

        var delete = set.PrepareDelete(new long[] { 1, 2, 3, 4 });
        delete.Execute();
        Assert.AreEqual(1, delete.LawMutationCount);

        store.Save();
        Assert.IsTrue(stream.Length > 0);
    }
}

internal readonly record struct MixedTestPoint(long Entropy, int Segment, bool Active);
internal sealed class MixedTestRoot
{
    public long Id { get; set; }
    public long Entropy { get; set; }
    public int Segment { get; set; }
    public bool Active { get; set; }
    public MixedTestProfile Profile { get; set; } = new();
    public List<MixedTestEvent> Events { get; set; } = new();
}
internal sealed class MixedTestProfile { public string Region { get; set; } = string.Empty; public bool Verified { get; set; } }
internal sealed class MixedTestEvent { public long Amount { get; set; } public bool Accepted { get; set; } public MixedTestMetadata Metadata { get; set; } = new(); }
internal sealed class MixedTestMetadata { public long Fingerprint { get; set; } public bool Suspicious { get; set; } }
