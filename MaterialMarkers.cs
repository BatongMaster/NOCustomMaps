using System;

namespace CustomMaps
{
    /// <summary>What a material slot in a map's bundle asks to be given at load, told from the name of
    /// the placeholder in it (<see cref="MaterialMarkers.Of"/>).</summary>
    internal enum MaterialSlot
    {
        /// <summary>A material of the map's own, left as it is.</summary>
        Own,

        /// <summary>The airfields' paint, <see cref="MaterialMarkers.Markings"/>.</summary>
        Markings,

        /// <summary>The ground: a bare <see cref="MaterialMarkers.Borrow"/>, one naming a surface this
        /// plugin does not know, or an empty slot.</summary>
        Terrain,

        Water,
        Paved,
        Runway,
        Tarmac,
        Structure,
        Concrete,
    }

    /// <summary>
    /// The placeholder names a map's bundle wears where the base game's materials go, and which surface
    /// each asks for.
    ///
    /// <c>MapFixups</c> names and explains each marker (<c>BorrowMarker</c>, <c>WaterMarker</c> and the
    /// rest) and swaps the slots in <c>BorrowMaterials</c>; the names live here, free of game and Unity
    /// types, so the tests can hold the reading of them to what that swap has always done. Each marker
    /// is a prefix, so <c>__BORROW__Paved_bridge</c> is paved; none is a prefix of another, so a name
    /// asks for one surface at most.
    /// </summary>
    internal static class MaterialMarkers
    {
        public const string Borrow = "__BORROW__";
        public const string Water = Borrow + "Water";
        public const string Paved = Borrow + "Paved";
        public const string Runway = Borrow + "Runway";
        public const string Tarmac = Borrow + "Tarmac";
        public const string Structure = Borrow + "Structure";
        public const string Concrete = Borrow + "Concrete";

        /// <summary>Not a <see cref="Borrow"/> name, on purpose: <c>MapFixups.MarkingsMarker</c> says why.</summary>
        public const string Markings = "__MARKINGS__Runway";

        /// <summary>
        /// The surface a slot asks for, from the name of the material in it (null for an empty slot).
        ///
        /// An empty slot and a <see cref="Borrow"/> name that names no surface this plugin knows are both
        /// the ground, since that is all a map has unless it ships more; a name that is neither marker is
        /// the map's own material.
        /// </summary>
        public static MaterialSlot Of(string materialName)
        {
            if (materialName == null) return MaterialSlot.Terrain;
            if (materialName.StartsWith(Markings, StringComparison.Ordinal)) return MaterialSlot.Markings;
            if (!materialName.StartsWith(Borrow, StringComparison.Ordinal)) return MaterialSlot.Own;

            if (materialName.StartsWith(Water, StringComparison.Ordinal)) return MaterialSlot.Water;
            if (materialName.StartsWith(Paved, StringComparison.Ordinal)) return MaterialSlot.Paved;
            if (materialName.StartsWith(Runway, StringComparison.Ordinal)) return MaterialSlot.Runway;
            if (materialName.StartsWith(Tarmac, StringComparison.Ordinal)) return MaterialSlot.Tarmac;
            if (materialName.StartsWith(Structure, StringComparison.Ordinal)) return MaterialSlot.Structure;
            if (materialName.StartsWith(Concrete, StringComparison.Ordinal)) return MaterialSlot.Concrete;

            return MaterialSlot.Terrain;
        }
    }
}
