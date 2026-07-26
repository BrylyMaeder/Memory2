using System;
using System.Collections.Generic;
using System.IO;
using Memory2;

namespace Memory2.PerformanceLab;

internal sealed class LawfulWorldMill
{
    private readonly RootVisibilityLaw _rootVisibility;
    private readonly LawfulFixedDomain _cars;
    private readonly LawfulFixedDomain _tires;
    private readonly LawfulFixedDomain _uses;
    private readonly PackedCoordinateColumn<byte> _tireColor;
    private readonly PackedCoordinateColumn<int> _tirePressure;
    private readonly PackedCoordinateColumn<byte> _tireActive;
    private readonly PackedCoordinateColumn<byte> _metadataMarker;
    private readonly PackedCoordinateColumn<byte> _metadataTrack;

    public LawfulWorldMill(int rootCount)
    {
        RootCount = rootCount;
        _rootVisibility = new RootVisibilityLaw();
        _cars = new LawfulFixedDomain(_rootVisibility, rootCount, 2);
        _tires = new LawfulFixedDomain(_rootVisibility, rootCount, 2, 4);
        _uses = new LawfulFixedDomain(_rootVisibility, rootCount, 2, 4, 1);
        _tireColor = new PackedCoordinateColumn<byte>(_tires.LogicalCount);
        _tirePressure = new PackedCoordinateColumn<int>(_tires.LogicalCount);
        _tireActive = new PackedCoordinateColumn<byte>(_tires.LogicalCount);
        _metadataMarker = new PackedCoordinateColumn<byte>(_uses.LogicalCount);
        _metadataTrack = new PackedCoordinateColumn<byte>(_uses.LogicalCount);
    }

    public int RootCount { get; }
    public long LogicalCoordinates => RootCount + _cars.LogicalCount + _tires.LogicalCount + _uses.LogicalCount * 2;
    public long LogicalRelationships => _cars.LogicalCount + _tires.LogicalCount + _uses.LogicalCount * 2;
    public long PhysicalNodeRecords => 0;
    public long PhysicalRelationshipRecords => 0;
    public long PhysicalRelationshipExceptions => 0;
    public long ScalarCells => (long)_tireColor.Count + _tirePressure.Count + _tireActive.Count + _metadataMarker.Count + _metadataTrack.Count;

    public void Seed()
    {
        for (int root = 0; root < RootCount; root++)
        {
            for (int car = 0; car < 2; car++)
            {
                for (int tire = 0; tire < 4; tire++)
                {
                    long tc = _tires.Coordinate(root, car, tire);
                    GraphColor color = tire == 3 && (root & 1) == 0 ? GraphColor.Green : GraphColor.Blue;
                    _tireColor.Write(tc, (byte)color);
                    _tirePressure.Write(tc, 30 + tire);
                    _tireActive.Write(tc, 1);
                    long uc = _uses.Coordinate(root, car, tire, 0);
                    _metadataMarker.Write(uc, (byte)color);
                    _metadataTrack.Write(uc, (byte)(((root & 1) == 0) ? 1 : 0));
                }
            }
        }
    }

    public long ProjectTireChecksum()
    {
        long checksum = 0;
        for (long c = 0; c < _tires.LogicalCount; c++)
        {
            if (!_tires.IsVisible(c)) continue;
            checksum += _tirePressure.Read(c) + _tireColor.Read(c) + _tireActive.Read(c);
        }
        return checksum;
    }

    public long QueryCorrelatedRootChecksum()
    {
        long checksum = 0;
        for (int root = 0; root < RootCount; root++)
        {
            // Last() is lawful: fixed tire ordinal 3. Preserve car/tire/usage binding.
            bool matched = false;
            for (int car = 0; car < 2 && !matched; car++)
            {
                long tire = _tires.Coordinate(root, car, 3);
                if (!_tires.IsVisible(tire) || _tireColor.Read(tire) != (byte)GraphColor.Green) continue;
                long use = _uses.Coordinate(root, car, 3, 0);
                if (_metadataTrack.Read(use) != 0 && _metadataMarker.Read(use) != (byte)GraphColor.Unknown) matched = true;
            }
            if (matched) checksum += root + 1L;
        }
        return checksum;
    }

    public void UpdateTire(long ordinal, int iteration)
    {
        long c = ordinal % _tires.LogicalCount;
        _tirePressure.Write(c, 28 + (iteration & 31));
        _tireColor.Write(c, (byte)(((iteration & 1) == 0) ? GraphColor.Green : GraphColor.Blue));
        _tireActive.Write(c, (byte)(((iteration & 7) != 0) ? 1 : 0));
    }

    public void DeleteRoot(int rootIndex) => _rootVisibility.ExcludeRange(rootIndex, 1);

    public int DeleteRootRange(int startRoot, int count) => _rootVisibility.ExcludeRange(startRoot, count);

    public int DeletionLawRecords => (_rootVisibility.DeletedPrefixExclusive > 0 ? 1 : 0) + _rootVisibility.ExceptionIntervalCount;

    public long Save(Stream stream)
    {
        stream.Position = 0;
        stream.SetLength(0);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write("M2LAWFUL1");
        _cars.WriteLaw(writer);
        _tires.WriteLaw(writer);
        _uses.WriteLaw(writer);
        _tireColor.Write(writer, static (w, v) => w.Write(v));
        _tirePressure.Write(writer, static (w, v) => w.Write(v));
        _tireActive.Write(writer, static (w, v) => w.Write(v));
        _metadataMarker.Write(writer, static (w, v) => w.Write(v));
        _metadataTrack.Write(writer, static (w, v) => w.Write(v));
        writer.Flush();
        return stream.Length;
    }
}
