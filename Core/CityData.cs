using System;
using System.Collections.Generic;
using System.IO;

namespace CustomMaps
{
    /// <summary>One building, placed.</summary>
    public struct Placement
    {
        /// <summary>Index into <see cref="CityCatalogue.Entries"/>.</summary>
        public byte Type;

        /// <summary>Position in map metres. Y is the ground the building sits on.</summary>
        public float X, Y, Z;

        /// <summary>Rotation about Y, in degrees.</summary>
        public float Yaw;
    }

    /// <summary>
    /// The map's buildings, on disk.
    ///
    /// Baked by the generator and read by the plugin, which clones a donor prefab at each
    /// entry. Baked rather than scattered at load for one decisive reason: every client
    /// has to agree on where the buildings are, or collision, occlusion and line of sight
    /// disagree between machines. A baked table makes that a fact rather than a hope
    /// about float determinism across different CPUs.
    ///
    /// Twenty bytes per building — a type index, three bytes of padding that keep the
    /// floats aligned, then a position and a heading. Twenty-five thousand of them is
    /// under half a megabyte. Little-endian, matching the tree scatter and the road
    /// polylines.
    /// </summary>
    public static class CityData
    {
        public static readonly byte[] Magic = { (byte)'N', (byte)'O', (byte)'C', (byte)'I', (byte)'T', (byte)'Y', (byte)'0', (byte)'1' };

        /// <summary>Refuses anything implausible before allocating from it.</summary>
        public const int MaxPlacements = 2_000_000;

        public static void Write(string path, IReadOnlyList<Placement> placements)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(placements.Count);

                foreach (Placement placement in placements)
                {
                    writer.Write(placement.Type);
                    writer.Write((byte)0);                  // three bytes of padding, so the
                    writer.Write((ushort)0);                // four floats land 4-byte aligned
                    writer.Write(placement.X);
                    writer.Write(placement.Y);
                    writer.Write(placement.Z);
                    writer.Write(placement.Yaw);
                }
            }
        }

        public static List<Placement> Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            using (var stream = new MemoryStream(bytes, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("city data is truncated");

                for (int i = 0; i < Magic.Length; i++)
                    if (magic[i] != Magic[i])
                        throw new InvalidDataException("not city data — the magic does not match");

                int count = reader.ReadInt32();
                if (count < 0 || count > MaxPlacements)
                    throw new InvalidDataException($"city data claims {count} placements");

                var placements = new List<Placement>(count);
                for (int i = 0; i < count; i++)
                {
                    byte type = reader.ReadByte();
                    reader.ReadByte();
                    reader.ReadUInt16();

                    placements.Add(new Placement
                    {
                        Type = type,
                        X = reader.ReadSingle(),
                        Y = reader.ReadSingle(),
                        Z = reader.ReadSingle(),
                        Yaw = reader.ReadSingle(),
                    });
                }

                return placements;
            }
        }
    }
}
