using System;

namespace Memory2
{
    /// <summary>
    /// Reversible mill transition used to derive descendant coordinates from an
    /// owning coordinate and a stable schema/slot token. It is intentionally small:
    /// the goal is coordinate composition, not cryptographic hashing.
    /// </summary>
    internal static class DescendantCoordinateMill
    {
        private const ulong Additive = 0x9E3779B97F4A7C15UL;
        private const ulong SaltStep = 0xD1B54A32D192ED03UL;
        private const int Rotate = 23;

        public static long Root(Type rootType, long rootId)
        {
            ulong token = Token(rootType.AssemblyQualifiedName, GraphSlotKind.Root, -1, rootId);
            return Descend(0, token, 0);
        }

        public static ulong Token(string propertyName, GraphSlotKind kind, int ordinal, object dictionaryKey)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                Append(ref h, propertyName ?? string.Empty);
                h ^= (byte)kind; h *= 1099511628211UL;
                h ^= (uint)ordinal; h *= 1099511628211UL;
                if (dictionaryKey != null)
                {
                    Append(ref h, dictionaryKey.GetType().AssemblyQualifiedName ?? dictionaryKey.GetType().FullName);
                    Append(ref h, Convert.ToString(dictionaryKey, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                }
                return Mix(h);
            }
        }

        public static long Descend(long parentCoordinate, ulong token, int salt)
        {
            unchecked
            {
                ulong state = (ulong)parentCoordinate ^ token ^ ((ulong)salt * SaltStep);
                state = RotateLeft(state, Rotate);
                state += Additive;
                return (long)state;
            }
        }

        public static long Ascend(long descendantCoordinate, ulong token, int salt)
        {
            unchecked
            {
                ulong state = (ulong)descendantCoordinate - Additive;
                state = RotateRight(state, Rotate);
                state ^= token ^ ((ulong)salt * SaltStep);
                return (long)state;
            }
        }

        private static void Append(ref ulong hash, string value)
        {
            unchecked
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    hash ^= (byte)c; hash *= 1099511628211UL;
                    hash ^= (byte)(c >> 8); hash *= 1099511628211UL;
                }
            }
        }

        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z ^= z >> 30; z *= 0xBF58476D1CE4E5B9UL;
                z ^= z >> 27; z *= 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));
        private static ulong RotateRight(ulong value, int count) => (value >> count) | (value << (64 - count));
    }
}
