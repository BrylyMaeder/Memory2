using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Memory2
{
    internal enum GraphSlotKind { Root, Property, SequenceElement, DictionaryValue }

    internal readonly struct GraphIdentity
    {
        public readonly Type RootType;
        public readonly long RootId;
        public readonly Type NodeType;
        public readonly long NodeId;
        public readonly long ParentNodeId;
        public readonly string PropertyName;
        public readonly int Ordinal;
        public readonly object DictionaryKey;
        public readonly GraphSlotKind SlotKind;
        public readonly ulong TransitionToken;
        public readonly int CoordinateSalt;

        public GraphIdentity(Type rootType, long rootId, Type nodeType, long nodeId,
            long parentNodeId, string propertyName, int ordinal, object dictionaryKey,
            GraphSlotKind slotKind, ulong transitionToken, int coordinateSalt)
        {
            RootType = rootType;
            RootId = rootId;
            NodeType = nodeType;
            NodeId = nodeId;
            ParentNodeId = parentNodeId;
            PropertyName = propertyName;
            Ordinal = ordinal;
            DictionaryKey = dictionaryKey;
            SlotKind = slotKind;
            TransitionToken = transitionToken;
            CoordinateSalt = coordinateSalt;
        }
    }

    internal sealed class MaterializedGraphIdentity
    {
        public GraphIdentity Value { get; }
        public MaterializedGraphIdentity(GraphIdentity value) => Value = value;
    }

    public sealed class GraphStatistics
    {
        public long Roots { get; internal set; }
        public long Coordinates { get; internal set; }
        public long DirectDescendantWrites { get; internal set; }
        public long RootRewritesAvoided { get; internal set; }
        public long CoordinateMaterializations { get; internal set; }
        public long ParentResolutions { get; internal set; }
        public long RootResolutions { get; internal set; }
        public long CascadeRootDeletes { get; internal set; }
        public long ScalarSnapshots { get; internal set; }
        public long RelationshipSlots { get; internal set; }
        public long RelationshipDomainRebuilds { get; internal set; }
        public long CoordinateDerivations { get; internal set; }
        public long CoordinateCollisionRepairs { get; internal set; }
        public long InverseCoordinateValidations { get; internal set; }
        public long InlineIdentityRecords { get; internal set; }
        public long CandidateLawDerivedRelationships { get; internal set; }
        public long PhysicalRelationshipExceptions { get; internal set; }
        public long ScalarShellProjections { get; internal set; }
        public long NativeDescendantPredicateEvaluations { get; internal set; }
        public long NativeRootCandidates { get; internal set; }
        public long NativeQueryMaterializations { get; internal set; }
        public long CorrelatedBindingChecks { get; internal set; }
        public long AncestorCoordinateWalks { get; internal set; }
    }

    internal sealed class OwnedGraphRegistry
    {
        private sealed class Node
        {
            public GraphIdentity Identity;
            public object ScalarSnapshot;
            public bool IsAlias;
            public long CanonicalNodeId;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, List<Node>> _byRoot = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        private readonly Dictionary<Type, List<Node>> _byType = new Dictionary<Type, List<Node>>();
        private readonly ConditionalWeakTable<object, MaterializedGraphIdentity> _returned = new ConditionalWeakTable<object, MaterializedGraphIdentity>();
        private long _directWrites;
        private long _rootRewritesAvoided;
        private long _materializations;
        private long _parentResolutions;
        private long _rootResolutions;
        private long _cascadeDeletes;
        private long _relationshipRebuilds;
        private long _coordinateDerivations;
        private long _collisionRepairs;
        private long _inverseValidations;
        private long _scalarShellProjections;
        private long _nativePredicateEvaluations;
        private long _nativeRootCandidates;
        private long _correlatedBindingChecks;
        private long _ancestorCoordinateWalks;

        public void ReplaceRoot(Type rootType, long rootId, object root)
        {
            if (rootType == null) throw new ArgumentNullException(nameof(rootType));
            if (root == null) throw new ArgumentNullException(nameof(root));
            lock (_gate)
            {
                RemoveRootCore(rootType, rootId, false);
                var nodes = new List<Node>();
                Walk(root, rootType, rootId, 0, null, -1, null, GraphSlotKind.Root, nodes, new Dictionary<object, long>(ReferenceComparer.Instance));
                _byRoot[RootKey(rootType, rootId)] = nodes;
                foreach (Node node in nodes.Where(n => !n.IsAlias))
                {
                    if (!_byType.TryGetValue(node.Identity.NodeType, out List<Node> list))
                        _byType[node.Identity.NodeType] = list = new List<Node>();
                    list.Add(node);
                }
            }
        }

        public void RemoveRoot(Type rootType, long rootId)
        {
            lock (_gate) RemoveRootCore(rootType, rootId, true);
        }

        public T MaterializeRoot<T>(long rootId) where T : class
        {
            lock (_gate)
            {
                Node root = GetRootNodeCore(typeof(T), rootId);
                return root == null ? null : (T)MaterializeNodeCore(root);
            }
        }

        public object MaterializeRoot(Type rootType, long rootId)
        {
            lock (_gate)
            {
                Node root = GetRootNodeCore(rootType, rootId);
                return root == null ? null : MaterializeNodeCore(root);
            }
        }

        public IEnumerable<T> Query<T>() where T : class
        {
            List<Node> snapshot;
            lock (_gate)
                snapshot = _byType.TryGetValue(typeof(T), out List<Node> nodes) ? new List<Node>(nodes) : new List<Node>();

            foreach (Node node in snapshot)
            {
                T clone;
                lock (_gate) clone = (T)MaterializeNodeCore(node);
                yield return clone;
            }
        }


        public IEnumerable<TResult> ProjectScalarShells<T, TResult>(Func<T, TResult> selector) where T : class
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            List<Node> snapshot;
            lock (_gate)
                snapshot = _byType.TryGetValue(typeof(T), out List<Node> nodes) ? new List<Node>(nodes) : new List<Node>();

            foreach (Node node in snapshot)
            {
                Node source;
                lock (_gate)
                {
                    source = node.IsAlias ? GetNodeCore(node.Identity.RootType, node.Identity.RootId, node.CanonicalNodeId) : node;
                    if (source == null) continue;
                    _scalarShellProjections++;
                }
                yield return selector((T)source.ScalarSnapshot);
            }
        }


        public IEnumerable<long> QueryRootIdsByScalarDescendant<T>(Func<T, bool> predicate, bool requireLastInOwner = false) where T : class
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            lock (_gate)
            {
                if (!_byType.TryGetValue(typeof(T), out List<Node> nodes)) return Array.Empty<long>();
                Dictionary<OwnerSlotKey, int> lastOrdinals = requireLastInOwner ? BuildLastOrdinalMap(nodes) : null;
                var roots = new HashSet<long>();
                foreach (Node node in nodes)
                {
                    Node source = ResolveCanonical(node);
                    if (source == null) continue;
                    _nativePredicateEvaluations++;
                    if (requireLastInOwner && !IsLast(source, lastOrdinals)) continue;
                    if (predicate((T)source.ScalarSnapshot)) roots.Add(source.Identity.RootId);
                }
                _nativeRootCandidates += roots.Count;
                return roots.OrderBy(x => x).ToArray();
            }
        }

        /// <summary>
        /// Evaluates an anchor predicate and a descendant predicate while preserving the
        /// exact ancestor binding between them. This prevents cross-branch false positives:
        /// the matching descendant must live below the same matching anchor coordinate.
        /// No CLR graph is reconstructed.
        /// </summary>
        public IEnumerable<long> QueryRootIdsByCorrelatedDescendants<TAnchor, TDescendant>(
            Func<TAnchor, bool> anchorPredicate,
            Func<TDescendant, bool> descendantPredicate,
            bool requireLastAnchorInOwner = false)
            where TAnchor : class
            where TDescendant : class
        {
            if (anchorPredicate == null) throw new ArgumentNullException(nameof(anchorPredicate));
            if (descendantPredicate == null) throw new ArgumentNullException(nameof(descendantPredicate));

            lock (_gate)
            {
                if (!_byType.TryGetValue(typeof(TAnchor), out List<Node> anchorNodes) ||
                    !_byType.TryGetValue(typeof(TDescendant), out List<Node> descendantNodes))
                    return Array.Empty<long>();

                Dictionary<OwnerSlotKey, int> lastOrdinals = requireLastAnchorInOwner ? BuildLastOrdinalMap(anchorNodes) : null;
                var anchorsByRoot = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
                foreach (Node node in anchorNodes)
                {
                    Node anchor = ResolveCanonical(node);
                    if (anchor == null) continue;
                    _nativePredicateEvaluations++;
                    if (requireLastAnchorInOwner && !IsLast(anchor, lastOrdinals)) continue;
                    if (!anchorPredicate((TAnchor)anchor.ScalarSnapshot)) continue;
                    string key = RootKey(anchor.Identity.RootType, anchor.Identity.RootId);
                    if (!anchorsByRoot.TryGetValue(key, out HashSet<long> ids)) anchorsByRoot[key] = ids = new HashSet<long>();
                    ids.Add(anchor.Identity.NodeId);
                }

                var roots = new HashSet<long>();
                foreach (Node node in descendantNodes)
                {
                    Node descendant = ResolveCanonical(node);
                    if (descendant == null) continue;
                    string rootKey = RootKey(descendant.Identity.RootType, descendant.Identity.RootId);
                    if (!anchorsByRoot.TryGetValue(rootKey, out HashSet<long> anchors)) continue;
                    _nativePredicateEvaluations++;
                    if (!descendantPredicate((TDescendant)descendant.ScalarSnapshot)) continue;
                    _correlatedBindingChecks++;
                    if (HasAncestor(descendant, anchors)) roots.Add(descendant.Identity.RootId);
                }

                _nativeRootCandidates += roots.Count;
                return roots.OrderBy(x => x).ToArray();
            }
        }

        private readonly struct OwnerSlotKey : IEquatable<OwnerSlotKey>
        {
            private readonly Type _rootType;
            private readonly long _rootId;
            private readonly long _parent;
            private readonly string _property;
            private readonly GraphSlotKind _kind;
            public OwnerSlotKey(GraphIdentity id)
            {
                _rootType = id.RootType; _rootId = id.RootId; _parent = id.ParentNodeId;
                _property = id.PropertyName; _kind = id.SlotKind;
            }
            public bool Equals(OwnerSlotKey other) => _rootType == other._rootType && _rootId == other._rootId &&
                _parent == other._parent && _kind == other._kind && string.Equals(_property, other._property, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is OwnerSlotKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { int h = _rootType == null ? 0 : _rootType.GetHashCode(); h = h * 397 ^ _rootId.GetHashCode(); h = h * 397 ^ _parent.GetHashCode(); h = h * 397 ^ (int)_kind; h = h * 397 ^ (_property == null ? 0 : StringComparer.Ordinal.GetHashCode(_property)); return h; }
            }
        }

        private Dictionary<OwnerSlotKey, int> BuildLastOrdinalMap(IEnumerable<Node> nodes)
        {
            var result = new Dictionary<OwnerSlotKey, int>();
            foreach (Node node in nodes)
            {
                Node source = ResolveCanonical(node);
                if (source == null || source.Identity.ParentNodeId == 0) continue;
                var key = new OwnerSlotKey(source.Identity);
                if (!result.TryGetValue(key, out int current) || source.Identity.Ordinal > current) result[key] = source.Identity.Ordinal;
            }
            return result;
        }

        private static bool IsLast(Node source, Dictionary<OwnerSlotKey, int> lastOrdinals)
        {
            if (source.Identity.ParentNodeId == 0) return true;
            return lastOrdinals != null && lastOrdinals.TryGetValue(new OwnerSlotKey(source.Identity), out int max) && source.Identity.Ordinal == max;
        }

        private Node ResolveCanonical(Node node) => node.IsAlias ? GetNodeCore(node.Identity.RootType, node.Identity.RootId, node.CanonicalNodeId) : node;

        private bool HasAncestor(Node descendant, HashSet<long> candidateAncestors)
        {
            long parentId = descendant.Identity.ParentNodeId;
            while (parentId != 0)
            {
                _ancestorCoordinateWalks++;
                if (candidateAncestors.Contains(parentId)) return true;
                Node parent = GetNodeCore(descendant.Identity.RootType, descendant.Identity.RootId, parentId);
                if (parent == null) return false;
                parentId = parent.Identity.ParentNodeId;
            }
            return false;
        }

        public TRoot Root<TRoot>(object descendant) where TRoot : class, new()
        {
            if (!TryIdentity(descendant, out GraphIdentity id) || id.RootType != typeof(TRoot)) return null;
            lock (_gate)
            {
                Node root = GetRootNodeCore(id.RootType, id.RootId);
                if (root == null) return null;
                _rootResolutions++;
                return (TRoot)MaterializeNodeCore(root);
            }
        }

        public TParent Parent<TParent>(object descendant) where TParent : class
        {
            if (!TryIdentity(descendant, out GraphIdentity id) || id.ParentNodeId == 0) return null;
            lock (_gate)
            {
                Node parent = GetNodeCore(id.RootType, id.RootId, id.ParentNodeId);
                if (parent == null || !typeof(TParent).IsAssignableFrom(parent.Identity.NodeType)) return null;
                _parentResolutions++;
                return (TParent)MaterializeNodeCore(parent);
            }
        }

        /// <summary>
        /// Directly updates the materialized descendant coordinate. The aggregate root is
        /// neither loaded nor rewritten. Scalar state is replaced at the node and the
        /// descendant's owned sub-domain is rebuilt only when that descendant itself
        /// contains relationships.
        /// </summary>
        public bool TrySaveCoordinate(object materialized, out Type rootType, out long rootId, out long nodeId)
        {
            rootType = null;
            rootId = 0;
            nodeId = 0;
            if (!TryIdentity(materialized, out GraphIdentity identity)) return false;

            lock (_gate)
            {
                Node node = GetNodeCore(identity.RootType, identity.RootId, identity.NodeId);
                if (node == null) return false;

                rootType = identity.RootType;
                rootId = identity.RootId;
                nodeId = identity.NodeId;

                node.ScalarSnapshot = GraphObject.CloneScalars(materialized);
                if (!RelationshipsMatchCore(node, materialized))
                {
                    ReplaceOwnedChildrenCore(node, materialized);
                    _relationshipRebuilds++;
                }
                _directWrites++;
                if (identity.ParentNodeId != 0) _rootRewritesAvoided++;
                return true;
            }
        }

        public GraphStatistics GetStatistics()
        {
            lock (_gate)
            {
                long nodes = _byRoot.Values.Sum(x => (long)x.Count);
                return new GraphStatistics
                {
                    Roots = _byRoot.Count,
                    Coordinates = nodes,
                    DirectDescendantWrites = _directWrites,
                    RootRewritesAvoided = _rootRewritesAvoided,
                    CoordinateMaterializations = _materializations,
                    ParentResolutions = _parentResolutions,
                    RootResolutions = _rootResolutions,
                    CascadeRootDeletes = _cascadeDeletes,
                    ScalarSnapshots = nodes,
                    RelationshipSlots = _byRoot.Values.Sum(list => list.LongCount(n => n.Identity.ParentNodeId != 0)),
                    RelationshipDomainRebuilds = _relationshipRebuilds,
                    CoordinateDerivations = _coordinateDerivations,
                    CoordinateCollisionRepairs = _collisionRepairs,
                    InverseCoordinateValidations = _inverseValidations,
                    InlineIdentityRecords = nodes,
                    CandidateLawDerivedRelationships = Math.Max(0, _byRoot.Values.Sum(list => list.LongCount(n => n.Identity.ParentNodeId != 0)) - _relationshipRebuilds),
                    PhysicalRelationshipExceptions = _relationshipRebuilds,
                    ScalarShellProjections = _scalarShellProjections,
                    NativeDescendantPredicateEvaluations = _nativePredicateEvaluations,
                    NativeRootCandidates = _nativeRootCandidates,
                    NativeQueryMaterializations = 0,
                    CorrelatedBindingChecks = _correlatedBindingChecks,
                    AncestorCoordinateWalks = _ancestorCoordinateWalks
                };
            }
        }

        private bool RelationshipsMatchCore(Node owner, object materialized)
        {
            if (!_byRoot.TryGetValue(RootKey(owner.Identity.RootType, owner.Identity.RootId), out List<Node> all)) return false;
            List<Node> expected = all.Where(n => n.Identity.ParentNodeId == owner.Identity.NodeId).OrderBy(n => n.Identity.PropertyName).ThenBy(n => n.Identity.Ordinal).ToList();
            var actual = new List<Tuple<string, int, object, long>>();
            foreach (PropertyInfo property in GraphShape.For(materialized.GetType()).RelationshipProperties)
            {
                object child = property.GetValue(materialized, null);
                if (child == null) continue;
                if (child is IDictionary dictionary)
                {
                    int index = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Value != null && !GraphObject.IsScalar(entry.Value.GetType()))
                        {
                            if (!TryIdentity(entry.Value, out GraphIdentity id)) return false;
                            actual.Add(Tuple.Create(property.Name, index, entry.Key, id.NodeId));
                        }
                        index++;
                    }
                }
                else if (child is IEnumerable enumerable && !(child is string) && !(child is byte[]))
                {
                    int index = 0;
                    foreach (object item in enumerable)
                    {
                        if (item != null && !GraphObject.IsScalar(item.GetType()))
                        {
                            if (!TryIdentity(item, out GraphIdentity id)) return false;
                            actual.Add(Tuple.Create(property.Name, index, (object)null, id.NodeId));
                        }
                        index++;
                    }
                }
                else if (!GraphObject.IsScalar(child.GetType()))
                {
                    if (!TryIdentity(child, out GraphIdentity id)) return false;
                    actual.Add(Tuple.Create(property.Name, -1, (object)null, id.NodeId));
                }
            }
            actual = actual.OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
            if (expected.Count != actual.Count) return false;
            for (int i = 0; i < expected.Count; i++)
            {
                Node e = expected[i];
                Tuple<string, int, object, long> a = actual[i];
                long expectedNodeId = e.IsAlias ? e.CanonicalNodeId : e.Identity.NodeId;
                if (!string.Equals(e.Identity.PropertyName, a.Item1, StringComparison.Ordinal) ||
                    e.Identity.Ordinal != a.Item2 || !object.Equals(e.Identity.DictionaryKey, a.Item3) ||
                    expectedNodeId != a.Item4) return false;
            }
            return true;
        }

        private void ReplaceOwnedChildrenCore(Node owner, object materialized)
        {
            if (!_byRoot.TryGetValue(RootKey(owner.Identity.RootType, owner.Identity.RootId), out List<Node> all)) return;
            List<Node> descendants = CollectDescendants(all, owner.Identity.NodeId);
            foreach (Node child in descendants)
            {
                all.Remove(child);
                if (!child.IsAlias && _byType.TryGetValue(child.Identity.NodeType, out List<Node> typed))
                {
                    typed.Remove(child);
                    if (typed.Count == 0) _byType.Remove(child.Identity.NodeType);
                }
            }

            var additions = new List<Node>();
            var seen = new Dictionary<object, long>(ReferenceComparer.Instance) { [materialized] = owner.Identity.NodeId };
            WalkChildren(materialized, owner, additions, seen);
            foreach (Node child in additions)
            {
                all.Add(child);
                if (child.IsAlias) continue;
                if (!_byType.TryGetValue(child.Identity.NodeType, out List<Node> typed))
                    _byType[child.Identity.NodeType] = typed = new List<Node>();
                typed.Add(child);
            }
        }

        private static List<Node> CollectDescendants(List<Node> all, long parentId)
        {
            var result = new List<Node>();
            var queue = new Queue<long>();
            queue.Enqueue(parentId);
            while (queue.Count != 0)
            {
                long current = queue.Dequeue();
                foreach (Node node in all.Where(n => n.Identity.ParentNodeId == current).ToArray())
                {
                    result.Add(node);
                    queue.Enqueue(node.Identity.NodeId);
                }
            }
            return result;
        }

        private object MaterializeNodeCore(Node node)
        {
            Node source = node.IsAlias ? GetNodeCore(node.Identity.RootType, node.Identity.RootId, node.CanonicalNodeId) : node;
            if (source == null) throw new InvalidOperationException("The canonical descendant coordinate is unavailable.");
            object instance = GraphObject.Clone(source.ScalarSnapshot, new Dictionary<object, object>(ReferenceComparer.Instance));
            RegisterIdentity(instance, source.Identity);
            _materializations++;

            if (!_byRoot.TryGetValue(RootKey(node.Identity.RootType, node.Identity.RootId), out List<Node> all)) return instance;
            Node[] directChildren = all.Where(n => n.Identity.ParentNodeId == source.Identity.NodeId).ToArray();
            foreach (IGrouping<string, Node> group in directChildren.Where(n => n.Identity.SlotKind == GraphSlotKind.SequenceElement).GroupBy(n => n.Identity.PropertyName))
            {
                PropertyInfo property = instance.GetType().GetProperty(group.Key, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.PropertyType.IsArray)
                {
                    int length = group.Max(n => n.Identity.Ordinal) + 1;
                    property.SetValue(instance, Array.CreateInstance(property.PropertyType.GetElementType(), length), null);
                }
            }
            foreach (Node child in directChildren.OrderBy(n => n.Identity.Ordinal))
            {
                object childValue = MaterializeNodeCore(child);
                AttachChild(instance, child.Identity, childValue);
            }
            return instance;
        }

        private static void AttachChild(object parent, GraphIdentity child, object value)
        {
            // "$self" identifies an element owned directly by a collection
            // node rather than by a reflected property on a POCO.
            if (string.Equals(child.PropertyName, "$self", StringComparison.Ordinal))
            {
                if (child.SlotKind == GraphSlotKind.SequenceElement)
                {
                    if (parent is Array directArray)
                    {
                        if (child.Ordinal < directArray.Length) directArray.SetValue(value, child.Ordinal);
                        return;
                    }
                    if (parent is IList directList)
                    {
                        while (directList.Count <= child.Ordinal) directList.Add(null);
                        directList[child.Ordinal] = value;
                        return;
                    }
                }
                if (child.SlotKind == GraphSlotKind.DictionaryValue && parent is IDictionary directDictionary)
                {
                    directDictionary[child.DictionaryKey] = value;
                    return;
                }
                throw new InvalidOperationException("The nested graph collection is no longer writable.");
            }

            PropertyInfo property = parent.GetType().GetProperty(child.PropertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new InvalidOperationException("Graph property no longer exists: " + child.PropertyName);
            object slot = property.GetValue(parent, null);

            if (child.SlotKind == GraphSlotKind.Property)
            {
                property.SetValue(parent, value, null);
                return;
            }

            if (child.SlotKind == GraphSlotKind.SequenceElement)
            {
                if (slot is Array array)
                {
                    if (child.Ordinal < array.Length) array.SetValue(value, child.Ordinal);
                    return;
                }
                if (slot is IList list)
                {
                    while (list.Count <= child.Ordinal) list.Add(null);
                    list[child.Ordinal] = value;
                    return;
                }
                if (slot != null)
                {
                    MethodInfo add = slot.GetType().GetMethod("Add", new[] { value.GetType() });
                    if (add == null) add = slot.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
                    if (add != null) { add.Invoke(slot, new[] { value }); return; }
                }
                throw new InvalidOperationException("The graph sequence is no longer indexable.");
            }

            if (child.SlotKind == GraphSlotKind.DictionaryValue)
            {
                if (!(slot is IDictionary dictionary)) throw new InvalidOperationException("The graph dictionary is no longer available.");
                dictionary[child.DictionaryKey] = value;
            }
        }

        private long Walk(object value, Type rootType, long rootId, long parentNodeId, string propertyName, int ordinal, object dictionaryKey, GraphSlotKind slotKind, List<Node> nodes, Dictionary<object, long> seen)
        {
            if (value == null || GraphObject.IsScalar(value.GetType())) return 0;
            if (seen.TryGetValue(value, out long existing))
            {
                Node canonical = nodes.First(n => n.Identity.NodeId == existing);
                ulong aliasToken = DescendantCoordinateMill.Token(propertyName, slotKind, ordinal, dictionaryKey);
                int aliasSalt = 0;
                long aliasId = DeriveUniqueCoordinate(parentNodeId, aliasToken, nodes, ref aliasSalt);
                var aliasIdentity = new GraphIdentity(
                    rootType, rootId, value.GetType(), aliasId,
                    parentNodeId, propertyName, ordinal,
                    GraphObject.Clone(dictionaryKey, new Dictionary<object, object>(ReferenceComparer.Instance)),
                    slotKind, aliasToken, aliasSalt);
                nodes.Add(new Node { Identity = aliasIdentity, IsAlias = true, CanonicalNodeId = existing });
                return existing;
            }

            ulong transitionToken = slotKind == GraphSlotKind.Root
                ? DescendantCoordinateMill.Token(rootType.AssemblyQualifiedName, slotKind, -1, rootId)
                : DescendantCoordinateMill.Token(propertyName, slotKind, ordinal, dictionaryKey);
            int coordinateSalt = 0;
            long nodeId = slotKind == GraphSlotKind.Root
                ? DescendantCoordinateMill.Root(rootType, rootId)
                : DeriveUniqueCoordinate(parentNodeId, transitionToken, nodes, ref coordinateSalt);
            seen[value] = nodeId;
            var identity = new GraphIdentity(
                rootType, rootId, value.GetType(), nodeId,
                parentNodeId, propertyName, ordinal,
                GraphObject.Clone(dictionaryKey, new Dictionary<object, object>(ReferenceComparer.Instance)),
                slotKind, transitionToken, coordinateSalt);
            var node = new Node { Identity = identity, ScalarSnapshot = GraphObject.CloneScalars(value), CanonicalNodeId = nodeId };
            nodes.Add(node);
            WalkChildren(value, node, nodes, seen);
            return nodeId;
        }

        private void WalkChildren(object value, Node owner, List<Node> nodes, Dictionary<object, long> seen)
        {
            // Collection containers may themselves be graph nodes when they are
            // nested inside another relationship (List<List<T>>, dictionary of
            // lists, dictionary of dictionaries, and similar shapes). Walk the
            // container's contents directly instead of reflecting over List<T>
            // or Dictionary<TKey,TValue> implementation properties.
            if (value is IDictionary nestedDictionary)
            {
                int index = 0;
                foreach (DictionaryEntry entry in nestedDictionary)
                {
                    if (entry.Value != null && !GraphObject.IsScalar(entry.Value.GetType()))
                        Walk(entry.Value, owner.Identity.RootType, owner.Identity.RootId, owner.Identity.NodeId,
                            "$self", index, entry.Key, GraphSlotKind.DictionaryValue, nodes, seen);
                    index++;
                }
                return;
            }
            if (value is IEnumerable nestedSequence && !(value is string) && !(value is byte[]))
            {
                int index = 0;
                foreach (object item in nestedSequence)
                {
                    if (item != null && !GraphObject.IsScalar(item.GetType()))
                        Walk(item, owner.Identity.RootType, owner.Identity.RootId, owner.Identity.NodeId,
                            "$self", index, null, GraphSlotKind.SequenceElement, nodes, seen);
                    index++;
                }
                return;
            }

            foreach (PropertyInfo property in GraphShape.For(value.GetType()).RelationshipProperties)
            {
                object child = property.GetValue(value, null);
                if (child == null) continue;

                if (child is IDictionary dictionary)
                {
                    int index = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Value != null && !GraphObject.IsScalar(entry.Value.GetType()))
                            Walk(entry.Value, owner.Identity.RootType, owner.Identity.RootId, owner.Identity.NodeId, property.Name, index, entry.Key, GraphSlotKind.DictionaryValue, nodes, seen);
                        index++;
                    }
                }
                else if (child is IEnumerable enumerable && !(child is string) && !(child is byte[]))
                {
                    int index = 0;
                    foreach (object item in enumerable)
                    {
                        if (item != null && !GraphObject.IsScalar(item.GetType()))
                            Walk(item, owner.Identity.RootType, owner.Identity.RootId, owner.Identity.NodeId, property.Name, index, null, GraphSlotKind.SequenceElement, nodes, seen);
                        index++;
                    }
                }
                else if (!GraphObject.IsScalar(child.GetType()))
                {
                    Walk(child, owner.Identity.RootType, owner.Identity.RootId, owner.Identity.NodeId, property.Name, -1, null, GraphSlotKind.Property, nodes, seen);
                }
            }
        }

        private long DeriveUniqueCoordinate(long parentCoordinate, ulong token, List<Node> nodes, ref int salt)
        {
            while (true)
            {
                long coordinate = DescendantCoordinateMill.Descend(parentCoordinate, token, salt);
                _coordinateDerivations++;
                if (!nodes.Any(n => n.Identity.NodeId == coordinate))
                {
                    if (DescendantCoordinateMill.Ascend(coordinate, token, salt) != parentCoordinate)
                        throw new InvalidOperationException("Descendant coordinate mill failed inverse validation.");
                    _inverseValidations++;
                    return coordinate;
                }
                salt++;
                _collisionRepairs++;
            }
        }

        private bool TryIdentity(object value, out GraphIdentity identity)
        {
            identity = default;
            if (value == null || !_returned.TryGetValue(value, out MaterializedGraphIdentity box)) return false;
            identity = box.Value;
            return true;
        }

        private void RegisterIdentity(object value, GraphIdentity identity)
        {
            if (value == null) return;
            try { _returned.Add(value, new MaterializedGraphIdentity(identity)); }
            catch (ArgumentException) { }
        }

        private Node GetRootNodeCore(Type type, long id)
            => _byRoot.TryGetValue(RootKey(type, id), out List<Node> nodes) ? nodes.FirstOrDefault(x => x.Identity.ParentNodeId == 0) : null;

        private Node GetNodeCore(Type type, long id, long nodeId)
            => _byRoot.TryGetValue(RootKey(type, id), out List<Node> nodes) ? nodes.FirstOrDefault(x => x.Identity.NodeId == nodeId) : null;

        private void RemoveRootCore(Type rootType, long rootId, bool countCascade)
        {
            string key = RootKey(rootType, rootId);
            if (!_byRoot.TryGetValue(key, out List<Node> nodes)) return;
            foreach (Node node in nodes)
            {
                if (!node.IsAlias && _byType.TryGetValue(node.Identity.NodeType, out List<Node> typed))
                {
                    typed.Remove(node);
                    if (typed.Count == 0) _byType.Remove(node.Identity.NodeType);
                }
            }
            _byRoot.Remove(key);
            if (countCascade) _cascadeDeletes++;
        }

        private static string RootKey(Type type, long id) => type.AssemblyQualifiedName + "#" + id;

        internal sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    internal sealed class GraphShape
    {
        private static readonly ConcurrentDictionary<Type, GraphShape> Cache = new ConcurrentDictionary<Type, GraphShape>();
        public PropertyInfo[] Properties { get; private set; }
        public PropertyInfo[] ScalarProperties { get; private set; }
        public PropertyInfo[] RelationshipProperties { get; private set; }
        public bool HasRelationships => RelationshipProperties.Length != 0;
        public static GraphShape For(Type type) => Cache.GetOrAdd(type, Create);

        private static GraphShape Create(Type type)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(x => x.CanRead && x.CanWrite && x.GetIndexParameters().Length == 0).ToArray();
            return new GraphShape
            {
                Properties = properties,
                ScalarProperties = properties.Where(x => !GraphObject.IsRelationshipType(x.PropertyType)).ToArray(),
                RelationshipProperties = properties.Where(x => GraphObject.IsRelationshipType(x.PropertyType)).ToArray()
            };
        }
    }

    internal static class GraphObject
    {
        public static bool IsScalar(Type type)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;
            return t.IsEnum || t.IsPrimitive || t == typeof(string) || t == typeof(decimal) ||
                   t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) ||
                   t == typeof(Guid) || t == typeof(byte[]) || t == typeof(Uri);
        }

        public static PropertyInfo[] Properties(Type type) => GraphShape.For(type).Properties;

        public static bool IsRelationshipType(Type type)
        {
            if (IsScalar(type)) return false;
            if (type.IsArray) return !IsScalar(type.GetElementType());
            Type dictionary = type.GetInterfaces().Concat(new[] { type }).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionary != null) return !IsScalar(dictionary.GetGenericArguments()[1]);
            Type enumerable = type.GetInterfaces().Concat(new[] { type }).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerable != null) return !IsScalar(enumerable.GetGenericArguments()[0]);
            return type.IsClass;
        }

        public static object CloneScalars(object source)
        {
            if (source == null) return null;
            Type type = source.GetType();

            // A collection can itself be an owned graph node (for example a
            // Dictionary<string,List<T>> value). Its scalar shell is the empty
            // collection container; its elements are independent descendant
            // coordinates and are attached during materialization.
            if (type.IsArray)
                return Array.CreateInstance(type.GetElementType(), ((Array)source).Length);
            if (source is IDictionary)
                return CreateCollection(type, typeof(Dictionary<,>));
            if (source is IList)
                return CreateCollection(type, typeof(List<>));

            object clone = Activator.CreateInstance(type);
            foreach (PropertyInfo property in GraphShape.For(type).ScalarProperties)
                property.SetValue(clone, Clone(property.GetValue(source, null), new Dictionary<object, object>(OwnedGraphRegistry.ReferenceComparer.Instance)), null);
            foreach (PropertyInfo property in GraphShape.For(type).RelationshipProperties)
                property.SetValue(clone, CreateEmptyRelationship(property.PropertyType), null);
            return clone;
        }

        private static object CreateEmptyRelationship(Type type)
        {
            if (type.IsArray) return Array.CreateInstance(type.GetElementType(), 0);
            if (typeof(IDictionary).IsAssignableFrom(type)) return CreateCollection(type, typeof(Dictionary<,>));
            if (typeof(IList).IsAssignableFrom(type)) return CreateCollection(type, typeof(List<>));
            if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISet<>)))
                return CreateCollection(type, typeof(HashSet<>));
            return null;
        }

        public static object Clone(object source, Dictionary<object, object> seen)
        {
            if (source == null) return null;
            Type type = source.GetType();
            if (IsScalar(type)) { if (source is byte[] bytes) return bytes.ToArray(); return source; }
            if (seen.TryGetValue(source, out object prior)) return prior;
            if (type.IsArray)
            {
                Array sourceArray = (Array)source;
                Array destination = Array.CreateInstance(type.GetElementType(), sourceArray.Length);
                seen[source] = destination;
                for (int i = 0; i < sourceArray.Length; i++) destination.SetValue(Clone(sourceArray.GetValue(i), seen), i);
                return destination;
            }
            if (source is IDictionary sourceDictionary)
            {
                IDictionary destination = (IDictionary)CreateCollection(type, typeof(Dictionary<,>));
                seen[source] = destination;
                foreach (DictionaryEntry entry in sourceDictionary) destination.Add(Clone(entry.Key, seen), Clone(entry.Value, seen));
                return destination;
            }
            if (source is IList sourceList)
            {
                IList destination = (IList)CreateCollection(type, typeof(List<>));
                seen[source] = destination;
                foreach (object item in sourceList) destination.Add(Clone(item, seen));
                return destination;
            }
            if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISet<>)))
            {
                object destination = CreateCollection(type, typeof(HashSet<>));
                seen[source] = destination;
                MethodInfo add = destination.GetType().GetMethod("Add");
                foreach (object item in (IEnumerable)source) add.Invoke(destination, new[] { Clone(item, seen) });
                return destination;
            }
            object clone = Activator.CreateInstance(type);
            seen[source] = clone;
            foreach (PropertyInfo property in Properties(type)) property.SetValue(clone, Clone(property.GetValue(source, null), seen), null);
            return clone;
        }

        private static object CreateCollection(Type actual, Type fallbackOpen)
        {
            if (!actual.IsInterface && !actual.IsAbstract && actual.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(actual);
            Type[] args = actual.IsGenericType ? actual.GetGenericArguments() : Type.EmptyTypes;
            if (args.Length == fallbackOpen.GetGenericArguments().Length)
                return Activator.CreateInstance(fallbackOpen.MakeGenericType(args));
            throw new InvalidOperationException("Unsupported collection type: " + actual.FullName);
        }
    }
}
