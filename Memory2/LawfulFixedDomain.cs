using System;
using System.IO;

namespace Memory2
{
    /// <summary>
    /// Describes the live root domain as a law plus sparse excluded intervals.
    /// Deleting a contiguous population slice records one boundary/interval, not one tombstone per root.
    /// </summary>
    public sealed class RootVisibilityLaw
    {
        private int _deletedPrefixExclusive;
        private readonly System.Collections.Generic.List<RootInterval> _excluded = new System.Collections.Generic.List<RootInterval>();
        private uint _epoch = 1;

        public int DeletedPrefixExclusive => _deletedPrefixExclusive;
        public int ExceptionIntervalCount => _excluded.Count;
        public uint Epoch => _epoch;

        public bool IsVisible(int rootIndex)
        {
            if (rootIndex < _deletedPrefixExclusive) return false;
            for (int i = 0; i < _excluded.Count; i++)
            {
                RootInterval interval = _excluded[i];
                if (rootIndex < interval.Start) return true;
                if (rootIndex < interval.EndExclusive) return false;
            }
            return true;
        }

        /// <summary>
        /// Replaces the live-domain prefix as one new epoch. This is the repeatable
        /// constant-time primitive used when a population/domain incarnation is
        /// invalidated or replaced; it never touches individual roots or descendants.
        /// </summary>
        public void ReplacePrefixForNewEpoch(int deletedPrefixExclusive)
        {
            if (deletedPrefixExclusive < 0) throw new ArgumentOutOfRangeException(nameof(deletedPrefixExclusive));
            _deletedPrefixExclusive = deletedPrefixExclusive;
            _excluded.Clear();
            unchecked { _epoch++; }
        }

        public int ExcludePrefix(int endExclusive)
        {
            if (endExclusive < 0) throw new ArgumentOutOfRangeException(nameof(endExclusive));
            if (endExclusive <= _deletedPrefixExclusive) return 0;
            int previous = _deletedPrefixExclusive;
            int newlyExcluded = endExclusive - previous;
            for (int i = 0; i < _excluded.Count; i++)
            {
                RootInterval interval = _excluded[i];
                if (interval.Start >= endExclusive) break;
                int overlapStart = Math.Max(previous, interval.Start);
                int overlapEnd = Math.Min(endExclusive, interval.EndExclusive);
                if (overlapEnd > overlapStart) newlyExcluded -= overlapEnd - overlapStart;
            }
            _deletedPrefixExclusive = endExclusive;
            while (_excluded.Count != 0 && _excluded[0].EndExclusive <= endExclusive) _excluded.RemoveAt(0);
            if (_excluded.Count != 0 && _excluded[0].Start < endExclusive)
            {
                RootInterval overlap = _excluded[0];
                _excluded[0] = new RootInterval(endExclusive, overlap.EndExclusive);
            }
            unchecked { _epoch++; }
            return newlyExcluded;
        }

        public int ExcludeRange(int start, int count)
        {
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (count <= 0) return 0;
            int end = checked(start + count);
            if (start <= _deletedPrefixExclusive) return ExcludePrefix(Math.Max(_deletedPrefixExclusive, end));
            int insert = 0;
            while (insert < _excluded.Count && _excluded[insert].EndExclusive < start) insert++;
            int mergedStart = start, mergedEnd = end;
            int newlyExcluded = count;
            while (insert < _excluded.Count && _excluded[insert].Start <= mergedEnd)
            {
                RootInterval overlap = _excluded[insert];
                int overlapStart = Math.Max(start, overlap.Start);
                int overlapEnd = Math.Min(end, overlap.EndExclusive);
                if (overlapEnd > overlapStart) newlyExcluded -= overlapEnd - overlapStart;
                mergedStart = Math.Min(mergedStart, overlap.Start);
                mergedEnd = Math.Max(mergedEnd, overlap.EndExclusive);
                _excluded.RemoveAt(insert);
            }
            _excluded.Insert(insert, new RootInterval(mergedStart, mergedEnd));
            unchecked { _epoch++; }
            return newlyExcluded;
        }

        public void Include(int rootIndex)
        {
            if (rootIndex < _deletedPrefixExclusive) throw new InvalidOperationException("A deleted prefix coordinate must be reused through a new generation/domain, not individually revived.");
            for (int i = 0; i < _excluded.Count; i++)
            {
                RootInterval interval = _excluded[i];
                if (rootIndex < interval.Start) break;
                if (rootIndex >= interval.EndExclusive) continue;
                _excluded.RemoveAt(i);
                if (interval.Start < rootIndex) _excluded.Insert(i++, new RootInterval(interval.Start, rootIndex));
                if (rootIndex + 1 < interval.EndExclusive) _excluded.Insert(i, new RootInterval(rootIndex + 1, interval.EndExclusive));
                unchecked { _epoch++; }
                return;
            }
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(_epoch);
            writer.Write(_deletedPrefixExclusive);
            writer.Write(_excluded.Count);
            for (int i = 0; i < _excluded.Count; i++)
            {
                writer.Write(_excluded[i].Start);
                writer.Write(_excluded[i].EndExclusive);
            }
        }

        internal void Read(BinaryReader reader, int rootCount)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            uint epoch = reader.ReadUInt32();
            int deletedPrefixExclusive = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (deletedPrefixExclusive < 0 || deletedPrefixExclusive > rootCount)
                throw new InvalidDataException("The persisted deleted-root prefix is outside the root domain.");
            if (count < 0 || count > rootCount)
                throw new InvalidDataException("The persisted root-exclusion count is invalid.");

            var intervals = new System.Collections.Generic.List<RootInterval>(count);
            int previousEnd = deletedPrefixExclusive;
            for (int i = 0; i < count; i++)
            {
                int start = reader.ReadInt32();
                int endExclusive = reader.ReadInt32();
                if (start < previousEnd || endExclusive <= start || endExclusive > rootCount)
                    throw new InvalidDataException("The persisted root-exclusion intervals are invalid.");
                intervals.Add(new RootInterval(start, endExclusive));
                previousEnd = endExclusive;
            }

            _epoch = epoch == 0 ? 1u : epoch;
            _deletedPrefixExclusive = deletedPrefixExclusive;
            _excluded.Clear();
            _excluded.AddRange(intervals);
        }

        private readonly struct RootInterval
        {
            public RootInterval(int start, int endExclusive) { Start = start; EndExclusive = endExclusive; }
            public int Start { get; }
            public int EndExclusive { get; }
        }
    }

    /// <summary>
    /// A fixed-cardinality logical descendant domain. Descendant identities are arithmetic
    /// coordinates; no node or relationship occurrence objects are allocated.
    /// </summary>
    public sealed class LawfulFixedDomain
    {
        private readonly int[] _dimensions;
        private readonly int[] _strides;
        private readonly RootVisibilityLaw _visibility;
        private readonly int _itemsPerRoot;

        public LawfulFixedDomain(int rootCount, params int[] dimensions)
            : this(new RootVisibilityLaw(), rootCount, dimensions)
        {
        }

        public LawfulFixedDomain(RootVisibilityLaw visibility, int rootCount, params int[] dimensions)
        {
            if (rootCount < 0) throw new ArgumentOutOfRangeException(nameof(rootCount));
            if (dimensions == null || dimensions.Length == 0) throw new ArgumentException("At least one descendant dimension is required.", nameof(dimensions));
            _dimensions = (int[])dimensions.Clone();
            _strides = new int[_dimensions.Length];
            long product = 1;
            for (int i = _dimensions.Length - 1; i >= 0; i--)
            {
                if (_dimensions[i] <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
                _strides[i] = checked((int)product);
                product = checked(product * _dimensions[i]);
            }
            _itemsPerRoot = checked((int)product);
            RootCount = rootCount;
            LogicalCount = checked((long)rootCount * _itemsPerRoot);
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        }

        public int RootCount { get; }
        public long LogicalCount { get; }
        public int ItemsPerRoot => _itemsPerRoot;
        public int PhysicalRelationshipRecords => 0;
        public int PhysicalNodeRecords => 0;
        public int DimensionCount => _dimensions.Length;

        public long Coordinate(int rootIndex, params int[] indices)
        {
            ValidateRoot(rootIndex);
            if (indices == null || indices.Length != _dimensions.Length) throw new ArgumentException("Coordinate rank does not match the domain.", nameof(indices));
            int local = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                int value = indices[i];
                if ((uint)value >= (uint)_dimensions[i]) throw new ArgumentOutOfRangeException(nameof(indices));
                local = checked(local + value * _strides[i]);
            }
            return checked((long)rootIndex * _itemsPerRoot + local);
        }

        public int RootIndex(long coordinate)
        {
            ValidateCoordinate(coordinate);
            return (int)(coordinate / _itemsPerRoot);
        }

        public int LocalIndex(long coordinate)
        {
            ValidateCoordinate(coordinate);
            return (int)(coordinate % _itemsPerRoot);
        }

        public int IndexAt(long coordinate, int dimension)
        {
            ValidateCoordinate(coordinate);
            if ((uint)dimension >= (uint)_dimensions.Length) throw new ArgumentOutOfRangeException(nameof(dimension));
            int local = LocalIndex(coordinate);
            return (local / _strides[dimension]) % _dimensions[dimension];
        }

        public bool IsVisible(long coordinate)
        {
            int root = RootIndex(coordinate);
            return _visibility.IsVisible(root);
        }

        public uint RootGeneration(int rootIndex)
        {
            ValidateRoot(rootIndex);
            return _visibility.Epoch;
        }

        public void DeleteRoot(int rootIndex)
        {
            ValidateRoot(rootIndex);
            _visibility.ExcludeRange(rootIndex, 1);
        }

        public int DeleteRange(int startRoot, int count)
        {
            if (startRoot < 0 || count < 0 || startRoot + count > RootCount) throw new ArgumentOutOfRangeException();
            return _visibility.ExcludeRange(startRoot, count);
        }

        public void RecreateRoot(int rootIndex)
        {
            ValidateRoot(rootIndex);
            _visibility.Include(rootIndex);
        }

        public void WriteLaw(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(RootCount);
            writer.Write(_dimensions.Length);
            for (int i = 0; i < _dimensions.Length; i++) writer.Write(_dimensions[i]);
            _visibility.Write(writer);
        }

        private void ValidateRoot(int rootIndex)
        {
            if ((uint)rootIndex >= (uint)RootCount) throw new ArgumentOutOfRangeException(nameof(rootIndex));
        }

        private void ValidateCoordinate(long coordinate)
        {
            if ((ulong)coordinate >= (ulong)LogicalCount) throw new ArgumentOutOfRangeException(nameof(coordinate));
        }
    }

    /// <summary>Dense typed scalar column indexed by lawful coordinates.</summary>
    public sealed class PackedCoordinateColumn<T> where T : struct
    {
        private readonly T[] _values;
        public PackedCoordinateColumn(long logicalCount)
        {
            if (logicalCount < 0 || logicalCount > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(logicalCount));
            _values = new T[(int)logicalCount];
        }
        public int Count => _values.Length;
        public ref T At(long coordinate)
        {
            if ((ulong)coordinate >= (ulong)_values.Length) throw new ArgumentOutOfRangeException(nameof(coordinate));
            return ref _values[(int)coordinate];
        }
        public T Read(long coordinate) => At(coordinate);
        public void Write(long coordinate, T value) => At(coordinate) = value;
        public void Write(BinaryWriter writer, Action<BinaryWriter, T> writeValue)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (writeValue == null) throw new ArgumentNullException(nameof(writeValue));
            writer.Write(_values.Length);
            for (int i = 0; i < _values.Length; i++) writeValue(writer, _values[i]);
        }
    }
}
