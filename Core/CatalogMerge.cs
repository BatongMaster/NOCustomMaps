using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// Append-only, idempotent merge into the game's two map arrays
    /// (<c>MapLoader.Maps</c> and <c>MapSettingsManager.Maps</c>).
    ///
    /// Modelled over a key selector rather than the game types so that the code the
    /// registrar actually calls is the code the test suite exercises — there is no
    /// second implementation to drift.
    ///
    /// Two properties this has to guarantee, both of which are real bugs if broken:
    ///
    /// <list type="number">
    /// <item><b>Append only; never prepend, never reorder.</b>
    /// <c>MissionEditorNewMenu(V2).Awake</c> walks a <em>serialized, fixed-size</em>
    /// button array by index — <c>for (i = 0; i &lt; mapButtons.Length; i++) map = mapLoader.Maps[i]</c>
    /// — so moving index 0 or 1 silently re-points the two vanilla buttons at the
    /// wrong map. The failure is a mislabelled button, not an exception.</item>
    ///
    /// <item><b>Idempotent by key, not by reference.</b> The registrar runs from a
    /// prefix on every consumer of the arrays rather than from a lifecycle hook, so
    /// it is called many times per session and must converge. Keying on identity
    /// would double-append after a scene reload handed us a fresh
    /// <c>MapDetails</c> instance for the same map.</item>
    /// </list>
    /// </summary>
    public static class CatalogMerge
    {
        /// <summary>
        /// Returns <paramref name="existing"/> with every candidate whose key is not
        /// already present appended in candidate order.
        ///
        /// Returns the <em>same array instance</em> when there is nothing to add, so a
        /// caller can cheaply tell a no-op from a real change and only log the latter.
        /// </summary>
        /// <param name="existing">The game's current array. Null is treated as empty.</param>
        /// <param name="candidates">Our maps, in a stable order. Null is treated as empty.</param>
        /// <param name="keyOf">Extracts the identity key (a PrefabName). May return null
        /// for an entry that should be skipped.</param>
        public static T[] Append<T>(T[] existing, IReadOnlyList<T> candidates, Func<T, string> keyOf)
        {
            if (keyOf == null) throw new ArgumentNullException(nameof(keyOf));

            T[] current = existing ?? Array.Empty<T>();
            if (candidates == null || candidates.Count == 0) return current;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < current.Length; i++)
            {
                string k = SafeKey(keyOf, current[i]);
                if (k != null) seen.Add(k);
            }

            List<T> additions = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                T candidate = candidates[i];
                string k = SafeKey(keyOf, candidate);

                // A null key means the entry is unusable (no MapDetails, no MapSettings).
                // Skipping is correct: the caller has already logged why, and appending an
                // entry the game will dereference is worse than omitting it.
                if (k == null) continue;

                // Adding to `seen` here is what makes two bundles claiming the same
                // PrefabName resolve deterministically — first in candidate order wins,
                // and the loser is not appended twice.
                if (!seen.Add(k)) continue;

                (additions ??= new List<T>()).Add(candidate);
            }

            if (additions == null) return current;

            var merged = new T[current.Length + additions.Count];
            Array.Copy(current, merged, current.Length);
            for (int i = 0; i < additions.Count; i++) merged[current.Length + i] = additions[i];
            return merged;
        }

        /// <summary>Keys already present in <paramref name="existing"/>, for logging.</summary>
        public static List<string> KeysOf<T>(T[] existing, Func<T, string> keyOf)
        {
            if (keyOf == null) throw new ArgumentNullException(nameof(keyOf));

            var keys = new List<string>();
            if (existing == null) return keys;

            for (int i = 0; i < existing.Length; i++)
            {
                string k = SafeKey(keyOf, existing[i]);
                keys.Add(k ?? "<null>");
            }
            return keys;
        }

        // The game arrays can legitimately contain nulls (an unassigned slot in the
        // scene), and a key selector that dereferences MapDetails will throw on one.
        // A merge that throws here would take out CanLoad, which is on the join path.
        static string SafeKey<T>(Func<T, string> keyOf, T item)
        {
            if (item == null) return null;
            try
            {
                string k = keyOf(item);
                return string.IsNullOrEmpty(k) ? null : k;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }
    }
}
