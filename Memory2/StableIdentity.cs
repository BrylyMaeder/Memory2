using System;

namespace Memory2
{
    /// <summary>
    /// Stable identities used by deterministic projection and sparse field encoding.
    ///
    /// IMPORTANT: these algorithms are part of Memory2's data contract. Changing them
    /// changes the generated universe. Do not replace them with string.GetHashCode(),
    /// which is runtime-dependent.
    /// </summary>
    internal static class StableIdentity
    {
        private const ulong FnvOffsetBasis64 = 14695981039346656037UL;
        private const ulong FnvPrime64 = 1099511628211UL;

        public static void ValidatePowerOfTwoModulus(int modulus, string parameterName)
        {
            if (modulus < 2)
                throw new ArgumentOutOfRangeException(parameterName, "Modulus must be at least 2.");

            if ((modulus & (modulus - 1)) != 0)
                throw new ArgumentException("Modulus must be a power of two.", parameterName);
        }

        public static void CreateAffineCoefficients(string propertyName, int modulus, out int a, out int b)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentException("A property name is required.", nameof(propertyName));

            ValidatePowerOfTwoModulus(modulus, nameof(modulus));

            ulong identity = Hash64(propertyName);
            int mask = modulus - 1;

            // Independent mixes prevent correlation between the multiplier and offset.
            a = (unchecked((int)Mix64(identity ^ 0xA0761D6478BD642FUL)) & mask) | 1;
            b = unchecked((int)Mix64(identity ^ 0xE7037ED1A0B428DBUL)) & mask;
        }

        public static uint FieldId32(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentException("A property name is required.", nameof(propertyName));

            ulong h = Mix64(Hash64(propertyName) ^ 0x8EBC6AF09C88C6E3UL);
            uint id = unchecked((uint)(h ^ (h >> 32)));
            return id == 0 ? 1u : id;
        }

        private static ulong Hash64(string value)
        {
            unchecked
            {
                ulong hash = FnvOffsetBasis64;

                // Hash the exact UTF-16 code units, including both bytes. This is
                // allocation-free and independent of framework hash randomization.
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    hash ^= (byte)c;
                    hash *= FnvPrime64;
                    hash ^= (byte)(c >> 8);
                    hash *= FnvPrime64;
                }

                return Mix64(hash ^ (ulong)value.Length);
            }
        }

        private static ulong Mix64(ulong z)
        {
            unchecked
            {
                z ^= z >> 30;
                z *= 0xBF58476D1CE4E5B9UL;
                z ^= z >> 27;
                z *= 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
