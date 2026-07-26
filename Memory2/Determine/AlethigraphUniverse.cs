// File: Memory2/Determine/Universe.cs  (NEW)
using System;
using System.Collections.Generic;

namespace Memory2.Determine
{
    /// <summary>
    /// Shared "epoch scar" universe.
    /// ApplyEpoch is O(ringSize); reveals are scar-influenced.
    /// </summary>
    internal sealed class AlethigraphUniverse
    {
        private readonly Node[] _nodes;
        private readonly int[][] _nbrs;

        public int RingSize { get; }
        public int NodeCount => RingSize;

        public long TotalTouchCount
        {
            get
            {
                long sum = 0;
                for (int i = 0; i < _nodes.Length; i++) sum += _nodes[i].TouchCount;
                return sum;
            }
        }

        public AlethigraphUniverse(int ringSize, int hubCount)
        {
            if (ringSize < 256) throw new ArgumentOutOfRangeException(nameof(ringSize));
            if (hubCount < 0) throw new ArgumentOutOfRangeException(nameof(hubCount));

            RingSize = ringSize;

            _nodes = new Node[RingSize];
            for (int i = 0; i < RingSize; i++)
                _nodes[i] = new Node(i);

            int[] hubs = BuildHubs(ringSize, hubCount);

            _nbrs = new int[RingSize][];
            for (int a = 0; a < RingSize; a++)
            {
                int Wrap(int x) { int r = x % RingSize; return r < 0 ? r + RingSize : r; }

                int left1 = Wrap(a - 1);
                int right1 = Wrap(a + 1);
                int left7 = Wrap(a - 7);
                int right7 = Wrap(a + 7);

                int long1 = Wrap(a + 137);
                int long2 = Wrap(a * 3 + 17);
                int long3 = Wrap(a * 5 + 101);

                var tmp = new List<int>(16) { left1, right1, left7, right7, long1, long2, long3 };
                for (int i = 0; i < hubs.Length; i++) tmp.Add(hubs[i]);

                tmp.Sort();
                int w = 0;
                for (int i = 0; i < tmp.Count; i++)
                    if (i == 0 || tmp[i] != tmp[i - 1]) tmp[w++] = tmp[i];
                if (w < tmp.Count) tmp.RemoveRange(w, tmp.Count - w);

                _nbrs[a] = tmp.ToArray();
            }
        }

        private static int[] BuildHubs(int ringSize, int hubCount)
        {
            if (hubCount == 0) return Array.Empty<int>();

            var set = new HashSet<int>();
            int step = ringSize / Math.Max(1, hubCount);
            for (int i = 0; i < hubCount; i++)
                set.Add((i * step) % ringSize);

            var hubs = new int[set.Count];
            set.CopyTo(hubs);
            Array.Sort(hubs);
            return hubs;
        }

        public int[] Neighbors(int nodeId) => _nbrs[Wrap(nodeId)];

        public int Wrap(int x)
        {
            int r = x % RingSize;
            return r < 0 ? r + RingSize : r;
        }

        /// <summary>
        /// O(ringSize) epoch update; this is your "instant storage".
        /// </summary>
        public void ApplyEpoch(ulong epoch)
        {
            for (int i = 0; i < RingSize; i++)
            {
                ulong h = SplitMix64(epoch ^ (ulong)i * 0x9E3779B97F4A7C15UL);

                long dc = (long)(1 + (h & 255UL));
                _nodes[i].TouchCount += dc;

                ulong v = SplitMix64(h ^ 0xD1B54A32D192ED03UL);
                _nodes[i].XorFold ^= v;
                _nodes[i].SumFold += v;
            }
        }

        public uint Reveal32(int nodeId, int step)
        {
            unchecked
            {
                uint x = (uint)Wrap(nodeId);
                x ^= (uint)step * 0x9E3779B9u;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;

                x *= 0x85EBCA6Bu;
                x ^= x >> 13;
                x *= 0xC2B2AE35u;
                x ^= x >> 16;
                return x;
            }
        }

        public uint EffectiveReveal32(int nodeId, int step)
        {
            int w = Wrap(nodeId);
            uint baseR = Reveal32(w, step);
            uint scar = _nodes[w].Scar32();

            unchecked
            {
                uint x = baseR ^ scar ^ ((uint)step * 0x85EBCA6Bu);
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                return x;
            }
        }

        private static ulong SplitMix64(ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private sealed class Node
        {
            public readonly int Id;
            public long TouchCount;
            public ulong XorFold;
            public ulong SumFold;

            public Node(int id) { Id = id; }

            public uint Scar32()
            {
                unchecked
                {
                    ulong x = XorFold ^ (SumFold + (ulong)TouchCount * 0x9E3779B97F4A7C15UL);
                    x ^= x >> 33;
                    x *= 0xff51afd7ed558ccdUL;
                    x ^= x >> 33;
                    x *= 0xc4ceb9fe1a85ec53UL;
                    x ^= x >> 33;
                    return (uint)x;
                }
            }
        }
    }
}
