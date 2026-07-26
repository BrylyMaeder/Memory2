using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory2.Tests
{
    [TestClass]
    public sealed class LawfulFixedDomainTests
    {
        [TestMethod]
        public void Coordinates_are_arithmetic_and_relationship_free()
        {
            var domain = new LawfulFixedDomain(10, 2, 4);
            Assert.AreEqual(80L, domain.LogicalCount);
            Assert.AreEqual(0, domain.PhysicalNodeRecords);
            Assert.AreEqual(0, domain.PhysicalRelationshipRecords);
            long c = domain.Coordinate(3, 1, 2);
            Assert.AreEqual(3, domain.RootIndex(c));
            Assert.AreEqual(1, domain.IndexAt(c, 0));
            Assert.AreEqual(2, domain.IndexAt(c, 1));
        }

        [TestMethod]
        public void Root_generation_hides_descendants_without_traversal()
        {
            var domain = new LawfulFixedDomain(4, 2, 4);
            long c = domain.Coordinate(2, 1, 3);
            uint before = domain.RootGeneration(2);
            Assert.IsTrue(domain.IsVisible(c));
            domain.DeleteRoot(2);
            Assert.IsFalse(domain.IsVisible(c));
            Assert.AreNotEqual(before, domain.RootGeneration(2));
        }

        [TestMethod]
        public void Packed_columns_round_trip_values_and_stream_without_models()
        {
            var column = new PackedCoordinateColumn<int>(32);
            column.Write(7, 1234);
            Assert.AreEqual(1234, column.Read(7));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                column.Write(writer, (w, v) => w.Write(v));
            Assert.IsTrue(stream.Length > 0);
        }
    }
}
