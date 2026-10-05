using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The fingerprint that keeps the road pathfinder patch to the game build it was checked against: what
    /// it reads and what it ignores, and that the game installed here is that build.
    /// </summary>
    public class IlFingerprintTests
    {
        readonly ITestOutputHelper _output;

        public IlFingerprintTests(ITestOutputHelper output) => _output = output;

        // ldarg.0, ldfld <token>, ret
        static byte[] LoadField(int token)
        {
            byte[] t = BitConverter.GetBytes(token);
            return new byte[] { 0x02, 0x7B, t[0], t[1], t[2], t[3], 0x2A };
        }

        [Fact]
        public void ReadsWhatATokenNamesNotItsNumber()
        {
            var names = new Dictionary<int, string> { [0x04000010] = "dist", [0x04000099] = "dist", [0x04000011] = "length" };
            string Name(int token) => names[token];

            ulong print = IlFingerprint.Of(LoadField(0x04000010), Name);
            Assert.Equal(print, IlFingerprint.Of(LoadField(0x04000099), Name));
            Assert.NotEqual(print, IlFingerprint.Of(LoadField(0x04000011), Name));

            // ldflda in place of ldfld: same token, another instruction.
            byte[] address = LoadField(0x04000010);
            address[1] = 0x7C;
            Assert.NotEqual(print, IlFingerprint.Of(address, Name));
        }

        [Fact]
        public void ReadsConstantsAndBranches()
        {
            // ldc.r4 10, ret against ldc.r4 20, ret
            byte[] ten = { 0x22, 0, 0, 0x20, 0x41, 0x2A };
            byte[] twenty = { 0x22, 0, 0, 0xA0, 0x41, 0x2A };
            Assert.NotEqual(IlFingerprint.Of(ten, _ => ""), IlFingerprint.Of(twenty, _ => ""));

            // br.s +0 against br.s +1 (over a nop)
            byte[] near = { 0x2B, 0x00, 0x00, 0x2A };
            byte[] far = { 0x2B, 0x01, 0x00, 0x2A };
            Assert.NotEqual(IlFingerprint.Of(near, _ => ""), IlFingerprint.Of(far, _ => ""));
        }

        [Fact]
        public void CompilerGeneratedNamesReadAsOne()
        {
            Assert.Equal("<>", IlFingerprint.Readable("<TryPathfind>b__3_0"));
            Assert.Equal("<>", IlFingerprint.Readable("<>9__3_0"));
            Assert.Equal("Sort", IlFingerprint.Readable("Sort"));

            ulong a = IlFingerprint.Of(LoadField(1), _ => "<TryPathfind>b__3_0");
            ulong b = IlFingerprint.Of(LoadField(1), _ => "<TryPathfind>b__4_0");
            Assert.Equal(a, b);
        }

        [Fact]
        public void TheBodiesOfItsLambdasCount()
        {
            // switch (2 targets), ldftn <token>, ret: the switch's operand is skipped whole, so the ldftn
            // after it is found.
            byte[] il = { 0x45, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFE, 0x06, 0x05, 0, 0, 0x06, 0x2A };
            Assert.Equal(new[] { 0x06000005 }, IlFingerprint.FunctionPointers(il));

            byte[] byDistance = LoadField(7);
            byte[] byAddress = LoadField(7);
            byAddress[1] = 0x7C;

            ulong one = IlFingerprint.OfMethod(il, _ => "x", _ => byDistance);
            ulong other = IlFingerprint.OfMethod(il, _ => "x", _ => byAddress);
            Assert.NotEqual(one, other);
            Assert.NotEqual(IlFingerprint.Of(il, _ => "x"), one);
        }

        [Fact]
        public void UnknownOpcodesAreRefused()
        {
            Assert.Throws<FormatException>(() => IlFingerprint.Of(new byte[] { 0xF0 }, _ => ""));
            Assert.Throws<FormatException>(() => IlFingerprint.Of(new byte[] { 0xFE }, _ => ""));
        }

        [Fact]
        public void SeveralMethodsEachCountInTheirOrder()
        {
            ulong a = IlFingerprint.Of(LoadField(1), _ => "dist"), b = IlFingerprint.Of(LoadField(1), _ => "parent");

            Assert.NotEqual(IlFingerprint.OfAll(new[] { a, b }), IlFingerprint.OfAll(new[] { b, a }));
            Assert.NotEqual(IlFingerprint.OfAll(new[] { a, b }), IlFingerprint.OfAll(new[] { a }));
            Assert.NotEqual(IlFingerprint.OfAll(new[] { a, b }), IlFingerprint.OfAll(new[] { a, a }));
            Assert.Equal(IlFingerprint.OfAll(new[] { a, b }), IlFingerprint.OfAll(new List<ulong> { a, b }));
        }

        /// <summary>
        /// The game's <c>RoadPathfinder.TryPathfind</c>, and the two <c>ClearPathfindingData</c> it starts
        /// with, read from its DLL, are the methods <see cref="RoadPathSearch"/> and the patch were checked
        /// against (<see cref="RoadPathSearch.GameMethods"/>). Reads the Assembly-CSharp.dll the plugin
        /// builds against (<c>ManagedDir</c>, as CI sets it, or the default Steam install); passes without
        /// checking anything where there is none.
        ///
        /// This ties <c>dotnet test</c> to the game build on purpose: after a game update that changes any
        /// of these methods it fails, here and in CI, until someone has checked the search again and set
        /// the new value. The CI image carries the game's DLLs (<c>ci/collect-refs.ps1</c>), so whoever
        /// refreshes it after a game update should expect this test to fail if the search changed, and
        /// update <see cref="RoadPathSearch.VerifiedGameMethods"/> in the same change.
        /// </summary>
        [Fact]
        public void TheInstalledGameHasTheRoadSearchThisWasCheckedAgainst()
        {
            string dll = GameAssembly();
            if (dll == null)
            {
                _output.WriteLine("Assembly-CSharp.dll not found; nothing checked");
                return;
            }

            ulong print = IlFingerprint.OfAll(RoadPathSearch.GameMethods.Select(m => GameMethodPrint(dll, m[0], m[1], m[2])));
            _output.WriteLine($"{dll}: RoadPathfinder.TryPathfind with its ClearPathfindingData {print:X16}");

            Assert.True(print == RoadPathSearch.VerifiedGameMethods,
                $"The game's RoadPathfinder.TryPathfind or a ClearPathfindingData before it has changed (fingerprint " +
                $"0x{print:X16}UL). Decompile them (NOMapForge/tools/GameProbe), check they still match RoadPathSearch " +
                "and the opening FastRoadPathfinder copies, then set RoadPathSearch.VerifiedGameMethods to that value.");
        }

        static string GameAssembly()
        {
            var dirs = new List<string>();
            string configured = Environment.GetEnvironmentVariable("ManagedDir");
            if (!string.IsNullOrEmpty(configured)) dirs.Add(configured);
            dirs.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option\NuclearOption_Data\Managed");
            return dirs.Select(d => Path.Combine(d, "Assembly-CSharp.dll")).FirstOrDefault(File.Exists);
        }

        /// <summary>A method's fingerprint as the plugin computes it in the game, but with every name read
        /// from the file's metadata as the runtime's <c>Module.ResolveMember(token).Name</c> gives it.</summary>
        internal static ulong GameMethodPrint(string dll, string space, string type, string method)
        {
            using var stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            MetadataReader md = pe.GetMetadataReader();

            MethodDefinition found = md.TypeDefinitions.Select(md.GetTypeDefinition)
                .Where(t => md.GetString(t.Name) == type && md.GetString(t.Namespace) == space)
                .SelectMany(t => t.GetMethods().Select(md.GetMethodDefinition))
                .Single(m => md.GetString(m.Name) == method);

            byte[] Body(MethodDefinition m) => pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();

            return IlFingerprint.OfMethod(Body(found), token => Name(md, token),
                token => Body(md.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(token))));
        }

        static string Name(MetadataReader md, int token)
        {
            if ((uint)token >> 24 == 0x70) return md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
            return Name(md, MetadataTokens.EntityHandle(token));
        }

        static string Name(MetadataReader md, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.StandaloneSignature: return "sig";
                case HandleKind.FieldDefinition: return md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)handle).Name);
                case HandleKind.MethodDefinition: return md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)handle).Name);
                case HandleKind.MemberReference: return md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name);
                case HandleKind.MethodSpecification: return Name(md, md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                case HandleKind.TypeDefinition: return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Name);
                case HandleKind.TypeReference: return md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Name);
                case HandleKind.TypeSpecification:
                    return md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(new TypeNames(md), null);
                default: throw new NotSupportedException(handle.Kind.ToString());
            }
        }

        /// <summary>Types named as <c>Type.Name</c> names them: a generic instance by its definition
        /// (<c>List`1</c>), an array with its brackets.</summary>
        sealed class TypeNames : ISignatureTypeProvider<string, object>
        {
            readonly MetadataReader _md;

            public TypeNames(MetadataReader md) => _md = md;

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Name(_md, handle);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Name(_md, handle);
            public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind) => Name(_md, handle);
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
            public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "method";
        }
    }
}
