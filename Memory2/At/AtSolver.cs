// File: Memory2/At/AtSolver.cs
// FULL CLASS (drop-in replacement) — matches your CURRENT codec + AtExplain surface.
//
// Fixes compilation errors by:
// - Using IAtAxisCodec.TryEncodeLiteral(object, out int code, out int fingerprint32)
// - Using IAtAxisCodec.TryEncodeValue(object, out int code, out int fingerprint32)
// - NOT referencing AtExplain.HeaderBytes / AtExplain.ExpectedHeaderProbes (since your AtExplain lacks them)
//
// Keeps the *actual solver fix*:
// - GenerateIds() enumerates ONLY the congruence class using correct first+stride math.

using System;
using System.Collections.Generic;
using Memory2.Determine;

namespace Memory2.At
{
    internal static class AtSolver
    {
        internal sealed class Plan
        {
            private readonly long _pop;
            private readonly long _start;
            private readonly long _endExclusive;

            private readonly int _modulus;
            private readonly int? _residue;

            internal readonly ExpectedTerm[] Expected;
            internal readonly bool IsEmpty;
            public readonly AtExplain Explain;

            public Plan(
                long pop,
                long start,
                long endExclusive,
                int modulus,
                int? residue,
                ExpectedTerm[] expected,
                bool isEmpty,
                AtExplain explain)
            {
                _pop = pop;
                _start = start;
                _endExclusive = endExclusive;
                _modulus = modulus;
                _residue = residue;

                Expected = expected ?? new ExpectedTerm[0];
                IsEmpty = isEmpty;
                Explain = explain;
            }

            public IEnumerable<long> GenerateIds()
            {
                if (IsEmpty) yield break;

                long start = _start;
                long end = _endExclusive;

                if (start < 0) start = 0;
                if (end > _pop) end = _pop;
                if (end <= start) yield break;

                // No residue => scan narrowed range (bounded by start/end)
                if (_residue.HasValue == false)
                {
                    for (long id = start; id < end; id++)
                        yield return id;
                    yield break;
                }

                int M = _modulus;
                int r = _residue.Value;

                // first id >= start such that id % M == r
                long sMod = start % M;
                if (sMod < 0) sMod += M;

                long delta = r - sMod;
                delta %= M;
                if (delta < 0) delta += M;

                long first = start + delta;

                for (long id = first; id < end; id += M)
                    yield return id;
            }
        }

        internal struct ExpectedTerm
        {
            public int AxisIndex;
            public int ExpectedCode;
            public int ExpectedFingerprint;
            public string AxisName;

            public ExpectedTerm(int axisIndex, int code, int fp, string axisName)
            {
                AxisIndex = axisIndex;
                ExpectedCode = code;
                ExpectedFingerprint = fp;
                AxisName = axisName ?? "";
            }
        }

        public static Plan Compile(
            DetermineSchema schema,
            long population,
            IReadOnlyList<(string Axis, object Value)> terms,
            AtQueryOptions options)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (terms == null) throw new ArgumentNullException(nameof(terms));
            if (population < 0) population = 0;

            int modulus = schema.Modulus;
            long start = 0;
            long endExclusive = population;

            int supplied = terms.Count;

            var encodedNames = new List<string>(Math.Max(4, supplied));
            var droppedNames = new List<string>(0);
            var expected = new List<ExpectedTerm>(Math.Max(4, supplied));

            bool isEmpty = false;

            int? residue = null;

            for (int i = 0; i < terms.Count; i++)
            {
                string axisName = (terms[i].Axis ?? "").Trim();
                object lit = terms[i].Value;

                if (axisName.Length == 0)
                {
                    if (options.Strict) { isEmpty = true; break; }
                    continue;
                }

                int axisIndex;
                if (!schema.TryGetAxisIndex(axisName, out axisIndex))
                {
                    if (options.Strict) { isEmpty = true; break; }
                    droppedNames.Add(axisName);
                    continue;
                }

                var axis = schema.GetAxis(axisIndex);

                int code = 0;
                int fp = 0;

                // IMPORTANT: your codec requires fingerprint32 out-param
                if (axis.Codec == null || !axis.Codec.TryEncodeLiteral(lit, out code, out fp))
                {
                    if (options.Strict) { isEmpty = true; break; }
                    droppedNames.Add(axisName);
                    continue;
                }

                encodedNames.Add(axisName);
                expected.Add(new ExpectedTerm(axisIndex, code, fp, axisName));

                // Solvable residue for this axis (must match how projector determines that axis)
                int r = axis.SolveResidueFromCode(code);

                if (!residue.HasValue) residue = r;
                else if (residue.Value != r)
                {
                    // Contradiction: no IDs satisfy all solvable equalities
                    isEmpty = true;
                    break;
                }
            }

            var explain = new AtExplain
            {
                ModelType = schema.ModelType,
                Population = (population > int.MaxValue) ? int.MaxValue : (int)population,
                Modulus = modulus,
                Residue = residue.HasValue ? residue.Value : 0,
                SuppliedTermCount = supplied,
                EncodedTermCount = encodedNames.Count,
                DroppedTermCount = droppedNames.Count,
                DroppedTerms = droppedNames.ToArray(),
                EncodedTerms = encodedNames.ToArray()
            };

            return new Plan(
                pop: population,
                start: start,
                endExclusive: endExclusive,
                modulus: modulus,
                residue: residue,
                expected: expected.ToArray(),
                isEmpty: isEmpty,
                explain: explain);
        }

        public static bool MatchesAllTerms(
            DetermineSchema schema,
            object obj,
            IReadOnlyList<(string Axis, object Value)> originalTerms,
            ExpectedTerm[] expected)
        {
            if (schema == null) return false;
            if (obj == null) return false;
            if (expected == null) return true;

            for (int i = 0; i < expected.Length; i++)
            {
                var e = expected[i];
                var axis = schema.GetAxis(e.AxisIndex);

                object got = axis.Getter(obj);

                int gotCode = 0;
                int gotFp = 0;

                if (axis.Codec == null || !axis.Codec.TryEncodeValue(got, out gotCode, out gotFp))
                    return false;

                if (gotCode != e.ExpectedCode)
                    return false;

                // Fingerprint is optional but we do compare it since your codec provides it.
                if (gotFp != e.ExpectedFingerprint)
                    return false;
            }

            return true;
        }
    }
}
