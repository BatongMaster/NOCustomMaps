using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CustomMaps
{
    /// <summary>Thrown for malformed JSON, with the offset so a bad manifest can be pointed at.</summary>
    public sealed class JsonException : Exception
    {
        public int Offset { get; }
        public JsonException(string message, int offset)
            : base($"{message} (at offset {offset})") => Offset = offset;
    }

    /// <summary>
    /// A minimal JSON reader: objects, arrays, strings, numbers, booleans, null.
    ///
    /// Hand-rolled rather than taken from a package because <c>Core/</c> is compiled
    /// into the test project with no reference assemblies at all, and into a
    /// netstandard2.1 BepInEx plugin where an extra dependency would have to be
    /// shipped alongside the DLL. Unity's <c>JsonUtility</c> is the obvious
    /// alternative but lives in <c>UnityEngine.CoreModule</c>, which <c>Core/</c> may
    /// not reference — and it cannot represent an absent field distinctly from a
    /// zero, which the manifest validation depends on.
    ///
    /// Produces <see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/>,
    /// <see cref="string"/>, <see cref="double"/>, <see cref="bool"/> and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            int i = 0;
            SkipWhitespace(text, ref i);
            object value = ParseValue(text, ref i);
            SkipWhitespace(text, ref i);
            if (i != text.Length) throw new JsonException("trailing content after top-level value", i);
            return value;
        }

        // --- typed accessors -------------------------------------------------
        // Every one of these takes the enclosing object and a key so a failure can
        // name the field. `required: false` returns the fallback for an absent key
        // but still throws for a present-but-wrong-typed one, which is the
        // distinction that matters when validating a hand-edited manifest.

        public static Dictionary<string, object> AsObject(object value, string what = "value")
            => value as Dictionary<string, object>
               ?? throw new JsonException($"{what} is not an object", 0);

        public static List<object> AsArray(object value, string what = "value")
            => value as List<object>
               ?? throw new JsonException($"{what} is not an array", 0);

        public static string GetString(Dictionary<string, object> obj, string key, string fallback = null)
        {
            if (obj == null || !obj.TryGetValue(key, out object v) || v == null) return fallback;
            return v as string ?? throw new JsonException($"'{key}' is not a string", 0);
        }

        public static double GetNumber(Dictionary<string, object> obj, string key, double fallback = 0d)
        {
            if (obj == null || !obj.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is double d) return d;
            throw new JsonException($"'{key}' is not a number", 0);
        }

        public static float GetFloat(Dictionary<string, object> obj, string key, float fallback = 0f)
            => (float)GetNumber(obj, key, fallback);

        public static int GetInt(Dictionary<string, object> obj, string key, int fallback = 0)
        {
            double d = GetNumber(obj, key, fallback);
            if (d > int.MaxValue || d < int.MinValue || d != Math.Floor(d))
                throw new JsonException($"'{key}' is not an integer", 0);
            return (int)d;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback = false)
        {
            if (obj == null || !obj.TryGetValue(key, out object v) || v == null) return fallback;
            return v is bool b ? b : throw new JsonException($"'{key}' is not a boolean", 0);
        }

        public static bool Has(Dictionary<string, object> obj, string key)
            => obj != null && obj.TryGetValue(key, out object v) && v != null;

        // --- parser ----------------------------------------------------------

        static object ParseValue(string s, ref int i)
        {
            if (i >= s.Length) throw new JsonException("unexpected end of input", i);

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;                                             // '{'
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new JsonException("expected a key string", i);

                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);

                if (i >= s.Length || s[i] != ':') throw new JsonException("expected ':'", i);
                i++;

                SkipWhitespace(s, ref i);
                // Last write wins on a duplicate key, matching every mainstream parser.
                result[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);

                if (i >= s.Length) throw new JsonException("unterminated object", i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new JsonException("expected ',' or '}'", i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++;                                             // '['
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);

                if (i >= s.Length) throw new JsonException("unterminated array", i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new JsonException("expected ',' or ']'", i);
            }
        }

        static string ParseString(string s, ref int i)
        {
            i++;                                             // opening quote
            var sb = new StringBuilder();

            while (true)
            {
                if (i >= s.Length) throw new JsonException("unterminated string", i);

                char c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) throw new JsonException("unterminated escape", i);
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new JsonException("truncated \\u escape", i);
                        if (!ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out ushort code))
                            throw new JsonException("malformed \\u escape", i);
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw new JsonException($"unknown escape '\\{e}'", i - 1);
                }
            }
        }

        static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'
                                    || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
                i++;

            string token = s.Substring(start, i - start);
            // InvariantCulture matters: a Swiss or German locale parses "0.5" as 5.
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new JsonException($"'{token}' is not a number", start);
            return value;
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new JsonException($"expected '{literal}'", i);
            i += literal.Length;
        }

        static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }
    }
}
