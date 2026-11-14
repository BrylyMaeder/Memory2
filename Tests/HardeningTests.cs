using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Memory2.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class HardeningTests
    {
        [TestMethod]
        public void SavingReopenedStorePreservesSetsThatWereNotMaterialized()
        {
            using var stream = new MemoryStream();
            using (var store = MemoryStore.Open(stream, leaveOpen: true))
            {
                store.Set<Customer>().Create(new Customer { Name = "Ada" });
                store.Set<Order>().Create(new Order { Reference = "ORD-1", Total = 42.25m });
                store.Save();
            }

            using (var reopened = MemoryStore.Open(stream, leaveOpen: true))
            {
                Assert.AreEqual("Ada", reopened.Set<Customer>().Load(1).Name);
                // Order deliberately remains deferred while the file is replaced.
                reopened.Save();
            }

            using var final = MemoryStore.Open(stream, leaveOpen: true);
            Assert.AreEqual("Ada", final.Set<Customer>().Load(1).Name);
            Assert.AreEqual("ORD-1", final.Set<Order>().Load(1).Reference);
            Assert.AreEqual(42.25m, final.Set<Order>().Load(1).Total);
        }

        [TestMethod]
        public void VerificationReadsThePayloadAndRejectsTruncation()
        {
            using var stream = new MemoryStream();
            using var store = MemoryStore.Open(stream, leaveOpen: true);
            store.Set<Customer>().Create(new Customer { Name = "payload" });
            store.Save();
            Assert.IsTrue(store.Verify().IsValid);

            stream.SetLength(stream.Length - 1);
            Assert.IsFalse(store.Verify().IsValid);
        }

        [TestMethod]
        public void FailedFileOpenDoesNotLeakTheHandle()
        {
            string path = Path.Combine(Path.GetTempPath(), "memory2-invalid-" + Guid.NewGuid().ToString("N") + ".m2");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            try
            {
                Assert.ThrowsException<InvalidDataException>(() => MemoryStore.Open(path));
                File.Delete(path);
                Assert.IsFalse(File.Exists(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void SynchronizationReplayCannotReplaceAcceptedState()
        {
            using var store = MemoryStore.CreateInMemory();
            var set = store.Set<ReplicatedAccount>();
            var accepted = new ReplicatedAccount { Id = 7, Owner = "accepted", Band = 1 };
            var divergentReplay = new ReplicatedAccount { Id = 7, Owner = "replayed", Band = 9 };

            Assert.AreEqual(SynchronizationStatus.Applied,
                set.ApplySynchronization(new SynchronizationEnvelope<ReplicatedAccount>("peer", 1, 7, accepted)).Status);
            Assert.AreEqual(SynchronizationStatus.AlreadyApplied,
                set.ApplySynchronization(new SynchronizationEnvelope<ReplicatedAccount>("peer", 1, 7, divergentReplay)).Status);
            Assert.AreEqual("accepted", set.Load(7).Owner);
            Assert.AreEqual(1, set.Load(7).Band);
        }

        [TestMethod]
        public void SynchronizationReplayLedgerAndMutationVersionSurviveReopen()
        {
            using var stream = new MemoryStream();
            long acceptedVersion;
            using (var store = MemoryStore.Open(stream, leaveOpen: true))
            {
                SynchronizationResult accepted = store.Set<ReplicatedAccount>().ApplySynchronization(
                    new SynchronizationEnvelope<ReplicatedAccount>("peer", 1, 7,
                        new ReplicatedAccount { Id = 7, Owner = "accepted", Band = 1 }));
                acceptedVersion = accepted.MutationVersion;
                store.Save();
            }

            using var reopened = MemoryStore.Open(stream, leaveOpen: true);
            var set = reopened.Set<ReplicatedAccount>();
            SynchronizationResult replay = set.ApplySynchronization(
                new SynchronizationEnvelope<ReplicatedAccount>("peer", 1, 7,
                    new ReplicatedAccount { Id = 7, Owner = "divergent", Band = 9 }));
            Assert.AreEqual(SynchronizationStatus.AlreadyApplied, replay.Status);
            Assert.AreEqual("accepted", set.Load(7).Owner);

            SynchronizationResult next = set.ApplySynchronization(
                new SynchronizationEnvelope<ReplicatedAccount>("peer", 2, 7,
                    new ReplicatedAccount { Id = 7, Owner = "next", Band = 2 }, acceptedVersion));
            Assert.AreEqual(SynchronizationStatus.Applied, next.Status);
            Assert.AreEqual("next", set.Load(7).Owner);
        }

        [TestMethod]
        public void QueryAtIncludesMatchesCreatedOnlyBySparsePatch()
        {
            var db = MemoryDb.Create(modulus: 4096);
            var records = db.For<AtPatchRecord>();
            const long id = 123;
            const int patchedScore = 900_001;
            AtPatchRecord patched = records.Get(id);
            patched.Score = patchedScore;
            records.Upsert(patched);

            AtPatchRecord hit = records.Query(
                x => x.Id == id && x.Score == patchedScore,
                new QueryOptions(strict: true, take: 1)).Single();

            Assert.AreEqual(id, hit.Id);
            Assert.AreEqual(patchedScore, hit.Score);
        }

        [TestMethod]
        public void EpochChangesRemainInvertibleAndInvalidatePreparedAtResults()
        {
            var db = MemoryDb.Create(modulus: 4096);
            Collection<AtPatchRecord> records = db.For<AtPatchRecord>();
            const long originalId = 123;
            int originalScore = records.Get(originalId).Score;
            PreparedAt<AtPatchRecord> originalQuery = records.PrepareAt(x => x.Score == originalScore);
            Assert.AreEqual(originalId, originalQuery.ResolveId());

            db.ApplyEpoch(42);

            AtPatchRecord changed = records.Get(originalId);
            Assert.AreNotEqual(originalScore, changed.Score);
            Assert.AreEqual(originalId, records.GetAt(x => x.Score == changed.Score).Id,
                "The epoch-transformed code must still invert to the original coordinate.");
            Assert.AreEqual(originalId,
                records.GetAt(x => x.Score == changed.Score && x.Active == changed.Active).Id,
                "A non-bijective boolean term must verify the solved coordinate without creating a false contradiction.");

            long relocatedId = originalQuery.ResolveId();
            Assert.AreNotEqual(originalId, relocatedId,
                "A prepared result must not retain its pre-epoch coordinate.");
            Assert.AreEqual(originalScore, records.Get(relocatedId).Score);
        }

        [TestMethod]
        public void ConcurrentFirstAccessUsesOneCanonicalTable()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var db = MemoryDb.Create();
                var collections = Enumerable.Range(0, 16).Select(_ => db.For<ConcurrentRecord>()).ToArray();
                Parallel.For(0, collections.Length, i =>
                    collections[i].Upsert(new ConcurrentRecord { Id = i + 1, Marker = 10_000 + i }));

                var canonical = db.For<ConcurrentRecord>();
                for (int i = 0; i < collections.Length; i++)
                    Assert.AreEqual(10_000 + i, canonical.Get(i + 1).Marker);
            }
        }

        [TestMethod]
        public void LawfulStoreReopensColumnsGraphsMutationsAndDeletionLaw()
        {
            using var stream = new MemoryStream();
            LawfulScalarMutation mutation;
            using (var store = OpenLawful(stream))
            {
                for (int root = 0; root < 3; root++)
                {
                    store.Set().Create(new LawfulRoot
                    {
                        Name = "root-" + root,
                        Children =
                        {
                            new LawfulChild { Label = "a-" + root, Score = root * 10 + 1, Metadata = new LawfulMetadata { Active = true } },
                            new LawfulChild { Label = "b-" + root, Score = root * 10 + 2, Metadata = new LawfulMetadata { Active = false } }
                        }
                    });
                }

                LawfulChild changed = store.From<LawfulChild>().Where(x => x.Score == 22).FirstHydrated();
                changed.Score = 777;
                changed.Metadata.Active = true;
                mutation = store.CreateMutation(changed);
                Assert.IsTrue(store.Apply(mutation));
                store.Save(changed.Metadata);
                store.Set().Delete(2);
                store.Save();
            }

            using var reopened = OpenLawful(stream);
            Assert.AreEqual(3, reopened.Count);
            Assert.AreEqual("root-0", reopened.Set().Load(1).Name);
            Assert.ThrowsException<System.Collections.Generic.KeyNotFoundException>(() => reopened.Set().Load(2));
            LawfulChild restored = reopened.From<LawfulChild>().Where(x => x.Score == 777).SingleHydrated();
            Assert.AreEqual("b-2", restored.Label);
            Assert.IsTrue(restored.Metadata.Active);
            Assert.AreEqual(4, reopened.From<LawfulChild>().Count);
            Assert.IsFalse(reopened.Apply(mutation), "Applied mutation IDs must remain idempotent after reopen.");
        }

        [TestMethod]
        public void LawfulDeleteCountsOnlyNewlyExcludedRoots()
        {
            using var store = LawfulMemoryStore.Create<LawfulRoot>(8, _ => { });
            for (int i = 0; i < 5; i++)
                store.Set().Create(new LawfulRoot { Name = "root-" + i });

            Assert.AreEqual(2, store.Set().DeleteRange(2, 2));
            Assert.AreEqual(0, store.Set().DeleteRange(2, 2));
            Assert.AreEqual(2, store.Set().DeleteRange(1, 4));
            Assert.AreEqual(1, store.From<LawfulRoot>().Count);
        }

        [TestMethod]
        public void FacadeSubscriptionsTreatCreateAndDeleteAsRealTransitions()
        {
            using var store = MemoryStore.CreateInMemory();
            MemorySet<ReplicatedAccount> set = store.Set<ReplicatedAccount>();
            var changes = new System.Collections.Generic.List<SubscriptionChange<ReplicatedAccount>>();

            using (set.Subscribe(x => x.Band >= 7, changes.Add))
            {
                set.Create(new ReplicatedAccount { Owner = "new", Band = 9 });
                Assert.IsTrue(set.Delete(1));
            }

            CollectionAssert.AreEqual(
                new[] { SubscriptionChangeKind.Entered, SubscriptionChangeKind.Left },
                changes.Select(x => x.Kind).ToArray());
            Assert.IsNull(changes[0].Before);
            Assert.IsNotNull(changes[0].After);
            Assert.IsNotNull(changes[1].Before);
            Assert.IsNull(changes[1].After);
            Assert.AreEqual(0, set.GetStatistics().ActiveSubscriptions);
            Assert.AreEqual(2L, set.GetStatistics().SubscriptionEvents);
        }

        private static LawfulMemoryStore<LawfulRoot> OpenLawful(Stream stream)
            => LawfulMemoryStore.Create<LawfulRoot>(2, schema => schema
                .OwnsMany<LawfulRoot, LawfulChild>(x => x.Children, 2)
                .OwnsOne<LawfulChild, LawfulMetadata>(x => x.Metadata), stream, leaveOpen: true);

        public sealed class AtPatchRecord
        {
            public long Id { get; set; }
            public int Score { get; set; }
            public bool Active { get; set; }
        }

        public sealed class ConcurrentRecord
        {
            public long Id { get; set; }
            public int Marker { get; set; }
        }

        public sealed class LawfulRoot
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public System.Collections.Generic.List<LawfulChild> Children { get; set; } = new();
        }

        public sealed class LawfulChild
        {
            public string Label { get; set; }
            public int Score { get; set; }
            public LawfulMetadata Metadata { get; set; } = new();
        }

        public sealed class LawfulMetadata
        {
            public bool Active { get; set; }
        }
    }
}
