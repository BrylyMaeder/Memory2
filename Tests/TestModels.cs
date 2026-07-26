using System;
using System.Collections.Generic;
using System.IO;

namespace Memory2.Tests
{
    internal static class TestStore
    {
        internal static MemoryStore Open(Stream stream = null, bool leaveOpen = false) =>
            stream == null
                ? MemoryStore.CreateInMemory()
                : MemoryStore.Open(stream, leaveOpen);
    }

    public sealed class Customer { public long Id { get; set; } public string Name { get; set; } public bool Vip { get; set; } }
    public sealed class Order { public long Id { get; set; } public long CustomerId { get; set; } public string Reference { get; set; } public decimal Total { get; set; } }
    public sealed class Payment { public long Id { get; set; } public long OrderId { get; set; } public string Status { get; set; } }
    public sealed class Shipment { public long Id { get; set; } public long OrderId { get; set; } public string Status { get; set; } }
    public sealed class FraudHold { public long Id { get; set; } public long CustomerId { get; set; } public bool Active { get; set; } public string Reason { get; set; } }
    public sealed class SupportTicket { public long Id { get; set; } public long CustomerId { get; set; } public string Subject { get; set; } public bool Urgent { get; set; } public bool Resolved { get; set; } }
    public sealed class OperationalEvent { public long Id { get; set; } public string Topic { get; set; } public string Payload { get; set; } public int Severity { get; set; } public bool Ready { get; set; } }
    public sealed class ReplicatedAccount { public long Id { get; set; } public string Owner { get; set; } public int Band { get; set; } }
    public sealed class OperationsAction { public int Priority { get; set; } public string Action { get; set; } public string Subject { get; set; } public long CustomerId { get; set; } public decimal Value { get; set; } }
}

namespace Memory2.Tests
{
    public enum CrayonColor { Red, Blue, Green, Yellow }
    public sealed class CrayonMetadata
    {
        public string Location { get; set; }
        public int PressureBand { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }
    public sealed class CrayonUsage
    {
        public DateTime UsedAt { get; set; }
        public int Minutes { get; set; }
        public CrayonMetadata Metadata { get; set; } = new CrayonMetadata();
    }
    public sealed class Crayon
    {
        public string Name { get; set; }
        public CrayonColor Color { get; set; }
        public List<CrayonUsage> UsageHistory { get; set; } = new List<CrayonUsage>();
    }
    public sealed class CrayonBox
    {
        public long Id { get; set; }
        public string Label { get; set; }
        public List<Crayon> Crayons { get; set; } = new List<Crayon>();
    }
}

namespace Memory2.Tests
{
    public enum GraphQuality { Unknown, Student, Artist, Professional }
    public sealed class GraphLeaf
    {
        public string Code { get; set; }
        public GraphQuality Quality { get; set; }
        public int? OptionalScore { get; set; }
        public Guid Token { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public sbyte SignedByte { get; set; }
        public ushort UnsignedShort { get; set; }
        public uint UnsignedInt { get; set; }
        public ulong UnsignedLong { get; set; }
        public char Marker { get; set; }
        public Uri Link { get; set; }
        public HashSet<string> Labels { get; set; } = new HashSet<string>();
    }
    public sealed class GraphBranch
    {
        public string Name { get; set; }
        public GraphLeaf Primary { get; set; } = new GraphLeaf();
        public GraphLeaf[] ArrayLeaves { get; set; } = Array.Empty<GraphLeaf>();
        public List<GraphLeaf> ListLeaves { get; set; } = new List<GraphLeaf>();
        public Dictionary<string, GraphLeaf> NamedLeaves { get; set; } = new Dictionary<string, GraphLeaf>();
        public Dictionary<string, List<GraphLeaf>> Buckets { get; set; } = new Dictionary<string, List<GraphLeaf>>();
        public List<int> Numbers { get; set; } = new List<int>();
        public Dictionary<string, int> ScalarMap { get; set; } = new Dictionary<string, int>();
    }
    public sealed class GraphWorld
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public GraphBranch Branch { get; set; } = new GraphBranch();
        public List<GraphBranch> Branches { get; set; } = new List<GraphBranch>();
        public GraphBranch[] BranchArray { get; set; } = Array.Empty<GraphBranch>();
        public Dictionary<string, GraphBranch> BranchMap { get; set; } = new Dictionary<string, GraphBranch>();
    }
}

namespace Memory2.Tests
{
    public sealed class ExceptionalMetadata
    {
        public bool Track { get; set; }
        public CrayonColor Marker { get; set; }
    }
    public sealed class ExceptionalUsage
    {
        public int Minutes { get; set; }
        public ExceptionalMetadata Metadata { get; set; } = new ExceptionalMetadata();
    }
    public sealed class ExceptionalTire
    {
        public string Serial { get; set; }
        public CrayonColor Color { get; set; }
        public int Pressure { get; set; }
        public bool Active { get; set; }
        public List<ExceptionalUsage> Usage { get; set; } = new List<ExceptionalUsage>();
    }
    public sealed class ExceptionalCar
    {
        public string Vin { get; set; }
        public List<ExceptionalTire> Tires { get; set; } = new List<ExceptionalTire>();
    }
    public sealed class ExceptionalWorld
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public List<ExceptionalCar> Cars { get; set; } = new List<ExceptionalCar>();
    }
}
