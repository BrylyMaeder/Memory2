using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests;

[TestClass]
public sealed class WeakIdentityTrackingTests
{
    [TestMethod]
    public void Multiple_materializations_of_the_same_coordinate_remain_independently_saveable()
    {
        using LawfulMemoryStore<TrackedRoot> store = CreateStore(4);
        store.Set().Create(new TrackedRoot { Child = new TrackedChild { Value = 10 } });

        TrackedChild first = store.From<TrackedRoot>().FirstHydrated().Child;
        TrackedChild second = store.From<TrackedRoot>().FirstHydrated().Child;

        first.Value = 20;
        store.Save(first);
        Assert.AreEqual(20, store.From<TrackedChild>().FirstHydrated().Value);

        second.Value = 30;
        store.Save(second);
        Assert.AreEqual(30, store.From<TrackedChild>().FirstHydrated().Value);
    }

    [TestMethod]
    public void Weak_identity_slots_survive_collection_reuse_and_growth()
    {
        const int roots = 20_000;
        using LawfulMemoryStore<TrackedRoot> store = CreateStore(8);
        var models = new TrackedRoot[roots];
        for (int i = 0; i < roots; i++) models[i] = new TrackedRoot { Child = new TrackedChild { Value = i } };
        store.Set().CreateMany(models);

        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < roots; i++) _ = store.From<TrackedChild>().HydrateAt(i);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        TrackedChild retained = store.From<TrackedChild>().HydrateAt(roots - 1);
        retained.Value = 42;
        store.Save(retained);
        Assert.AreEqual(42, store.From<TrackedChild>().HydrateAt(roots - 1).Value);
    }

    [TestMethod]
    public void Concurrent_hydration_publishes_valid_tracking_entries()
    {
        const int roots = 4_096;
        using LawfulMemoryStore<TrackedRoot> store = CreateStore(roots);
        var models = new TrackedRoot[roots];
        for (int i = 0; i < roots; i++) models[i] = new TrackedRoot { Child = new TrackedChild { Value = i } };
        store.Set().CreateMany(models);

        var retained = new ConcurrentBag<TrackedChild>();
        Parallel.For(0, roots, i => retained.Add(store.From<TrackedChild>().HydrateAt(i)));

        int updated = 0;
        foreach (TrackedChild child in retained)
        {
            child.Value = ++updated;
            store.Save(child);
        }
        Assert.AreEqual(roots, updated);
    }

    [TestMethod]
    public void Hydration_reuses_constructor_created_owned_nodes_and_collections()
    {
        using LawfulMemoryStore<ConstructorRoot> store = LawfulMemoryStore.Create<ConstructorRoot>(
            4,
            schema => schema
                .OwnsOne<ConstructorRoot, ConstructorChild>(x => x.Child)
                .OwnsMany<ConstructorRoot, ConstructorItem>(x => x.Items, 2));
        store.Set().CreateMany(new[]
        {
            new ConstructorRoot
            {
                Child = new ConstructorChild { Value = 7 },
                Items = new List<ConstructorItem> { new() { Value = 8 }, new() { Value = 9 } }
            }
        });

        ConstructorChild.Created = 0;
        ConstructorRoot hydrated = store.From<ConstructorRoot>().FirstHydrated();

        Assert.AreEqual(1, ConstructorChild.Created);
        Assert.AreEqual(7, hydrated.Child.Value);
        CollectionAssert.AreEqual(new[] { 8, 9 }, hydrated.Items.ConvertAll(x => x.Value));
    }

    [TestMethod]
    public void Compiled_batch_ingest_preserves_collection_validation()
    {
        using LawfulMemoryStore<ConstructorRoot> store = LawfulMemoryStore.Create<ConstructorRoot>(
            2,
            schema => schema
                .OwnsOne<ConstructorRoot, ConstructorChild>(x => x.Child)
                .OwnsMany<ConstructorRoot, ConstructorItem>(x => x.Items, 2));

        var invalid = new ConstructorRoot { Items = null! };
        Assert.ThrowsException<InvalidOperationException>(() => store.Set().CreateMany(new[] { invalid }));
    }

    private static LawfulMemoryStore<TrackedRoot> CreateStore(int capacity)
        => LawfulMemoryStore.Create<TrackedRoot>(
            capacity,
            schema => schema.OwnsOne<TrackedRoot, TrackedChild>(x => x.Child));

    private sealed class TrackedRoot
    {
        public long Id { get; set; }
        public TrackedChild Child { get; set; } = new();
    }

    private sealed class TrackedChild
    {
        public int Value { get; set; }
    }

    private sealed class ConstructorRoot
    {
        public long Id { get; set; }
        public ConstructorChild Child { get; set; } = new();
        public List<ConstructorItem> Items { get; set; } = new();
    }

    private sealed class ConstructorChild
    {
        public ConstructorChild() => Created++;
        public static int Created { get; set; }
        public int Value { get; set; }
    }

    private sealed class ConstructorItem
    {
        public int Value { get; set; }
    }
}
