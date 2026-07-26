using System;
using System.Collections.Generic;

namespace Memory2.Storage
{
    internal struct RowRef
    {
        public int Chunk;
        public int Offset;
        public int Length;
    }

    internal sealed class DeltaArena
    {
        private readonly int _chunkBytes;
        private readonly List<byte[]> _chunks = new List<byte[]>();
        private int _writeChunk;
        private int _writeOffset;

        public DeltaArena(int chunkBytes)
        {
            if (chunkBytes < 64 * 1024) chunkBytes = 64 * 1024;
            _chunkBytes = chunkBytes;
            _chunks.Add(new byte[_chunkBytes]);
            _writeChunk = 0;
            _writeOffset = 0;
        }

        public RowRef Append(byte[] src, int offset, int count)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (offset < 0 || count < 0 || offset + count > src.Length) throw new ArgumentOutOfRangeException();

            // If too large for a chunk, store in a dedicated chunk.
            if (count > _chunkBytes)
            {
                var dedicated = new byte[count];
                Buffer.BlockCopy(src, offset, dedicated, 0, count);
                int idx = _chunks.Count;
                _chunks.Add(dedicated);
                return new RowRef { Chunk = idx, Offset = 0, Length = count };
            }

            if (_writeOffset + count > _chunks[_writeChunk].Length)
            {
                _chunks.Add(new byte[_chunkBytes]);
                _writeChunk = _chunks.Count - 1;
                _writeOffset = 0;
            }

            int dstOff = _writeOffset;
            Buffer.BlockCopy(src, offset, _chunks[_writeChunk], dstOff, count);
            _writeOffset += count;

            return new RowRef { Chunk = _writeChunk, Offset = dstOff, Length = count };
        }

        public byte[] Read(RowRef rr, out int length)
        {
            length = rr.Length;
            byte[] chunk = _chunks[rr.Chunk];
            // We return a slice copy only if needed by decoder; codec can read from chunk+offset directly,
            // but to keep decoder simple, we copy to a temp when offset!=0 or chunk length > len.
            // Optimization: return chunk directly when stored dedicated.
            if (rr.Offset == 0 && chunk.Length == rr.Length)
                return chunk;

            var tmp = new byte[rr.Length];
            Buffer.BlockCopy(chunk, rr.Offset, tmp, 0, rr.Length);
            return tmp;
        }
    }
}
