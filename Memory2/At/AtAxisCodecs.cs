// File: Memory2/At/AtAxisCodecs.cs
using System;
using System.Globalization;

namespace Memory2.At
{
    public interface IAtAxisCodec
    {
        bool TryEncodeLiteral(object literal, out int code, out int fingerprint32);
        bool TryEncodeValue(object value, out int code, out int fingerprint32);
        bool IsZeroAlloc { get; }
    }

    internal static class AtAxisCodecs
    {
        public static IAtAxisCodec ForType(Type t, string axisName, int modulus)
        {
            if (t == typeof(int) || t == typeof(short) || t == typeof(byte) ||
                t == typeof(uint) || t == typeof(ushort))
                return new Int32Codec(modulus);

            if (t == typeof(long) || t == typeof(ulong))
                return new Int64Low32Codec(modulus);

            if (t == typeof(bool))
                return new BoolCodec();

            if (t != null && t.IsEnum)
                return new EnumLow32Codec(modulus);

            if (t == typeof(string))
                return new StringNumericSuffixOrHashCodec(GetStringPrefixes(axisName), modulus);

            return null;
        }

        // ---- NEW: alias prefixes for "SSQL-like" literals (u0001, car0037, etc.)
        // Add more aliases as needed; this does not change any public API.
        private static string[] GetStringPrefixes(string axisName)
        {
            axisName = axisName ?? "";
            string a = axisName.Trim();

            // IMPORTANT: these are *prefixes accepted in query literals*.
            // DetermineBase currently emits $"{axis.ToLowerInvariant()}{code:D4}".
            // This adds extra accepted inputs like "u0001" and "car0037".
            if (a.Equals("Username", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("User", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("UserName", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "u", "user", "username" };
            }

            if (a.Equals("CarType", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("Car", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("Vehicle", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "car", "cartype", "vehicle" };
            }

            // Default: accept axisName itself (case-insensitive match in parser)
            return new[] { a };
        }

        private static int Hash32(string s)
        {
            if (s == null) return 0;
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return (int)h;
            }
        }

        private static int ModPos(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }

        private static bool TryParseInt32(string s, out int value)
        {
            value = 0;
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return false;

            int sign = 1;
            int i = 0;
            if (s[0] == '+') i++;
            else if (s[0] == '-') { sign = -1; i++; }
            if (i >= s.Length) return false;

            int acc = 0;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                uint d = (uint)(ch - '0');
                if (d > 9u) return false;
                if (acc > 214748364) return false;
                acc = acc * 10 + (int)d;
            }
            value = (sign == 1) ? acc : -acc;
            return true;
        }

        private static bool TryParseInt64(string s, out long value)
        {
            value = 0;
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return false;

            long sign = 1;
            int i = 0;
            if (s[0] == '+') i++;
            else if (s[0] == '-') { sign = -1; i++; }
            if (i >= s.Length) return false;

            long acc = 0;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                uint d = (uint)(ch - '0');
                if (d > 9u) return false;
                if (acc > 922337203685477580L) return false;
                acc = acc * 10 + (long)d;
            }
            value = (sign == 1) ? acc : -acc;
            return true;
        }

        private sealed class Int32Codec : IAtAxisCodec
        {
            private readonly int _modulus;
            public Int32Codec(int modulus) { _modulus = modulus <= 0 ? 4096 : modulus; }
            public bool IsZeroAlloc => true;

            public bool TryEncodeLiteral(object literal, out int code, out int fp)
            {
                code = 0; fp = 0;
                if (literal == null) return true;

                int v;
                if (literal is int) v = (int)literal;
                else if (literal is long) v = unchecked((int)(long)literal);
                else if (literal is short) v = (short)literal;
                else if (literal is byte) v = (byte)literal;
                else if (literal is uint) v = unchecked((int)(uint)literal);
                else if (literal is ushort) v = (ushort)literal;
                else if (literal is string)
                {
                    if (!TryParseInt32((string)literal, out v)) return false;
                }
                else return false;

                code = ModPos(v, _modulus);
                fp = v;
                return true;
            }

            public bool TryEncodeValue(object value, out int code, out int fp)
                => TryEncodeLiteral(value, out code, out fp);
        }

        private sealed class Int64Low32Codec : IAtAxisCodec
        {
            private readonly int _modulus;
            public Int64Low32Codec(int modulus) { _modulus = modulus <= 0 ? 4096 : modulus; }
            public bool IsZeroAlloc => true;

            public bool TryEncodeLiteral(object literal, out int code, out int fp)
            {
                code = 0; fp = 0;
                if (literal == null) return true;

                long v;
                if (literal is long) v = (long)literal;
                else if (literal is ulong) v = unchecked((long)(ulong)literal);
                else if (literal is int) v = (int)literal;
                else if (literal is string)
                {
                    if (!TryParseInt64((string)literal, out v)) return false;
                }
                else return false;

                int lo = unchecked((int)v);
                code = ModPos(lo, _modulus);
                fp = lo ^ (int)(v >> 32);
                return true;
            }

            public bool TryEncodeValue(object value, out int code, out int fp)
                => TryEncodeLiteral(value, out code, out fp);
        }

        private sealed class BoolCodec : IAtAxisCodec
        {
            public bool IsZeroAlloc => true;

            public bool TryEncodeLiteral(object literal, out int code, out int fp)
            {
                code = 0; fp = 0;
                if (literal == null) return true;

                bool b;
                if (literal is bool) b = (bool)literal;
                else if (literal is string)
                {
                    var s = ((string)literal).Trim();
                    if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) b = true;
                    else if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) b = false;
                    else return false;
                }
                else return false;

                code = b ? 1 : 0;
                fp = code;
                return true;
            }

            public bool TryEncodeValue(object value, out int code, out int fp)
                => TryEncodeLiteral(value, out code, out fp);
        }

        private sealed class EnumLow32Codec : IAtAxisCodec
        {
            private readonly int _modulus;
            public EnumLow32Codec(int modulus) { _modulus = modulus <= 0 ? 4096 : modulus; }
            public bool IsZeroAlloc => true;

            public bool TryEncodeLiteral(object literal, out int code, out int fp)
            {
                code = 0; fp = 0;
                if (literal == null) return true;

                int v;
                var t = literal.GetType();
                if (t.IsEnum) v = Convert.ToInt32(literal, CultureInfo.InvariantCulture);
                else if (literal is int) v = (int)literal;
                else if (literal is long) v = unchecked((int)(long)literal);
                else if (literal is string)
                {
                    if (!TryParseInt32((string)literal, out v)) return false;
                }
                else return false;

                code = ModPos(v, _modulus);
                fp = v;
                return true;
            }

            public bool TryEncodeValue(object value, out int code, out int fp)
                => TryEncodeLiteral(value, out code, out fp);
        }

        // ---- UPDATED: now accepts multiple prefixes (u, username, car, cartype, etc.)
        private sealed class StringNumericSuffixOrHashCodec : IAtAxisCodec
        {
            private readonly string[] _prefixes;
            private readonly int _modulus;

            public StringNumericSuffixOrHashCodec(string[] prefixes, int modulus)
            {
                _prefixes = (prefixes != null && prefixes.Length > 0) ? prefixes : new[] { "" };
                _modulus = modulus <= 0 ? 4096 : modulus;
            }

            public bool IsZeroAlloc => true;

            public bool TryEncodeLiteral(object literal, out int code, out int fp)
                => TryEncodeStringLike(literal, out code, out fp);

            public bool TryEncodeValue(object value, out int code, out int fp)
                => TryEncodeStringLike(value, out code, out fp);

            private bool TryEncodeStringLike(object value, out int code, out int fp)
            {
                code = 0; fp = 0;
                if (value == null) return true;

                string s = value as string ?? value.ToString();
                if (s == null) return true;

                if (TryParseSuffixAnyPrefix(s, _prefixes, out int suffix))
                {
                    code = ModPos(suffix, _modulus);

                    // Prefix aliases are alternate spellings of the same generated value.
                    // Keep the full numeric suffix as the fingerprint so values that only
                    // collide modulo M (for example 37 and 4133) still remain distinct.
                    fp = suffix;
                    return true;
                }

                int h = Hash32(s);
                code = ModPos(h, _modulus);
                fp = h;
                return true;
            }

            private static bool TryParseSuffixAnyPrefix(string s, string[] prefixes, out int suffix)
            {
                suffix = 0;
                if (s == null) return false;

                int lo = 0;
                int hi = s.Length;
                while (lo < hi && char.IsWhiteSpace(s[lo])) lo++;
                while (hi > lo && char.IsWhiteSpace(s[hi - 1])) hi--;
                if (hi <= lo) return false;

                int dataStart = -1;
                int bestPrefixLength = -1;

                // Prefer the longest matching alias so overlapping prefixes such as
                // "u", "user", and "username" all canonicalize correctly.
                for (int p = 0; p < prefixes.Length; p++)
                {
                    string prefix = prefixes[p] ?? string.Empty;
                    if (prefix.Length <= bestPrefixLength) continue;
                    if (hi - lo <= prefix.Length) continue;

                    bool match = true;
                    for (int i = 0; i < prefix.Length; i++)
                    {
                        if (char.ToUpperInvariant(s[lo + i]) != char.ToUpperInvariant(prefix[i]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        bestPrefixLength = prefix.Length;
                        dataStart = lo + prefix.Length;
                    }
                }

                if (dataStart < 0 || dataStart >= hi) return false;

                int value = 0;
                for (int i = dataStart; i < hi; i++)
                {
                    uint digit = (uint)(s[i] - '0');
                    if (digit > 9u) return false;
                    if (value > (int.MaxValue - (int)digit) / 10) return false;
                    value = value * 10 + (int)digit;
                }

                suffix = value;
                return true;
            }
        }
    }
}
