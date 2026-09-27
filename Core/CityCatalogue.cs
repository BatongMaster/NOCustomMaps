namespace CustomMaps
{
    /// <summary>One borrowable building type.</summary>
    public struct BuildingType
    {
        /// <summary>Name of the donor GameObject inside the shipped map prefab. This is
        /// what the plugin resolves at load; the generator never sees a mesh.</summary>
        public string Name;

        /// <summary>Footprint and above-ground height in metres, measured off the shipped
        /// mesh bounds rather than estimated.</summary>
        public float Width, Depth, Height;

        /// <summary>0 low-rise, 1 commercial. Decides how far away the building is still
        /// worth drawing.</summary>
        public byte Tier;

        /// <summary>Longest footprint edge, which is what an overlap or slope test has to
        /// respect.</summary>
        public float LongAxis => Width > Depth ? Width : Depth;

        public float ShortAxis => Width > Depth ? Depth : Width;
    }

    /// <summary>
    /// The buildings a custom map borrows, and their real dimensions.
    ///
    /// Taken from Ignus (<c>Terrain_naval</c>) rather than Heartland, and not as a
    /// compromise: <c>suburbs_block*</c> is the only low-rise family in the game at
    /// 7–12 m and two to three storeys, and it comes as terraced runs 30 to 163 m long,
    /// which is what European housing looks like from the air. Heartland's cheapest
    /// housing is <c>residential_1a</c> at 32 m and flat-roofed. Ignus is also cheaper to
    /// draw — 1.95 submeshes per building against Heartland's 2.87.
    ///
    /// Every number here was measured in-game by walking the shipped prefabs, not read
    /// off an asset listing. The footprints matter as much as the names: a 163 m terrace
    /// is long enough that placing it at a random angle reads as debris rather than as a
    /// street, so the generator aligns them and needs their real extents to do it.
    ///
    /// Shared by the generator, which uses the dimensions for slope and overlap tests,
    /// and by the plugin, which uses the names to find the donor objects.
    /// </summary>
    public static class CityCatalogue
    {
        public const byte LowRise = 0;
        public const byte Commercial = 1;

        /// <summary>The shipped map these are borrowed from.</summary>
        public const string DonorMap = "Terrain_naval";

        public static readonly BuildingType[] Entries =
        {
            // --- low-rise terraces, 7-12 m. The bulk of any town.
            //
            // Ordered by longest footprint edge, ascending, within each tier. That order
            // is load-bearing: when a site will not take the building the mix chose, the
            // scatter walks backwards through this list for something that fits, and an
            // unsorted catalogue turns that retry into a random second guess. Sloping
            // ground should get a 44 m terrace, not another 163 m one. ---
            new BuildingType { Name = "suburbs_block19",     Width = 44f,    Depth = 38f,    Height = 7f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block23",     Width = 31f,    Depth = 52f,    Height = 8f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block13",     Width = 31f,    Depth = 91f,    Height = 9f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block14",     Width = 42f,    Depth = 93f,    Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block17",     Width = 99f,    Depth = 39f,    Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block7",      Width = 108f,   Depth = 81f,    Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block6",      Width = 113f,   Depth = 39f,    Height = 12f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block1",      Width = 114f,   Depth = 25f,    Height = 9f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block5",      Width = 116f,   Depth = 24f,    Height = 9f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block9",      Width = 30f,    Depth = 121f,   Height = 7f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block10",     Width = 31f,    Depth = 121f,   Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block11",     Width = 32f,    Depth = 123f,   Height = 9f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block3",      Width = 123f,   Depth = 37f,    Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block8",      Width = 123f,   Depth = 39f,    Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block2",      Width = 35f,    Depth = 134f,   Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block4",      Width = 35f,    Depth = 135f,   Height = 10f,   Tier = LowRise },
            new BuildingType { Name = "suburbs_block15",     Width = 162f,   Depth = 32f,    Height = 9f,    Tier = LowRise },
            new BuildingType { Name = "suburbs_block16",     Width = 163f,   Depth = 36f,    Height = 9f,    Tier = LowRise },

            // --- commercial, 27-31 m. Town centres only. ---
            new BuildingType { Name = "commercial_block3",   Width = 70f,    Depth = 53f,    Height = 31f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block13",  Width = 74f,    Depth = 36f,    Height = 31f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block14",  Width = 80f,    Depth = 63f,    Height = 30f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block10",  Width = 44f,    Depth = 105f,   Height = 27f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block16",  Width = 83f,    Depth = 105f,   Height = 28f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block2",   Width = 49f,    Depth = 106f,   Height = 27f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block15",  Width = 50f,    Depth = 107f,   Height = 31f,   Tier = Commercial },
            new BuildingType { Name = "commercial_block1",   Width = 112f,   Depth = 106f,   Height = 31f,   Tier = Commercial },
        };

        /// <summary>Index of a type by name, or -1. Used by the plugin to report which
        /// catalogue entries a game update has moved or renamed.</summary>
        public static int IndexOf(string name)
        {
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].Name == name) return i;

            return -1;
        }
    }
}
