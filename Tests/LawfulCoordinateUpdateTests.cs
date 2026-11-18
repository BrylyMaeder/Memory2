using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests;

[TestClass]
public sealed class LawfulCoordinateUpdateTests
{
    [TestMethod]
    public void Linq_update_changes_only_selected_coordinates_without_hydration()
    {
        using LawfulMemoryStore<UpdateRow> store = CreateStore(16);
        Seed(store, 10);
        long materializations = store.Materializations;

        int updated = store.From<UpdateRow>()
            .Where(x => x.Active && x.Score >= 4)
            .Update(x => new UpdateRow
            {
                Score = x.Score + 100,
                Name = x.Name + "!"
            });

        Assert.AreEqual(3, updated);
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 3, 104, 5, 106, 7, 108, 9 },
            store.From<UpdateRow>().Select(x => x.Score).ToArray());
        CollectionAssert.AreEqual(
            new[] { "0", "1", "2", "3", "4!", "5", "6!", "7", "8!", "9" },
            store.From<UpdateRow>().Select(x => x.Name).ToArray());
        Assert.AreEqual(materializations, store.Materializations);
    }

    [TestMethod]
    public void Multiple_assignments_read_the_same_old_row()
    {
        using LawfulMemoryStore<UpdateRow> store = CreateStore(4);
        store.Set().CreateMany(new[]
        {
            new UpdateRow { Score = 10, Other = 20, Optional = 7, Name = "before" }
        });

        store.From<UpdateRow>().Update(x => new UpdateRow
        {
            Score = x.Other,
            Other = x.Score,
            Optional = x.Optional + 1
        });

        UpdateRow result = store.From<UpdateRow>().FirstHydrated();
        Assert.AreEqual(20, result.Score);
        Assert.AreEqual(10, result.Other);
        Assert.AreEqual(8, result.Optional);
        Assert.AreEqual("before", result.Name, "Omitted columns must remain unchanged.");
    }

    [TestMethod]
    public void Prepared_update_is_reusable_and_allocation_free()
    {
        const int count = 128;
        using LawfulMemoryStore<UpdateRow> store = CreateStore(count);
        Seed(store, count);
        LawfulPreparedUpdatePlan<UpdateRow, UpdateRow> plan = store.From<UpdateRow>()
            .PrepareUpdate(x => new UpdateRow { Score = x.Score + 1, Active = !x.Active });

        plan.Execute();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Assert.AreEqual(count, plan.Execute());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(count, plan.TargetCount);
        Assert.AreEqual(2, plan.AssignedColumnCount);
        Assert.AreEqual(101, store.From<UpdateRow>().Select(x => x.Score).ToArray()[0]);
    }

    [TestMethod]
    public void Coordinate_update_preserves_changed_column_subscription_routing()
    {
        using LawfulMemoryStore<UpdateRow> store = CreateStore(8);
        Seed(store, 8);
        var changes = new List<LawfulQueryChange<long>>();
        using IDisposable subscription = store.From<UpdateRow>()
            .Where(x => x.Active && x.Score >= 5)
            .Select(x => x.Id)
            .SubscribeChanges(changes.Add);
        long materializations = store.Materializations;

        int updated = store.From<UpdateRow>()
            .Where(x => !x.Active && x.Score >= 5)
            .Update(x => new UpdateRow { Active = true, Other = x.Other + 1 });

        Assert.AreEqual(2, updated);
        CollectionAssert.AreEqual(new long[] { 6, 8 }, changes.Select(x => x.Current).ToArray());
        CollectionAssert.AreEqual(
            new[] { LawfulTransitionKind.Entered, LawfulTransitionKind.Entered },
            changes.Select(x => x.Kind).ToArray());
        Assert.AreEqual(materializations, store.Materializations);
    }

    [TestMethod]
    public void Prepared_update_reacquires_columns_after_store_growth()
    {
        using LawfulMemoryStore<UpdateRow> store = CreateStore(2);
        Seed(store, 2);
        LawfulPreparedUpdatePlan<UpdateRow, UpdateRow> firstTwo = store.From<UpdateRow>()
            .PrepareUpdate(x => new UpdateRow { Score = x.Score + 100 });

        var more = new UpdateRow[1_000];
        for (int i = 0; i < more.Length; i++) more[i] = new UpdateRow { Score = i + 10 };
        store.Set().CreateMany(more);
        Assert.AreEqual(2, firstTwo.Execute());

        int[] scores = store.From<UpdateRow>().Select(x => x.Score).ToArray();
        CollectionAssert.AreEqual(new[] { 100, 101 }, scores.Take(2).ToArray());
        Assert.AreEqual(10, scores[2]);
        Assert.AreEqual(1_009, scores[^1]);
    }

    [TestMethod]
    public void Coordinate_update_consequences_can_issue_nested_coordinate_updates()
    {
        using LawfulMemoryStore<UpdateRow> store = CreateStore(4);
        Seed(store, 2);
        int scoreEntries = 0;
        using IDisposable score = store.From<UpdateRow>()
            .Where(x => x.Score >= 10)
            .Select(x => x.Id)
            .Subscribe(_ => scoreEntries++);
        using IDisposable active = store.From<UpdateRow>()
            .Where(x => x.Active)
            .Select(x => x.Id)
            .Subscribe(id => store.From<UpdateRow>()
                .Where(x => x.Id == id)
                .Update(x => new UpdateRow { Score = x.Score + 10 }));

        store.From<UpdateRow>()
            .Where(x => !x.Active)
            .Update(x => new UpdateRow { Active = true });

        Assert.AreEqual(1, scoreEntries);
        CollectionAssert.AreEqual(new[] { 0, 11 }, store.From<UpdateRow>().Select(x => x.Score).ToArray());
        Assert.AreEqual(0L, store.Materializations);
    }

    [TestMethod]
    public void Invalid_update_shapes_are_rejected_at_compile_time()
    {
        using LawfulMemoryStore<UpdateRow> store = CreateStore(2);
        store.Set().Create(new UpdateRow());

        Assert.ThrowsException<NotSupportedException>(() =>
            store.From<UpdateRow>().PrepareUpdate(x => x));
        Assert.ThrowsException<NotSupportedException>(() =>
            store.From<UpdateRow>().PrepareUpdate(x => new UpdateRow { Unstored = x.Unstored + 1 }));
    }

    [TestMethod]
    public void Prepared_creation_writes_complete_owned_graphs_without_constructing_models()
    {
        using LawfulMemoryStore<CreateRoot> store = CreateGraphStore(2);
        LawfulPreparedCreatePlan<CreateRoot> plan = store.Set().PrepareCreate(i => new CreateRoot
        {
            Score = i * 10,
            Active = (i & 1) == 0,
            Profile = new CreateProfile { Band = i + 100 },
            Items = new List<CreateItem>
            {
                new CreateItem { Sequence = i * 2L, Metadata = new CreateMetadata { Code = i + 1 } },
                new CreateItem { Sequence = i * 2L + 1, Metadata = new CreateMetadata { Code = i + 2 } }
            }
        });
        long materializations = store.Materializations;

        Assert.AreEqual(3, plan.Execute(40, 3));
        Assert.AreEqual(3, store.Count);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, store.From<CreateRoot>().Select(x => x.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 400, 410, 420 }, store.From<CreateRoot>().Select(x => x.Score).ToArray());
        CollectionAssert.AreEqual(new[] { 140, 141, 142 }, store.From<CreateProfile>().Select(x => x.Band).ToArray());
        CollectionAssert.AreEqual(new long[] { 80, 81, 82, 83, 84, 85 }, store.From<CreateItem>().Select(x => x.Sequence).ToArray());
        CollectionAssert.AreEqual(new[] { 41, 42, 42, 43, 43, 44 }, store.From<CreateMetadata>().Select(x => x.Code).ToArray());
        Assert.AreEqual(materializations, store.Materializations);

        CreateRoot hydrated = store.From<CreateRoot>().HydrateAt(1);
        Assert.AreEqual(410, hydrated.Score);
        Assert.AreEqual(141, hydrated.Profile.Band);
        Assert.AreEqual(82L, hydrated.Items[0].Sequence);
        Assert.AreEqual(43, hydrated.Items[1].Metadata.Code);
    }

    [TestMethod]
    public void Prepared_creation_survives_capacity_growth_and_allocates_nothing_at_execution()
    {
        using LawfulMemoryStore<CreateRoot> store = CreateGraphStore(2);
        LawfulPreparedCreatePlan<CreateRoot> plan = store.Set().PrepareCreate(i => new CreateRoot
        {
            Score = i,
            Profile = new CreateProfile { Band = i },
            Items = new List<CreateItem>
            {
                new CreateItem { Sequence = i, Metadata = new CreateMetadata { Code = i } },
                new CreateItem { Sequence = i + 1, Metadata = new CreateMetadata { Code = i + 1 } }
            }
        });

        Assert.AreEqual(1_000, plan.Execute(1_000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(24, plan.Execute(1_000, 24));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(1_024, store.Count);
        Assert.AreEqual(999, store.From<CreateRoot>().Select(x => x.Score).ToArray()[999]);
    }

    [TestMethod]
    public void Coordinate_creation_rejects_incomplete_fixed_collections()
    {
        using LawfulMemoryStore<CreateRoot> store = CreateGraphStore(2);
        Assert.ThrowsException<InvalidOperationException>(() => store.Set().PrepareCreate(i => new CreateRoot
        {
            Profile = new CreateProfile(),
            Items = new List<CreateItem> { new CreateItem() }
        }));
    }

    private static LawfulMemoryStore<UpdateRow> CreateStore(int capacity)
        => LawfulMemoryStore.Create<UpdateRow>(capacity, schema => schema
            .IncludeScalar<UpdateRow, long>(x => x.Id)
            .IncludeScalar<UpdateRow, int>(x => x.Score)
            .IncludeScalar<UpdateRow, int>(x => x.Other)
            .IncludeScalar<UpdateRow, int?>(x => x.Optional)
            .IncludeScalar<UpdateRow, bool>(x => x.Active)
            .IncludeScalar<UpdateRow, string>(x => x.Name));

    private static LawfulMemoryStore<CreateRoot> CreateGraphStore(int capacity)
        => LawfulMemoryStore.Create<CreateRoot>(capacity, schema => schema
            .OwnsOne<CreateRoot, CreateProfile>(x => x.Profile)
            .OwnsMany<CreateRoot, CreateItem>(x => x.Items, 2)
            .OwnsOne<CreateItem, CreateMetadata>(x => x.Metadata)
            .IncludeScalar<CreateRoot, long>(x => x.Id)
            .IncludeScalar<CreateRoot, int>(x => x.Score)
            .IncludeScalar<CreateRoot, bool>(x => x.Active)
            .IncludeScalar<CreateProfile, int>(x => x.Band)
            .IncludeScalar<CreateItem, long>(x => x.Sequence)
            .IncludeScalar<CreateMetadata, int>(x => x.Code));

    private static void Seed(LawfulMemoryStore<UpdateRow> store, int count)
    {
        var rows = new UpdateRow[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = new UpdateRow
            {
                Score = i,
                Other = i * 10,
                Optional = (i & 1) == 0 ? i : null,
                Active = (i & 1) == 0,
                Name = i.ToString()
            };
        }
        store.Set().CreateMany(rows);
    }

    public sealed class UpdateRow
    {
        public long Id { get; set; }
        public int Score { get; set; }
        public int Other { get; set; }
        public int? Optional { get; set; }
        public bool Active { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Unstored { get; set; }
    }

    public sealed class CreateRoot
    {
        public long Id { get; set; }
        public int Score { get; set; }
        public bool Active { get; set; }
        public CreateProfile Profile { get; set; } = new();
        public List<CreateItem> Items { get; set; } = new();
    }

    public sealed class CreateProfile
    {
        public int Band { get; set; }
    }

    public sealed class CreateItem
    {
        public long Sequence { get; set; }
        public CreateMetadata Metadata { get; set; } = new();
    }

    public sealed class CreateMetadata
    {
        public int Code { get; set; }
    }
}
