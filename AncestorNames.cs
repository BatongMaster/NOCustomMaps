using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CustomMaps
{
    /// <summary>
    /// Which of a few names a node or any of its ancestors carries, with each node's name read at most
    /// once.
    ///
    /// The map's passes tell a renderer or a collider by the subtree it sits in (under the bridges, the
    /// tunnels, the lakes, the roads), and they used to ask by walking its parents' names once per
    /// question. In Unity every read of <c>Object.name</c> crosses into the engine and allocates a new
    /// string, so <c>BorrowMaterials</c>, asking four questions of each of Swiss Alps 0.4.0's 12,542
    /// renderers and reading each slot's material name up to eight times, read 303,554 names where
    /// 14,234 now do (measured in the editor, 2026-10-04: some 67 ms down to 19). Here a node's answer
    /// is its parent's answer plus its own name, and every answer is kept, so each transform is named
    /// once however many renderers sit under it and whatever is asked of it.
    ///
    /// Free of game and Unity types so the tests can drive it; the plugin hands it
    /// <c>Transform.parent</c> and <c>Transform.name</c> (<c>MapFixups.Sections</c>). Meant for one pass
    /// over a hierarchy that does not change under it: a node renamed or moved afterwards keeps the
    /// answer it was first given.
    /// </summary>
    internal sealed class AncestorNames<T> where T : class
    {
        readonly Func<T, T> _parent;
        readonly Func<T, string> _name;
        readonly string[] _names;
        readonly Dictionary<T, int> _masks = new Dictionary<T, int>(ByReference<T>.Instance);
        readonly List<T> _unanswered = new List<T>();

        /// <param name="parent">A node's parent, or null at the root.</param>
        /// <param name="name">A node's name, compared ordinally, as <c>==</c> on strings does.</param>
        /// <param name="names">The names to look for, at most 32: bit <c>i</c> of <see cref="Mask"/>
        /// stands for <c>names[i]</c>.</param>
        public AncestorNames(Func<T, T> parent, Func<T, string> name, params string[] names)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            if (names.Length > 32) throw new ArgumentException("at most 32 names fit the mask", nameof(names));
        }

        /// <summary>How many names have been read so far: once per node ever asked about, or passed
        /// on the way up from one.</summary>
        public int NamesRead { get; private set; }

        /// <summary>
        /// The names the node or any of its ancestors carries, as bits in the order the constructor was
        /// given them; 0 for a null node.
        ///
        /// Walks up only as far as the first node already answered, then answers every node passed on
        /// the way, so the next renderer under the same parent costs one lookup.
        /// </summary>
        public int Mask(T node)
        {
            if (node == null) return 0;
            if (_masks.TryGetValue(node, out int known)) return known;

            _unanswered.Clear();
            int above = 0;
            for (T at = node; at != null; at = _parent(at))
            {
                if (_masks.TryGetValue(at, out above)) break;
                _unanswered.Add(at);
            }

            // Root-most first, so each node's answer is its parent's plus its own name.
            for (int i = _unanswered.Count - 1; i >= 0; i--)
            {
                T at = _unanswered[i];
                above |= Own(_name(at));
                _masks[at] = above;
            }

            return above;
        }

        /// <summary>True if the node, or any of its ancestors, carries <paramref name="name"/>, which
        /// must be one of the names the constructor was given.</summary>
        public bool Under(T node, string name) => (Mask(node) & Bit(name)) != 0;

        /// <summary>The bit standing for one of the constructor's names.</summary>
        public int Bit(string name)
        {
            for (int i = 0; i < _names.Length; i++)
                if (string.Equals(_names[i], name, StringComparison.Ordinal)) return 1 << i;

            throw new ArgumentException($"'{name}' is not one of the names looked for", nameof(name));
        }

        int Own(string name)
        {
            NamesRead++;

            int mask = 0;
            for (int i = 0; i < _names.Length; i++)
                if (string.Equals(_names[i], name, StringComparison.Ordinal)) mask |= 1 << i;

            return mask;
        }
    }

    /// <summary>
    /// Keys compared by reference, for caches keyed by Unity objects within one pass.
    ///
    /// <c>UnityEngine.Object</c> overrides <c>Equals</c> to ask the engine whether the native object is
    /// still alive; within a pass over objects that are, the reference is the identity, and comparing
    /// it costs nothing.
    /// </summary>
    internal sealed class ByReference<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ByReference<T> Instance = new ByReference<T>();

        ByReference() { }

        public bool Equals(T x, T y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
