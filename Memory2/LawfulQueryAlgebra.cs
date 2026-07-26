using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Memory2
{
    public sealed class LawfulQueryDiagnostics
    {
        public LawfulQueryDiagnostics() { }
        public long CoordinateVisits { get; internal set; }
        public long PredicateEvaluations { get; internal set; }
        public long ScalarReads { get; internal set; }
        public long EarlyExits { get; internal set; }
        public long RankPasses { get; internal set; }
        public long MembershipProbes { get; internal set; }
        public long ResultValues { get; internal set; }
        public long ModelMaterializations { get; internal set; }
        public long FallbackExecutions { get; internal set; }
        public long OperatorExecutions { get; internal set; }
        public long NativeTableBuilds { get; internal set; }
        public long TraditionalSortExecutions { get; internal set; }
        public long PlannerCompilations { get; internal set; }
        public long FusedStages { get; internal set; }
        public long EliminatedStages { get; internal set; }
        public long IntermediateWrites { get; internal set; }
    }

    public sealed class LawfulNodeQuery<TRoot, TNode>
        where TRoot : class, new()
        where TNode : class, new()
    {
        private readonly LawfulMemoryStore<TRoot> _store;
        private readonly LawfulNodeShape _shape;
        private readonly int[] _coordinates;
        private readonly LawfulQueryDiagnostics _diagnostics;

        private LawfulNodeQuery(LawfulMemoryStore<TRoot> store, LawfulNodeShape shape, int[] coordinates, LawfulQueryDiagnostics diagnostics)
        { _store = store; _shape = shape; _coordinates = coordinates; _diagnostics = diagnostics; }

        internal static LawfulNodeQuery<TRoot, TNode> Create(LawfulMemoryStore<TRoot> store)
        {
            LawfulNodeShape shape = store.GetQueryShape<TNode>();
            int roots = store.QueryRootCount;
            int live = 0;
            for (int root = 0; root < roots; root++) if (store.IsQueryRootVisible(root)) live++;
            int[] coordinates = new int[checked(live * shape.ItemsPerRoot)];
            int output = 0;
            for (int root = 0; root < roots; root++)
            {
                if (!store.IsQueryRootVisible(root)) continue;
                int first = root * shape.ItemsPerRoot;
                for (int local = 0; local < shape.ItemsPerRoot; local++) coordinates[output++] = first + local;
            }
            return new LawfulNodeQuery<TRoot, TNode>(store, shape, coordinates, new LawfulQueryDiagnostics());
        }

        public LawfulQueryDiagnostics Diagnostics => _diagnostics;
        public int Count => _coordinates.Length;

        /// <summary>Fully materializes every query result, including all configured owned descendants.</summary>
        public TNode[] Hydrate()
        {
            var models = new TNode[_coordinates.Length];
            for (int i = 0; i < models.Length; i++)
            {
                models[i] = _store.HydrateCoordinate<TNode>(_shape, _coordinates[i]);
                _diagnostics.ModelMaterializations++;
            }
            return models;
        }

        /// <summary>Fully materializes the result at the specified query position.</summary>
        public TNode HydrateAt(int index)
        {
            if ((uint)index >= (uint)_coordinates.Length) throw new ArgumentOutOfRangeException(nameof(index));
            TNode model = _store.HydrateCoordinate<TNode>(_shape, _coordinates[index]);
            _diagnostics.ModelMaterializations++;
            return model;
        }

        /// <summary>Fully materializes the first query result.</summary>
        public TNode FirstHydrated()
        {
            if (_coordinates.Length == 0) throw new InvalidOperationException("Sequence contains no elements.");
            return HydrateAt(0);
        }

        /// <summary>Fully materializes the first query result, or returns null when the sequence is empty.</summary>
        public TNode FirstOrDefaultHydrated()
            => _coordinates.Length == 0 ? null : HydrateAt(0);

        /// <summary>Fully materializes the last query result.</summary>
        public TNode LastHydrated()
        {
            if (_coordinates.Length == 0) throw new InvalidOperationException("Sequence contains no elements.");
            return HydrateAt(_coordinates.Length - 1);
        }

        /// <summary>Fully materializes the last query result, or returns null when the sequence is empty.</summary>
        public TNode LastOrDefaultHydrated()
            => _coordinates.Length == 0 ? null : HydrateAt(_coordinates.Length - 1);

        /// <summary>Fully materializes the only query result.</summary>
        public TNode SingleHydrated()
        {
            if (_coordinates.Length == 0) throw new InvalidOperationException("Sequence contains no elements.");
            if (_coordinates.Length != 1) throw new InvalidOperationException("Sequence contains more than one element.");
            return HydrateAt(0);
        }

        /// <summary>Fully materializes the only query result, or returns null when the sequence is empty.</summary>
        public TNode SingleOrDefaultHydrated()
        {
            if (_coordinates.Length > 1) throw new InvalidOperationException("Sequence contains more than one element.");
            return _coordinates.Length == 0 ? null : HydrateAt(0);
        }

        public LawfulPreparedNodeCountPlan<TRoot, TNode, TResult> PrepareWhereSelectCount<TResult>(Expression<Func<TNode, bool>> predicate, Expression<Func<TNode, TResult>> selector)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            return new LawfulPreparedNodeCountPlan<TRoot, TNode, TResult>(_coordinates, _store.CompileQuerySelector(_shape, predicate), _store.CompileQuerySelector(_shape, selector), _diagnostics);
        }

        public LawfulNodeQuery<TRoot, TNode> Where(Expression<Func<TNode, bool>> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            Func<int, bool> test = _store.CompileQuerySelector(_shape, predicate);
            int[] scratch = new int[_coordinates.Length];
            int output = 0;
            for (int i = 0; i < _coordinates.Length; i++)
            {
                _diagnostics.CoordinateVisits++;
                _diagnostics.PredicateEvaluations++;
                if (test(_coordinates[i])) scratch[output++] = _coordinates[i];
            }
            int[] result = new int[output];
            Array.Copy(scratch, result, output);
            _diagnostics.OperatorExecutions++;
            return new LawfulNodeQuery<TRoot, TNode>(_store, _shape, result, _diagnostics);
        }

        public LawfulValueQuery<TResult> Select<TResult>(Expression<Func<TNode, TResult>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            Func<int, TResult> read = _store.CompileQuerySelector(_shape, selector);
            TResult[] values = new TResult[_coordinates.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = read(_coordinates[i]);
                _diagnostics.ScalarReads++;
            }
            _diagnostics.ResultValues += values.Length;
            return new LawfulValueQuery<TResult>(values, _diagnostics);
        }

        public bool Any()
        { if (_coordinates.Length != 0) _diagnostics.EarlyExits++; return _coordinates.Length != 0; }

        public bool Any(Expression<Func<TNode, bool>> predicate)
        {
            Func<int, bool> test = _store.CompileQuerySelector(_shape, predicate);
            for (int i = 0; i < _coordinates.Length; i++)
            { _diagnostics.PredicateEvaluations++; if (test(_coordinates[i])) { _diagnostics.EarlyExits++; return true; } }
            return false;
        }

        public bool All(Expression<Func<TNode, bool>> predicate)
        {
            Func<int, bool> test = _store.CompileQuerySelector(_shape, predicate);
            for (int i = 0; i < _coordinates.Length; i++)
            { _diagnostics.PredicateEvaluations++; if (!test(_coordinates[i])) { _diagnostics.EarlyExits++; return false; } }
            return true;
        }

        public int CountWhere(Expression<Func<TNode, bool>> predicate)
        {
            Func<int, bool> test = _store.CompileQuerySelector(_shape, predicate);
            int count = 0;
            for (int i = 0; i < _coordinates.Length; i++) { _diagnostics.PredicateEvaluations++; if (test(_coordinates[i])) count++; }
            return count;
        }

        public LawfulNodeQuery<TRoot, TNode> Skip(int count) => Slice(count < 0 ? 0 : count, _coordinates.Length);
        public LawfulNodeQuery<TRoot, TNode> Take(int count) => Slice(0, count < 0 ? 0 : count);
        public LawfulNodeQuery<TRoot, TNode> SkipLast(int count) => Slice(0, Math.Max(0, _coordinates.Length - Math.Max(0, count)));
        public LawfulNodeQuery<TRoot, TNode> TakeLast(int count) => Slice(Math.Max(0, _coordinates.Length - Math.Max(0, count)), _coordinates.Length);
        public LawfulNodeQuery<TRoot, TNode> Reverse()
        {
            int[] result = new int[_coordinates.Length];
            for (int i = 0; i < result.Length; i++) result[i] = _coordinates[result.Length - 1 - i];
            return new LawfulNodeQuery<TRoot, TNode>(_store, _shape, result, _diagnostics);
        }

        private LawfulNodeQuery<TRoot, TNode> Slice(int start, int exclusiveEnd)
        {
            start = Math.Min(Math.Max(0, start), _coordinates.Length);
            exclusiveEnd = Math.Min(Math.Max(start, exclusiveEnd), _coordinates.Length);
            int[] result = new int[exclusiveEnd - start];
            Array.Copy(_coordinates, start, result, 0, result.Length);
            return new LawfulNodeQuery<TRoot, TNode>(_store, _shape, result, _diagnostics);
        }
    }

    public sealed class LawfulValueQuery<T>
    {
        private readonly T[] _values;
        private readonly LawfulQueryDiagnostics _diagnostics;
        public LawfulValueQuery(T[] values, LawfulQueryDiagnostics diagnostics) { _values = values; _diagnostics = diagnostics; }
        public int Count => _values.Length;
        public LawfulQueryDiagnostics Diagnostics => _diagnostics;
        public LawfulPreparedValuePlan<T> Prepare(int maximumAdditionalValues = 0) => new LawfulPreparedValuePlan<T>(_values, maximumAdditionalValues, _diagnostics);
        public LawfulCompositionBuilder<T> Compose(int maximumAdditionalValues = 0) => new LawfulCompositionBuilder<T>(_values, maximumAdditionalValues, _diagnostics);
        internal T[] UnsafeValues => _values;
        public T[] ToArray() { var copy = new T[_values.Length]; Array.Copy(_values, copy, copy.Length); return copy; }

        public LawfulValueQuery<T> Where(Func<T, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            T[] scratch = new T[_values.Length]; int output = 0;
            for (int i = 0; i < _values.Length; i++) { _diagnostics.PredicateEvaluations++; if (predicate(_values[i])) scratch[output++] = _values[i]; }
            T[] result = new T[output]; Array.Copy(scratch, result, output); _diagnostics.OperatorExecutions++;
            return new LawfulValueQuery<T>(result, _diagnostics);
        }

        public LawfulValueQuery<TResult> Select<TResult>(Func<T, TResult> selector)
        {
            TResult[] result = new TResult[_values.Length];
            for (int i = 0; i < result.Length; i++) result[i] = selector(_values[i]);
            return new LawfulValueQuery<TResult>(result, _diagnostics);
        }

        public LawfulValueQuery<T> Distinct(IEqualityComparer<T> comparer = null)
        {
            var seen = new NativeMembershipTable<T>(_values.Length, comparer);
            _diagnostics.NativeTableBuilds++;
            T[] scratch = new T[_values.Length]; int count = 0;
            for (int i = 0; i < _values.Length; i++) { _diagnostics.MembershipProbes++; if (seen.Add(_values[i])) scratch[count++] = _values[i]; }
            T[] result = new T[count]; Array.Copy(scratch, result, count);
            _diagnostics.OperatorExecutions++;
            return new LawfulValueQuery<T>(result, _diagnostics);
        }

        public LawfulValueQuery<T> OrderByRank(Func<T, long> rankSelector, bool descending = false)
        {
            if (rankSelector == null) throw new ArgumentNullException(nameof(rankSelector));
            T[] current = ToArray(); T[] next = new T[current.Length];
            ulong[] ranks = new ulong[current.Length]; ulong[] nextRanks = new ulong[current.Length];
            for (int i = 0; i < current.Length; i++) ranks[i] = unchecked((ulong)(rankSelector(current[i]) ^ long.MinValue));
            for (int shift = 0; shift < 64; shift += 8)
            {
                int[] counts = new int[256];
                for (int i = 0; i < ranks.Length; i++) counts[(int)((ranks[i] >> shift) & 255)]++;
                int total = 0;
                if (!descending)
                    for (int b = 0; b < 256; b++) { int c = counts[b]; counts[b] = total; total += c; }
                else
                    for (int b = 255; b >= 0; b--) { int c = counts[b]; counts[b] = total; total += c; }
                for (int i = 0; i < ranks.Length; i++)
                {
                    int b = (int)((ranks[i] >> shift) & 255); int p = counts[b]++;
                    next[p] = current[i]; nextRanks[p] = ranks[i];
                }
                var tv = current; current = next; next = tv;
                var tr = ranks; ranks = nextRanks; nextRanks = tr;
                _diagnostics.RankPasses++;
            }
            return new LawfulValueQuery<T>(current, _diagnostics);
        }

        public LawfulValueQuery<T> Skip(int count) => Slice(Math.Max(0, count), _values.Length);
        public LawfulValueQuery<T> Take(int count) => Slice(0, Math.Max(0, count));
        public LawfulValueQuery<T> SkipLast(int count) => Slice(0, Math.Max(0, _values.Length - Math.Max(0, count)));
        public LawfulValueQuery<T> TakeLast(int count) => Slice(Math.Max(0, _values.Length - Math.Max(0, count)), _values.Length);
        public LawfulValueQuery<T> Reverse()
        { T[] result = new T[_values.Length]; for (int i = 0; i < result.Length; i++) result[i] = _values[result.Length - 1 - i]; return new LawfulValueQuery<T>(result, _diagnostics); }
        public LawfulValueQuery<T> Append(T value) { T[] result = new T[_values.Length + 1]; Array.Copy(_values, result, _values.Length); result[_values.Length] = value; return new LawfulValueQuery<T>(result, _diagnostics); }
        public LawfulValueQuery<T> Prepend(T value) { T[] result = new T[_values.Length + 1]; result[0] = value; Array.Copy(_values, 0, result, 1, _values.Length); return new LawfulValueQuery<T>(result, _diagnostics); }
        public LawfulValueQuery<T> Concat(LawfulValueQuery<T> other) { T[] result = new T[_values.Length + other._values.Length]; Array.Copy(_values, result, _values.Length); Array.Copy(other._values, 0, result, _values.Length, other._values.Length); return new LawfulValueQuery<T>(result, _diagnostics); }

        public LawfulValueQuery<T> Union(LawfulValueQuery<T> other, IEqualityComparer<T> comparer = null) => Concat(other).Distinct(comparer);
        public LawfulValueQuery<T> Intersect(LawfulValueQuery<T> other, IEqualityComparer<T> comparer = null)
        {
            var membership = new NativeMembershipTable<T>(other._values.Length, comparer);
            var emitted = new NativeMembershipTable<T>(_values.Length, comparer);
            _diagnostics.NativeTableBuilds += 2;
            for (int i = 0; i < other._values.Length; i++) membership.Add(other._values[i]);
            T[] scratch = new T[_values.Length]; int count = 0;
            for (int i = 0; i < _values.Length; i++) { _diagnostics.MembershipProbes += 2; if (membership.Contains(_values[i]) && emitted.Add(_values[i])) scratch[count++] = _values[i]; }
            T[] result = new T[count]; Array.Copy(scratch, result, count); _diagnostics.OperatorExecutions++; return new LawfulValueQuery<T>(result, _diagnostics);
        }
        public LawfulValueQuery<T> Except(LawfulValueQuery<T> other, IEqualityComparer<T> comparer = null)
        {
            var membership = new NativeMembershipTable<T>(other._values.Length, comparer);
            var emitted = new NativeMembershipTable<T>(_values.Length, comparer);
            _diagnostics.NativeTableBuilds += 2;
            for (int i = 0; i < other._values.Length; i++) membership.Add(other._values[i]);
            T[] scratch = new T[_values.Length]; int count = 0;
            for (int i = 0; i < _values.Length; i++) { _diagnostics.MembershipProbes += 2; if (!membership.Contains(_values[i]) && emitted.Add(_values[i])) scratch[count++] = _values[i]; }
            T[] result = new T[count]; Array.Copy(scratch, result, count); _diagnostics.OperatorExecutions++; return new LawfulValueQuery<T>(result, _diagnostics);
        }

        public bool Any() => _values.Length != 0;
        public bool Any(Func<T, bool> predicate) { for (int i = 0; i < _values.Length; i++) if (predicate(_values[i])) { _diagnostics.EarlyExits++; return true; } return false; }
        public bool All(Func<T, bool> predicate) { for (int i = 0; i < _values.Length; i++) if (!predicate(_values[i])) { _diagnostics.EarlyExits++; return false; } return true; }
        public bool Contains(T value, IEqualityComparer<T> comparer = null) { var cmp = comparer ?? EqualityComparer<T>.Default; for (int i = 0; i < _values.Length; i++) if (cmp.Equals(_values[i], value)) return true; return false; }
        public T First() => _values.Length == 0 ? throw new InvalidOperationException("Sequence contains no elements.") : _values[0];
        public T FirstOrDefault() => _values.Length == 0 ? default(T) : _values[0];
        public T Last() => _values.Length == 0 ? throw new InvalidOperationException("Sequence contains no elements.") : _values[_values.Length - 1];
        public T LastOrDefault() => _values.Length == 0 ? default(T) : _values[_values.Length - 1];
        public T ElementAt(int index) => (uint)index >= (uint)_values.Length ? throw new ArgumentOutOfRangeException(nameof(index)) : _values[index];
        public T ElementAtOrDefault(int index) => (uint)index >= (uint)_values.Length ? default(T) : _values[index];
        public T Single() => _values.Length != 1 ? throw new InvalidOperationException("Sequence does not contain exactly one element.") : _values[0];
        public T SingleOrDefault() => _values.Length > 1 ? throw new InvalidOperationException("Sequence contains more than one element.") : (_values.Length == 0 ? default(T) : _values[0]);
        public long LongCount() => _values.LongLength;
        public T Min(IComparer<T> comparer = null) { if (_values.Length == 0) throw new InvalidOperationException(); var cmp = comparer ?? Comparer<T>.Default; T v = _values[0]; for (int i=1;i<_values.Length;i++) if(cmp.Compare(_values[i],v)<0)v=_values[i]; return v; }
        public T Max(IComparer<T> comparer = null) { if (_values.Length == 0) throw new InvalidOperationException(); var cmp = comparer ?? Comparer<T>.Default; T v = _values[0]; for (int i=1;i<_values.Length;i++) if(cmp.Compare(_values[i],v)>0)v=_values[i]; return v; }
        public TAccumulate Aggregate<TAccumulate>(TAccumulate seed, Func<TAccumulate,T,TAccumulate> accumulator) { TAccumulate v=seed; for(int i=0;i<_values.Length;i++)v=accumulator(v,_values[i]); return v; }
        public decimal SumDecimal(Func<T, decimal> selector) { decimal sum=0; for(int i=0;i<_values.Length;i++)sum+=selector(_values[i]); return sum; }
        public double AverageDouble(Func<T, double> selector) { if(_values.Length==0)throw new InvalidOperationException(); double sum=0; for(int i=0;i<_values.Length;i++)sum+=selector(_values[i]); return sum/_values.Length; }
        public bool SequenceEqual(LawfulValueQuery<T> other, IEqualityComparer<T> comparer=null) { if(_values.Length!=other._values.Length)return false; var cmp=comparer??EqualityComparer<T>.Default; for(int i=0;i<_values.Length;i++)if(!cmp.Equals(_values[i],other._values[i]))return false; return true; }
        public LawfulValueQuery<T> DefaultIfEmpty(T value=default(T)) => _values.Length==0 ? new LawfulValueQuery<T>(new[]{value},_diagnostics) : this;

        public LawfulGrouping<TKey,T>[] GroupBy<TKey>(Func<T,TKey> keySelector, IEqualityComparer<TKey> comparer=null)
        {
            var map = new NativeMultiMap<TKey,T>(_values.Length, comparer);
            _diagnostics.NativeTableBuilds++;
            for(int i=0;i<_values.Length;i++) map.Add(keySelector(_values[i]), _values[i]);
            _diagnostics.OperatorExecutions++;
            return map.ToGroupings();
        }

        public LawfulValueQuery<TResult> Join<TInner,TKey,TResult>(LawfulValueQuery<TInner> inner, Func<T,TKey> outerKey, Func<TInner,TKey> innerKey, Func<T,TInner,TResult> resultSelector, IEqualityComparer<TKey> comparer=null)
        {
            var map = new NativeMultiMap<TKey,TInner>(inner._values.Length, comparer);
            _diagnostics.NativeTableBuilds++;
            for(int i=0;i<inner._values.Length;i++) map.Add(innerKey(inner._values[i]), inner._values[i]);
            TResult[] scratch = new TResult[Math.Max(4, _values.Length)]; int count = 0;
            for(int i=0;i<_values.Length;i++)
            {
                int entry = map.GetHead(outerKey(_values[i]));
                _diagnostics.MembershipProbes++;
                while(entry >= 0)
                {
                    if(count==scratch.Length) Array.Resize(ref scratch, scratch.Length*2);
                    scratch[count++]=resultSelector(_values[i],map.ValueAt(entry));
                    entry = map.NextAt(entry);
                }
            }
            TResult[] result=new TResult[count]; Array.Copy(scratch,result,count); _diagnostics.OperatorExecutions++;
            return new LawfulValueQuery<TResult>(result,_diagnostics);
        }

        public LawfulValueQuery<TResult> Zip<TOther,TResult>(LawfulValueQuery<TOther> other, Func<T,TOther,TResult> selector)
        { int count=Math.Min(_values.Length,other._values.Length); TResult[] result=new TResult[count]; for(int i=0;i<count;i++)result[i]=selector(_values[i],other._values[i]); return new LawfulValueQuery<TResult>(result,_diagnostics); }

        private LawfulValueQuery<T> Slice(int start,int end){start=Math.Min(Math.Max(0,start),_values.Length);end=Math.Min(Math.Max(start,end),_values.Length);T[] result=new T[end-start];Array.Copy(_values,start,result,0,result.Length);return new LawfulValueQuery<T>(result,_diagnostics);}
    }

    public sealed class LawfulGrouping<TKey,TElement>
    {
        internal LawfulGrouping(TKey key,TElement[] values){Key=key;Values=values;}
        public TKey Key { get; }
        public TElement[] Values { get; }
        public int Count => Values.Length;
    }

    internal static class NativeHash
    {
        // Murmur-inspired 32-bit avalanche. Table masking happens only after all
        // source hash bits have influenced the low bits, preventing inputs such
        // as i << 13 from collapsing into one initial probe slot.
        internal static int FinalizeToSlot(int hash, int mask)
        {
            uint x = unchecked((uint)hash);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (int)(x & unchecked((uint)mask));
        }
    }

    internal sealed class NativeMembershipTable<T>
    {
        private readonly IEqualityComparer<T> _comparer;
        private readonly T[] _keys;
        private readonly byte[] _used;
        private readonly int _mask;
        public NativeMembershipTable(int expected, IEqualityComparer<T> comparer)
        {
            _comparer = comparer ?? EqualityComparer<T>.Default;
            int capacity = 4;
            while (capacity < Math.Max(4, expected * 2)) capacity <<= 1;
            _keys = new T[capacity]; _used = new byte[capacity]; _mask = capacity - 1;
        }
        public bool Add(T value)
        {
            int hash = ReferenceEquals(value, null) ? 0 : _comparer.GetHashCode(value);
            int slot = NativeHash.FinalizeToSlot(hash, _mask);
            while (_used[slot] != 0)
            { if (_comparer.Equals(_keys[slot], value)) return false; slot = (slot + 1) & _mask; }
            _used[slot] = 1; _keys[slot] = value; return true;
        }
        public bool Contains(T value)
        {
            int hash = ReferenceEquals(value, null) ? 0 : _comparer.GetHashCode(value);
            int slot = NativeHash.FinalizeToSlot(hash, _mask);
            while (_used[slot] != 0)
            { if (_comparer.Equals(_keys[slot], value)) return true; slot = (slot + 1) & _mask; }
            return false;
        }
    }

    internal readonly struct NativeValueSlice<T>
    {
        private readonly T[] _values; private readonly int _start;
        public NativeValueSlice(T[] values,int start,int count){_values=values;_start=start;Count=count;}
        public int Count { get; }
        public T this[int index] => _values[_start+index];
    }

    internal sealed class NativeMultiMap<TKey,TValue>
    {
        private readonly IEqualityComparer<TKey> _comparer;
        private readonly TKey[] _keys; private readonly int[] _heads; private readonly int[] _tails; private readonly byte[] _used; private readonly int _mask;
        private readonly int[] _keySlots;
        private readonly TValue[] _values; private readonly int[] _next; private int _count; private int _keyCount;
        public NativeMultiMap(int expected,IEqualityComparer<TKey> comparer)
        {
            _comparer=comparer??EqualityComparer<TKey>.Default; int cap=4; while(cap<Math.Max(4,expected*2))cap<<=1;
            _keys=new TKey[cap];_heads=new int[cap];_tails=new int[cap];_used=new byte[cap];_mask=cap-1;_keySlots=new int[cap];
            _values=new TValue[Math.Max(1,expected)];_next=new int[Math.Max(1,expected)];
            for(int i=0;i<_heads.Length;i++){_heads[i]=-1;_tails[i]=-1;}
        }
        public void Add(TKey key,TValue value)
        {
            int slot=FindSlot(key,true); int index=_count++; _values[index]=value; _next[index]=-1;
            if(_heads[slot]<0)_heads[slot]=index;else _next[_tails[slot]]=index;_tails[slot]=index;
        }
        public int GetHead(TKey key){int slot=FindSlot(key,false);return slot<0?-1:_heads[slot];}
        public TValue ValueAt(int entry)=>_values[entry];
        public int NextAt(int entry)=>_next[entry];
        public NativeValueSlice<TValue> Get(TKey key)
        {
            int entry=GetHead(key);if(entry<0)return new NativeValueSlice<TValue>(Array.Empty<TValue>(),0,0);
            int count=0;for(int p=entry;p>=0;p=_next[p])count++;TValue[] result=new TValue[count];int i=0;for(int p=entry;p>=0;p=_next[p])result[i++]=_values[p];
            return new NativeValueSlice<TValue>(result,0,count);
        }
        public LawfulGrouping<TKey,TValue>[] ToGroupings()
        {
            var result=new LawfulGrouping<TKey,TValue>[_keyCount];
            for(int o=0;o<_keyCount;o++)
            {int slot=_keySlots[o];var slice=Get(_keys[slot]);TValue[] vals=new TValue[slice.Count];for(int i=0;i<vals.Length;i++)vals[i]=slice[i];result[o]=new LawfulGrouping<TKey,TValue>(_keys[slot],vals);}
            return result;
        }
        private int FindSlot(TKey key,bool create)
        {
            int hash=ReferenceEquals(key,null)?0:_comparer.GetHashCode(key);int slot=NativeHash.FinalizeToSlot(hash,_mask);
            while(_used[slot]!=0){if(_comparer.Equals(_keys[slot],key))return slot;slot=(slot+1)&_mask;}
            if(!create)return -1;_used[slot]=1;_keys[slot]=key;_heads[slot]=-1;_tails[slot]=-1;_keySlots[_keyCount++]=slot;return slot;
        }
    }



    public sealed class LawfulPreparedNodeCountPlan<TRoot, TNode, TResult>
        where TRoot : class, new()
        where TNode : class, new()
    {
        private readonly int[] _coordinates;
        private readonly Func<int, bool> _predicate;
        private readonly Func<int, TResult> _selector;
        private readonly LawfulQueryDiagnostics _diagnostics;

        internal LawfulPreparedNodeCountPlan(int[] coordinates, Func<int, bool> predicate, Func<int, TResult> selector, LawfulQueryDiagnostics diagnostics)
        { _coordinates = coordinates; _predicate = predicate; _selector = selector; _diagnostics = diagnostics; }

        public int ExecuteCount()
        {
            int count = 0;
            for (int i = 0; i < _coordinates.Length; i++)
            {
                _diagnostics.CoordinateVisits++;
                _diagnostics.PredicateEvaluations++;
                if (!_predicate(_coordinates[i])) continue;
                // Read the projection in the same fused pass so the compiler cannot
                // replace the operation with predicate-only counting.
                _selector(_coordinates[i]);
                _diagnostics.ScalarReads++;
                count++;
            }
            _diagnostics.OperatorExecutions++;
            return count;
        }
    }

    /// <summary>
    /// Reusable Memory2-native execution workspace. Preparation owns all hash,
    /// rank, grouping, join, and result scratch. Execution advances epochs and
    /// writes into retained buffers; it does not allocate transient engine state.
    /// </summary>
    public sealed class LawfulPreparedValuePlan<T>
    {
        private readonly T[] _source;
        private readonly T[] _scratchA;
        private readonly T[] _scratchB;
        private readonly ulong[] _ranksA;
        private readonly ulong[] _ranksB;
        private readonly int[] _radixCounts = new int[256];
        private readonly EpochMembershipTable<T> _membershipA;
        private readonly EpochMembershipTable<T> _membershipB;
        private readonly LawfulQueryDiagnostics _diagnostics;

        internal LawfulPreparedValuePlan(T[] source, int maximumAdditionalValues, LawfulQueryDiagnostics diagnostics)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            int capacity = Math.Max(1, checked(source.Length + Math.Max(0, maximumAdditionalValues)));
            _scratchA = new T[capacity];
            _scratchB = new T[capacity];
            _ranksA = new ulong[capacity];
            _ranksB = new ulong[capacity];
            _membershipA = new EpochMembershipTable<T>(capacity, null);
            _membershipB = new EpochMembershipTable<T>(capacity, null);
            _diagnostics = diagnostics ?? new LawfulQueryDiagnostics();
        }

        public long PreparedWorkspaceBytes =>
            ((long)_scratchA.Length + _scratchB.Length) * ApproximateElementBytes<T>() +
            ((long)_ranksA.Length + _ranksB.Length) * sizeof(ulong) +
            _radixCounts.Length * sizeof(int) + _membershipA.ApproximateBytes + _membershipB.ApproximateBytes;

        public int WhereInto(Func<T, bool> predicate)
        {
            int count = 0;
            for (int i = 0; i < _source.Length; i++)
            { _diagnostics.PredicateEvaluations++; if (predicate(_source[i])) _scratchA[count++] = _source[i]; }
            _diagnostics.OperatorExecutions++;
            return count;
        }

        public int SelectInto<TResult>(Func<T, TResult> selector, TResult[] destination)
        {
            if (destination == null || destination.Length < _source.Length) throw new ArgumentException("Destination is too small.", nameof(destination));
            for (int i = 0; i < _source.Length; i++) destination[i] = selector(_source[i]);
            _diagnostics.OperatorExecutions++;
            return _source.Length;
        }

        public int DistinctInto()
        {
            _membershipA.NextEpoch(); int count = 0;
            for (int i = 0; i < _source.Length; i++)
            { _diagnostics.MembershipProbes++; if (_membershipA.Add(_source[i])) _scratchA[count++] = _source[i]; }
            _diagnostics.OperatorExecutions++;
            return count;
        }

        public int UnionInto(T[] other)
        {
            EnsureCapacity(_source.Length + other.Length);
            _membershipA.NextEpoch(); int count = 0;
            for (int i = 0; i < _source.Length; i++) if (_membershipA.Add(_source[i])) _scratchA[count++] = _source[i];
            for (int i = 0; i < other.Length; i++) if (_membershipA.Add(other[i])) _scratchA[count++] = other[i];
            _diagnostics.MembershipProbes += _source.Length + other.Length; _diagnostics.OperatorExecutions++;
            return count;
        }

        public int IntersectInto(T[] other)
        {
            _membershipA.NextEpoch(); _membershipB.NextEpoch();
            for (int i = 0; i < other.Length; i++) _membershipA.Add(other[i]);
            int count = 0;
            for (int i = 0; i < _source.Length; i++) if (_membershipA.Contains(_source[i]) && _membershipB.Add(_source[i])) _scratchA[count++] = _source[i];
            _diagnostics.MembershipProbes += other.Length + _source.Length * 2L; _diagnostics.OperatorExecutions++;
            return count;
        }

        public int ExceptInto(T[] other)
        {
            _membershipA.NextEpoch(); _membershipB.NextEpoch();
            for (int i = 0; i < other.Length; i++) _membershipA.Add(other[i]);
            int count = 0;
            for (int i = 0; i < _source.Length; i++) if (!_membershipA.Contains(_source[i]) && _membershipB.Add(_source[i])) _scratchA[count++] = _source[i];
            _diagnostics.MembershipProbes += other.Length + _source.Length * 2L; _diagnostics.OperatorExecutions++;
            return count;
        }

        public int OrderByRankInto(Func<T, long> rankSelector, bool descending = false)
        {
            int length = _source.Length;
            Array.Copy(_source, _scratchA, length);
            for (int i = 0; i < length; i++) _ranksA[i] = unchecked((ulong)(rankSelector(_source[i]) ^ long.MinValue));
            T[] current = _scratchA, next = _scratchB; ulong[] ranks = _ranksA, nextRanks = _ranksB;
            for (int shift = 0; shift < 64; shift += 8)
            {
                Array.Clear(_radixCounts, 0, _radixCounts.Length);
                for (int i = 0; i < length; i++) _radixCounts[(int)((ranks[i] >> shift) & 255)]++;
                int total = 0;
                if (!descending) for (int b = 0; b < 256; b++) { int c = _radixCounts[b]; _radixCounts[b] = total; total += c; }
                else for (int b = 255; b >= 0; b--) { int c = _radixCounts[b]; _radixCounts[b] = total; total += c; }
                for (int i = 0; i < length; i++) { int b = (int)((ranks[i] >> shift) & 255); int p = _radixCounts[b]++; next[p] = current[i]; nextRanks[p] = ranks[i]; }
                T[] tv = current; current = next; next = tv; ulong[] tr = ranks; ranks = nextRanks; nextRanks = tr;
                _diagnostics.RankPasses++;
            }
            if (!ReferenceEquals(current, _scratchA)) Array.Copy(current, _scratchA, length);
            _diagnostics.OperatorExecutions++;
            return length;
        }

        public int GroupCount<TKey>(Func<T, TKey> keySelector)
        {
            var keys = PreparedKeyCounter<TKey>.ForCapacity(_source.Length);
            keys.NextEpoch();
            for (int i = 0; i < _source.Length; i++) keys.Add(keySelector(_source[i]));
            _diagnostics.MembershipProbes += _source.Length; _diagnostics.OperatorExecutions++;
            return keys.KeyCount;
        }

        public long JoinCount<TInner, TKey>(TInner[] inner, Func<T, TKey> outerKey, Func<TInner, TKey> innerKey)
        {
            var keys = PreparedKeyCounter<TKey>.ForCapacity(inner.Length);
            keys.NextEpoch();
            for (int i = 0; i < inner.Length; i++) keys.Increment(innerKey(inner[i]));
            long count = 0;
            for (int i = 0; i < _source.Length; i++) count += keys.GetCount(outerKey(_source[i]));
            _diagnostics.MembershipProbes += inner.Length + _source.Length; _diagnostics.OperatorExecutions++;
            return count;
        }

        public int ZipInto<TOther, TResult>(TOther[] other, Func<T, TOther, TResult> selector, TResult[] destination)
        {
            int count = Math.Min(_source.Length, other.Length);
            if (destination == null || destination.Length < count) throw new ArgumentException("Destination is too small.", nameof(destination));
            for (int i = 0; i < count; i++) destination[i] = selector(_source[i], other[i]);
            _diagnostics.OperatorExecutions++;
            return count;
        }

        public int SliceReverseCount(int skip, int take)
        {
            int start = Math.Min(Math.Max(0, skip), _source.Length);
            int count = Math.Min(Math.Max(0, take), _source.Length - start);
            // Rank/select view: reversing changes coordinate direction, not storage.
            _diagnostics.OperatorExecutions++;
            return count;
        }

        public int ConcatAppendPrependCount(T[] other) => checked(_source.Length + other.Length + 2);

        private void EnsureCapacity(int required)
        { if (required > _scratchA.Length) throw new InvalidOperationException("Prepared capacity is too small; prepare with maximumAdditionalValues."); }

        private static int ApproximateElementBytes<TElement>() => typeof(TElement).IsValueType ? Math.Max(1, System.Runtime.InteropServices.Marshal.SizeOf(typeof(TElement))) : IntPtr.Size;
    }

    internal sealed class EpochMembershipTable<T>
    {
        private readonly IEqualityComparer<T> _comparer;
        private readonly T[] _keys;
        private readonly int[] _epochs;
        private readonly int _mask;
        private int _epoch = 1;
        internal EpochMembershipTable(int expected, IEqualityComparer<T> comparer)
        { _comparer = comparer ?? EqualityComparer<T>.Default; int cap = 4; while (cap < Math.Max(4, expected * 2)) cap <<= 1; _keys = new T[cap]; _epochs = new int[cap]; _mask = cap - 1; }
        internal long ApproximateBytes => (long)_keys.Length * (typeof(T).IsValueType ? Math.Max(1, System.Runtime.InteropServices.Marshal.SizeOf(typeof(T))) : IntPtr.Size) + (long)_epochs.Length * sizeof(int);
        internal void NextEpoch() { if (++_epoch == 0) { Array.Clear(_epochs, 0, _epochs.Length); _epoch = 1; } }
        internal bool Add(T value) { int slot = Slot(value); while (_epochs[slot] == _epoch) { if (_comparer.Equals(_keys[slot], value)) return false; slot = (slot + 1) & _mask; } _epochs[slot] = _epoch; _keys[slot] = value; return true; }
        internal bool Contains(T value) { int slot = Slot(value); while (_epochs[slot] == _epoch) { if (_comparer.Equals(_keys[slot], value)) return true; slot = (slot + 1) & _mask; } return false; }
        private int Slot(T value) { int hash = ReferenceEquals(value, null) ? 0 : _comparer.GetHashCode(value); return NativeHash.FinalizeToSlot(hash, _mask); }
    }

    internal sealed class PreparedKeyCounter<TKey>
    {
        [ThreadStatic] private static PreparedKeyCounter<TKey> _cached;
        private readonly IEqualityComparer<TKey> _comparer = EqualityComparer<TKey>.Default;
        private TKey[] _keys; private int[] _epochs; private int[] _counts; private int _mask; private int _epoch = 1;
        private PreparedKeyCounter(int expected) { Resize(expected); }
        internal static PreparedKeyCounter<TKey> ForCapacity(int expected) { var c = _cached; if (c == null) _cached = c = new PreparedKeyCounter<TKey>(expected); else c.Ensure(expected); return c; }
        internal int KeyCount { get; private set; }
        internal void NextEpoch() { if (++_epoch == 0) { Array.Clear(_epochs, 0, _epochs.Length); _epoch = 1; } KeyCount = 0; }
        internal void Add(TKey key) { int s = Find(key, true); if (_counts[s] == 0) { _counts[s] = 1; KeyCount++; } }
        internal void Increment(TKey key) { int s = Find(key, true); if (_counts[s] == 0) KeyCount++; _counts[s]++; }
        internal int GetCount(TKey key) { int s = Find(key, false); return s < 0 ? 0 : _counts[s]; }
        private int Find(TKey key, bool create) { int hash = ReferenceEquals(key, null) ? 0 : _comparer.GetHashCode(key); int s = NativeHash.FinalizeToSlot(hash, _mask); while (_epochs[s] == _epoch) { if (_comparer.Equals(_keys[s], key)) return s; s = (s + 1) & _mask; } if (!create) return -1; _epochs[s] = _epoch; _keys[s] = key; _counts[s] = 0; return s; }
        private void Ensure(int expected) { if (expected * 2 > _keys.Length) Resize(expected); }
        private void Resize(int expected) { int cap = 4; while (cap < Math.Max(4, expected * 2)) cap <<= 1; _keys = new TKey[cap]; _epochs = new int[cap]; _counts = new int[cap]; _mask = cap - 1; _epoch = 1; KeyCount = 0; }
    }

}
