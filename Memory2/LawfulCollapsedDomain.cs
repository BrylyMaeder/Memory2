using System;
using System.Collections.Generic;

namespace Memory2;

/// <summary>
/// Represents a very large lawful value domain as a deterministic coordinate law plus sparse
/// physical exceptions. Values are returned in their native type; there is no per-read decoding
/// or object materialization. Storage grows with exceptions rather than logical population.
/// </summary>
public sealed class LawfulCollapsedDomain<T> where T : unmanaged
{
    private readonly long _count;
    private readonly Func<long, T> _law;
    private long[] _exceptionCoordinates;
    private T[] _exceptionValues;
    private int _exceptionCount;

    public LawfulCollapsedDomain(long count, Func<long, T> law, int exceptionCapacity = 0)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (law == null) throw new ArgumentNullException(nameof(law));
        if (exceptionCapacity < 0) throw new ArgumentOutOfRangeException(nameof(exceptionCapacity));
        _count = count;
        _law = law;
        _exceptionCoordinates = exceptionCapacity == 0 ? Array.Empty<long>() : new long[exceptionCapacity];
        _exceptionValues = exceptionCapacity == 0 ? Array.Empty<T>() : new T[exceptionCapacity];
    }

    public long Count => _count;
    public int ExceptionCount => _exceptionCount;
    public long PrimitivePayloadBytes =>
        (long)_exceptionCoordinates.Length * sizeof(long) +
        (long)_exceptionValues.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>();

    public T Read(long coordinate)
    {
        ValidateCoordinate(coordinate);
        int index = Find(coordinate);
        return index >= 0 ? _exceptionValues[index] : _law(coordinate);
    }

    public bool TryRead(long coordinate, out T value)
    {
        if ((ulong)coordinate >= (ulong)_count)
        {
            value = default;
            return false;
        }
        int index = Find(coordinate);
        value = index >= 0 ? _exceptionValues[index] : _law(coordinate);
        return true;
    }

    /// <summary>Adds or replaces an exception. Coordinates are maintained in sorted order.</summary>
    public void Set(long coordinate, T value)
    {
        ValidateCoordinate(coordinate);
        T generated = _law(coordinate);
        int index = Find(coordinate);
        if (EqualityComparer<T>.Default.Equals(value, generated))
        {
            if (index >= 0) RemoveAt(index);
            return;
        }

        if (index >= 0)
        {
            _exceptionValues[index] = value;
            return;
        }

        int insert = ~index;
        EnsureCapacity(_exceptionCount + 1);
        if (insert < _exceptionCount)
        {
            Array.Copy(_exceptionCoordinates, insert, _exceptionCoordinates, insert + 1, _exceptionCount - insert);
            Array.Copy(_exceptionValues, insert, _exceptionValues, insert + 1, _exceptionCount - insert);
        }
        _exceptionCoordinates[insert] = coordinate;
        _exceptionValues[insert] = value;
        _exceptionCount++;
    }

    public void SetSorted(long coordinate, T value)
    {
        ValidateCoordinate(coordinate);
        if (_exceptionCount != 0 && coordinate <= _exceptionCoordinates[_exceptionCount - 1])
        {
            Set(coordinate, value);
            return;
        }
        if (EqualityComparer<T>.Default.Equals(value, _law(coordinate))) return;
        EnsureCapacity(_exceptionCount + 1);
        _exceptionCoordinates[_exceptionCount] = coordinate;
        _exceptionValues[_exceptionCount] = value;
        _exceptionCount++;
    }

    public long EstimatePersistedBytes(int lawDescriptorBytes = 64)
    {
        int valueSize = System.Runtime.InteropServices.Marshal.SizeOf<T>();
        return 32L + lawDescriptorBytes + (long)_exceptionCount * (sizeof(long) + valueSize);
    }

    private int Find(long coordinate) => Array.BinarySearch(_exceptionCoordinates, 0, _exceptionCount, coordinate);

    private void EnsureCapacity(int required)
    {
        if (_exceptionCoordinates.Length >= required) return;
        int next = _exceptionCoordinates.Length == 0 ? 16 : _exceptionCoordinates.Length * 2;
        if (next < required) next = required;
        Array.Resize(ref _exceptionCoordinates, next);
        Array.Resize(ref _exceptionValues, next);
    }

    private void RemoveAt(int index)
    {
        int tail = _exceptionCount - index - 1;
        if (tail > 0)
        {
            Array.Copy(_exceptionCoordinates, index + 1, _exceptionCoordinates, index, tail);
            Array.Copy(_exceptionValues, index + 1, _exceptionValues, index, tail);
        }
        _exceptionCount--;
        _exceptionCoordinates[_exceptionCount] = 0;
        _exceptionValues[_exceptionCount] = default;
    }

    private void ValidateCoordinate(long coordinate)
    {
        if ((ulong)coordinate >= (ulong)_count) throw new ArgumentOutOfRangeException(nameof(coordinate));
    }
}

/// <summary>Summary used to distinguish law-derived entropy from independently supplied entropy.</summary>
public readonly record struct LawfulCollapseEstimate(
    long LogicalRecords,
    long LogicalNativeBytes,
    long PhysicalBytes,
    int PhysicalExceptions,
    double BytesPerLogicalRecord,
    double CollapseRatio);

public static class LawfulCollapse
{
    public static LawfulCollapseEstimate Estimate<T>(LawfulCollapsedDomain<T> domain, int nativeBytesPerLogicalRecord, int lawDescriptorBytes = 64)
        where T : unmanaged
    {
        if (domain == null) throw new ArgumentNullException(nameof(domain));
        if (nativeBytesPerLogicalRecord <= 0) throw new ArgumentOutOfRangeException(nameof(nativeBytesPerLogicalRecord));
        long logicalNative = checked(domain.Count * nativeBytesPerLogicalRecord);
        long physical = domain.EstimatePersistedBytes(lawDescriptorBytes);
        return new LawfulCollapseEstimate(
            domain.Count,
            logicalNative,
            physical,
            domain.ExceptionCount,
            physical / (double)Math.Max(1, domain.Count),
            logicalNative / (double)Math.Max(1, physical));
    }
}
