using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace CustomMaps
{
    /// <summary>
    /// A method's IL reduced to what changes when its source does, so that a patch standing in for a game
    /// method can tell whether the game it is running in still has the method it was checked against.
    ///
    /// Hashing the raw bytes would not do. The metadata tokens in them are row numbers, which move whenever
    /// the game adds a type or a method anywhere in its assembly, so every update would read as a change.
    /// Each token is therefore replaced by the name of what it points at (field, method, type or string
    /// literal), and everything else — opcodes, local and argument numbers, constants, branch offsets — is
    /// taken as it stands. The names the compiler invents for lambdas and their caches
    /// (<c>&lt;&gt;c</c>, <c>&lt;TryPathfind&gt;b__3_0</c>) carry their position in the class, so they
    /// all read as one name, and the lambdas' own bodies are fingerprinted with the method instead
    /// (<see cref="OfMethod"/>).
    ///
    /// The names come from the caller because who resolves them differs: the plugin asks the running
    /// game's <c>Module</c>, and the tests read the game's DLL from disk. Free of game types, so both use
    /// this same walk over the IL.
    /// </summary>
    internal static class IlFingerprint
    {
        static readonly Dictionary<short, OpCode> Codes = Table();

        /// <summary>
        /// The method's own IL, then, in order, that of each method it takes the address of
        /// (<c>ldftn</c>: the lambdas it hands to <c>List.Sort</c> and the like), combined.
        /// <paramref name="body"/> gives the IL of the method a token names.
        /// </summary>
        public static ulong OfMethod(byte[] il, Func<int, string> name, Func<int, byte[]> body)
        {
            ulong print = Of(il, name);
            foreach (int token in FunctionPointers(il))
                print = Combine(print, Of(body(token), name));
            return print;
        }

        /// <summary>FNV-1a over the opcodes, every operand that is not a token, and the name of what each
        /// token points at.</summary>
        public static ulong Of(byte[] il, Func<int, string> name)
        {
            if (il == null) throw new ArgumentNullException(nameof(il));

            ulong hash = 14695981039346656037UL;
            int at = 0;
            while (at < il.Length)
            {
                OpCode code = Read(il, ref at);
                hash = Mix(hash, (ushort)code.Value);

                int size = OperandSize(code, il, at);
                if (IsToken(code.OperandType))
                {
                    foreach (char c in Readable(name(BitConverter.ToInt32(il, at)) ?? "")) hash = Mix(hash, c);
                    hash = Mix(hash, 0);
                }
                else
                {
                    for (int i = 0; i < size; i++) hash = Mix(hash, il[at + i]);
                }

                at += size;
            }

            return hash;
        }

        /// <summary>The tokens of every method whose address the IL takes, in order.</summary>
        public static List<int> FunctionPointers(byte[] il)
        {
            var tokens = new List<int>();
            int at = 0;
            while (at < il.Length)
            {
                OpCode code = Read(il, ref at);
                if (code.Value == OpCodes.Ldftn.Value) tokens.Add(BitConverter.ToInt32(il, at));
                at += OperandSize(code, il, at);
            }

            return tokens;
        }

        /// <summary>A name as the fingerprint reads it: compiler-generated names, which are numbered by
        /// their position in the class, all as one.</summary>
        public static string Readable(string name) => name != null && name.IndexOf('<') >= 0 ? "<>" : name;

        /// <summary>Several methods' fingerprints as one, each counted and in the order given, for a patch
        /// that relies on more than the one method it replaces.</summary>
        public static ulong OfAll(IEnumerable<ulong> prints)
        {
            ulong all = 14695981039346656037UL;
            foreach (ulong print in prints) all = Combine(all, print);
            return all;
        }

        public static ulong Combine(ulong a, ulong b)
        {
            for (int i = 0; i < 8; i++) a = Mix(a, (byte)(b >> (8 * i)));
            return a;
        }

        static ulong Mix(ulong hash, int value)
        {
            hash ^= (byte)value;
            hash *= 1099511628211UL;
            hash ^= (byte)(value >> 8);
            return hash * 1099511628211UL;
        }

        static OpCode Read(byte[] il, ref int at)
        {
            short value = il[at++];
            if (value == 0xFE)
            {
                if (at >= il.Length) throw new FormatException("IL ends inside a two-byte opcode");
                value = unchecked((short)(0xFE00 | il[at++]));
            }

            if (!Codes.TryGetValue(value, out OpCode code))
                throw new FormatException($"unknown opcode 0x{value & 0xFFFF:X} at {at}");
            return code;
        }

        static int OperandSize(OpCode code, byte[] il, int at)
        {
            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default:
                    return 4;
            }
        }

        static bool IsToken(OperandType type)
            => type == OperandType.InlineField || type == OperandType.InlineMethod || type == OperandType.InlineSig
            || type == OperandType.InlineString || type == OperandType.InlineTok || type == OperandType.InlineType;

        static Dictionary<short, OpCode> Table()
        {
            var codes = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode code) codes[code.Value] = code;
            return codes;
        }
    }
}
