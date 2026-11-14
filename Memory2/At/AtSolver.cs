using System;
using System.Collections.Generic;
using Memory2.Determine;

namespace Memory2.At
{
    internal static class AtSolver
    {
        internal sealed class Plan
        {
            private readonly long _population;
            private readonly long _start;
            private readonly long _endExclusive;
            private readonly int _modulus;
            private readonly int[] _residues;
            private readonly ulong[] _residueWords;

            internal readonly ExpectedTerm[] Expected;
            internal readonly bool IsEmpty;
            internal readonly bool GeneratedCandidatesAreExact;
            public readonly AtExplain Explain;

            internal Plan(long population, long start, long endExclusive, int modulus, int[] residues,
                ExpectedTerm[] expected, bool isEmpty, bool generatedCandidatesAreExact, AtExplain explain)
            {
                _population = population;
                _start = start;
                _endExclusive = endExclusive;
                _modulus = modulus;
                _residues = residues;
                Expected = expected ?? Array.Empty<ExpectedTerm>();
                IsEmpty = isEmpty;
                GeneratedCandidatesAreExact = generatedCandidatesAreExact;
                Explain = explain;
                if (residues != null && residues.Length > 1)
                {
                    _residueWords = new ulong[(modulus + 63) >> 6];
                    for (int i = 0; i < residues.Length; i++)
                        _residueWords[residues[i] >> 6] |= 1UL << (residues[i] & 63);
                }
            }

            internal IEnumerable<long> GenerateIds() => GenerateIds(null, null);

            internal IEnumerable<long> GenerateIds(long? startInclusive, long? endExclusive)
            {
                Clamp(startInclusive, endExclusive, out long start, out long end);
                if (IsEmpty || end <= start) yield break;
                if (_residues == null)
                {
                    for (long id = start; id < end; id++) yield return id;
                    yield break;
                }
                if (_residues.Length == 1)
                {
                    long first = FirstAtOrAfter(start, _residues[0]);
                    for (long id = first; id < end;)
                    {
                        yield return id;
                        if (id > long.MaxValue - _modulus) yield break;
                        id += _modulus;
                    }
                    yield break;
                }

                long block = start - (start % _modulus);
                while (block < end)
                {
                    for (int i = 0; i < _residues.Length; i++)
                    {
                        long id = block + _residues[i];
                        if (id < start) continue;
                        if (id >= end) break;
                        yield return id;
                    }
                    if (block > long.MaxValue - _modulus) yield break;
                    block += _modulus;
                }
            }

            internal bool Generates(long id)
            {
                if (IsEmpty || id < 0 || id >= _population || id < _start || id >= _endExclusive) return false;
                if (_residues == null) return true;
                int residue = (int)(id % _modulus);
                if (_residues.Length == 1) return residue == _residues[0];
                return (_residueWords[residue >> 6] & (1UL << (residue & 63))) != 0;
            }

            internal long CountGeneratedIds(long startInclusive, long endExclusive)
            {
                Clamp(startInclusive, endExclusive, out long start, out long end);
                if (IsEmpty || end <= start) return 0;
                if (_residues == null) return end - start;
                long count = 0;
                for (int i = 0; i < _residues.Length; i++) count = checked(count + CountResidue(start, end, _residues[i]));
                return count;
            }

            internal long ChecksumGeneratedIds(long startInclusive, long endExclusive)
            {
                Clamp(startInclusive, endExclusive, out long start, out long end);
                if (IsEmpty || end <= start) return 0;
                if (_residues == null) return SumProgression(start, end - start, 1);
                long checksum = 0;
                for (int i = 0; i < _residues.Length; i++)
                {
                    long first = FirstAtOrAfter(start, _residues[i]);
                    if (first >= end) continue;
                    long count = 1L + (end - 1L - first) / _modulus;
                    checksum = unchecked(checksum + SumProgression(first, count, _modulus));
                }
                return checksum;
            }

            internal long FirstGeneratedId(long startInclusive, long endExclusive)
            {
                Clamp(startInclusive, endExclusive, out long start, out long end);
                if (IsEmpty || end <= start) return -1;
                if (_residues == null) return start;
                long best = long.MaxValue;
                for (int i = 0; i < _residues.Length; i++)
                {
                    long candidate = FirstAtOrAfter(start, _residues[i]);
                    if (candidate < best) best = candidate;
                }
                return best < end ? best : -1;
            }

            private void Clamp(long? requestedStart, long? requestedEnd, out long start, out long end)
            {
                start = _start;
                end = _endExclusive;
                if (requestedStart.HasValue && requestedStart.Value > start) start = requestedStart.Value;
                if (requestedEnd.HasValue && requestedEnd.Value < end) end = requestedEnd.Value;
                if (start < 0) start = 0;
                if (end > _population) end = _population;
            }

            private long FirstAtOrAfter(long start, int residue)
            {
                long remainder = start % _modulus;
                long delta = residue - remainder;
                if (delta < 0) delta += _modulus;
                return start + delta;
            }

            private long CountResidue(long start, long end, int residue)
            {
                long first = FirstAtOrAfter(start, residue);
                return first >= end ? 0 : 1L + (end - 1L - first) / _modulus;
            }

            private static long SumProgression(long first, long count, long step)
            {
                if (count <= 0) return 0;
                long triangular = (count & 1L) == 0
                    ? unchecked((count >> 1) * (count - 1L))
                    : unchecked(count * ((count - 1L) >> 1));
                return unchecked(count * first + step * triangular);
            }
        }

        internal readonly struct ExpectedValue
        {
            internal ExpectedValue(int code, int fingerprint) { Code = code; Fingerprint = fingerprint; }
            internal int Code { get; }
            internal int Fingerprint { get; }
        }

        internal readonly struct ExpectedTerm
        {
            internal ExpectedTerm(int axisIndex, string axisName, ExpectedValue[] alternatives)
            {
                AxisIndex = axisIndex;
                AxisName = axisName ?? string.Empty;
                Alternatives = alternatives ?? Array.Empty<ExpectedValue>();
                PackedAlternatives = new long[Alternatives.Length];
                for (int i = 0; i < Alternatives.Length; i++)
                    PackedAlternatives[i] = Pack(Alternatives[i].Code, Alternatives[i].Fingerprint);
                Array.Sort(PackedAlternatives);
            }
            internal int AxisIndex { get; }
            internal string AxisName { get; }
            internal ExpectedValue[] Alternatives { get; }
            private long[] PackedAlternatives { get; }

            internal bool Matches(int code, int fingerprint)
            {
                long target = Pack(code, fingerprint);
                long[] values = PackedAlternatives;
                int low = 0, high = values.Length - 1;
                while (low <= high)
                {
                    int middle = low + ((high - low) >> 1);
                    long value = values[middle];
                    if (value == target) return true;
                    if (value < target) low = middle + 1;
                    else high = middle - 1;
                }
                return false;
            }

            private static long Pack(int code, int fingerprint)
                => unchecked(((long)(uint)code << 32) | (uint)fingerprint);
        }

        internal static Plan Compile(DetermineSchema schema, long population,
            IReadOnlyList<(string Axis, object Value)> terms, AtQueryOptions options, int epochCodeMask = 0)
        {
            if (terms == null) throw new ArgumentNullException(nameof(terms));
            var constraints = new AtConstraintTerm[terms.Count];
            for (int i = 0; i < terms.Count; i++) constraints[i] = AtConstraintTerm.Exact(terms[i].Axis, terms[i].Value);
            return Compile(schema, population, constraints, options, epochCodeMask, null);
        }

        internal static Plan Compile(DetermineSchema schema, long population,
            IReadOnlyList<AtConstraintTerm> terms, AtQueryOptions options, int epochCodeMask,
            Func<int, int, int, bool> generatedValueMatches)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (terms == null) throw new ArgumentNullException(nameof(terms));
            if (population < 0) population = 0;

            int modulus = schema.Modulus;
            var encodedNames = new List<string>(Math.Max(4, terms.Count));
            var droppedNames = new List<string>();
            var expected = new List<ExpectedTerm>(Math.Max(4, terms.Count));
            bool[] permittedResidues = null;
            bool isGeneratedEmpty = false;
            bool generatedCandidatesAreExact = true;
            int relationTermCount = 0;

            for (int i = 0; i < terms.Count; i++)
            {
                AtConstraintTerm term = terms[i];
                string axisName = (term.Axis ?? string.Empty).Trim();
                if (term.IsRelation) relationTermCount++;
                if (axisName.Length == 0 || !schema.TryGetAxisIndex(axisName, out int axisIndex))
                {
                    if (options.Strict)
                    {
                        isGeneratedEmpty = true;
                        expected.Add(new ExpectedTerm(-1, axisName, Array.Empty<ExpectedValue>()));
                    }
                    else droppedNames.Add(axisName);
                    continue;
                }

                DetermineSchema.Axis axis = schema.GetAxis(axisIndex);
                var alternatives = new List<ExpectedValue>(term.Alternatives.Length);
                bool[] termResidues = axis.CanSolveUniqueResidue ? new bool[modulus] : null;
                if (!axis.CanSolveUniqueResidue) generatedCandidatesAreExact = false;

                for (int alternative = 0; alternative < term.Alternatives.Length; alternative++)
                {
                    object literal = term.Alternatives[alternative];
                    if (axis.Codec == null || !axis.Codec.TryEncodeLiteral(literal, out int code, out int fingerprint))
                    {
                        if (options.Strict) isGeneratedEmpty = true;
                        continue;
                    }
                    bool duplicate = false;
                    for (int prior = 0; prior < alternatives.Count; prior++)
                        if (alternatives[prior].Code == code && alternatives[prior].Fingerprint == fingerprint)
                        { duplicate = true; break; }
                    if (duplicate) continue;
                    alternatives.Add(new ExpectedValue(code, fingerprint));

                    if (termResidues != null && (generatedValueMatches == null || generatedValueMatches(axisIndex, code, fingerprint)))
                    {
                        int generatedCode = code ^ (epochCodeMask & (modulus - 1));
                        termResidues[axis.SolveResidueFromCode(generatedCode)] = true;
                    }
                }

                if (alternatives.Count == 0)
                {
                    if (options.Strict || term.Alternatives.Length == 0)
                    {
                        expected.Add(new ExpectedTerm(axisIndex, axisName, Array.Empty<ExpectedValue>()));
                        isGeneratedEmpty = true;
                    }
                    else droppedNames.Add(axisName);
                    continue;
                }

                encodedNames.Add(term.IsRelation ? axisName + "[" + term.RelationName + "]" : axisName);
                expected.Add(new ExpectedTerm(axisIndex, axisName, alternatives.ToArray()));
                if (termResidues == null) continue;
                if (permittedResidues == null) permittedResidues = termResidues;
                else
                {
                    bool any = false;
                    for (int residue = 0; residue < modulus; residue++)
                    {
                        permittedResidues[residue] &= termResidues[residue];
                        any |= permittedResidues[residue];
                    }
                    if (!any) isGeneratedEmpty = true;
                }
            }

            int[] residues = null;
            if (permittedResidues != null)
            {
                var values = new List<int>();
                for (int residue = 0; residue < permittedResidues.Length; residue++)
                    if (permittedResidues[residue]) values.Add(residue);
                residues = values.ToArray();
                if (residues.Length == 0) isGeneratedEmpty = true;
            }
            if (expected.Count == 0) generatedCandidatesAreExact = false;

            var explain = new AtExplain
            {
                ModelType = schema.ModelType,
                Population = population,
                Modulus = modulus,
                Residue = residues != null && residues.Length == 1 ? residues[0] : -1,
                ResidueCount = residues?.Length ?? modulus,
                RelationTermCount = relationTermCount,
                SuppliedTermCount = terms.Count,
                EncodedTermCount = encodedNames.Count,
                DroppedTermCount = droppedNames.Count,
                DroppedTerms = droppedNames.ToArray(),
                EncodedTerms = encodedNames.ToArray()
            };
            var plan = new Plan(population, 0, population, modulus, residues, expected.ToArray(),
                isGeneratedEmpty, generatedCandidatesAreExact, explain);
            explain.ExpectedProbes = plan.CountGeneratedIds(0, population);
            return plan;
        }

        internal static bool MatchesAllTerms(DetermineSchema schema, object obj, ExpectedTerm[] expected)
        {
            if (schema == null || obj == null) return false;
            if (expected == null) return true;
            for (int i = 0; i < expected.Length; i++)
            {
                ExpectedTerm term = expected[i];
                if (term.AxisIndex < 0 || term.Alternatives.Length == 0) return false;
                DetermineSchema.Axis axis = schema.GetAxis(term.AxisIndex);
                object actual = axis.Getter(obj);
                if (axis.Codec == null || !axis.Codec.TryEncodeValue(actual, out int code, out int fingerprint)) return false;
                if (!term.Matches(code, fingerprint)) return false;
            }
            return true;
        }

        internal static bool MatchesAllTerms(DetermineSchema schema, object obj,
            IReadOnlyList<(string Axis, object Value)> originalTerms, ExpectedTerm[] expected)
            => MatchesAllTerms(schema, obj, expected);
    }
}
