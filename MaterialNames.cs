using System;

namespace CustomMaps
{
    /// <summary>
    /// What a loaded material's name says about where it came from, for the fallbacks that pick a
    /// material out of everything loaded by its shader alone.
    ///
    /// A custom map is prepared at its own <c>EnableMap</c>, and if another map was played earlier in
    /// the session, that map's instance is still in the scene at that point: the game unloads it just
    /// after. Its renderers may own copies of shared materials, made the first time anything asked for
    /// <c>Renderer.material</c>, which Unity names after the original with <c>" (Instance)"</c> on the
    /// end and which go when that map is destroyed a moment later. A fallback that took one of those
    /// would leave the custom map drawing with a destroyed material, so such copies are passed over;
    /// the original they were copied from is loaded too, and is found instead. Lookups by an exact
    /// name never match a copy and need no such care.
    ///
    /// Kept free of game and Unity types so the tests cover it.
    /// </summary>
    internal static class MaterialNames
    {
        /// <summary>The suffix Unity gives a material copied for one renderer.</summary>
        internal const string InstanceSuffix = " (Instance)";

        /// <summary>True if <paramref name="name"/> is that of a material copied for one renderer,
        /// which lives only as long as the object that owns it.</summary>
        internal static bool IsPerRendererCopy(string name) =>
            name != null && name.EndsWith(InstanceSuffix, StringComparison.Ordinal);
    }
}
