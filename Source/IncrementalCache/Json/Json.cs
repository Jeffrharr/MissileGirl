// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Json.cs (Piece B — patch classification)
//
// Contains: a tiny, dependency-free JSON value model (JsonObject/Array/String/
// Number/Bool/Null) plus a recursive-descent parser and a deterministic,
// insertion-order-preserving serializer.
//
// Used for: reading Piece A's DependencyGraph.json and writing this tool's
// PatchClassification.json output, plus all the in-memory JSON the self-tests
// build.
//
// Why: this tool must build on the repo's net481/mono toolchain with zero NuGet
// restore, so we hand-roll the small JSON subset the contract needs instead of
// pulling in Newtonsoft or System.Text.Json. Stable ordering keeps diffs clean.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gagarin.IncrementalCache.Json
{
    // Tiny, dependency-free JSON value model + parser/serializer.
    //
    // We intentionally avoid Newtonsoft / System.Text.Json here: this tool must build
    // on the repo's net481 / mono toolchain with zero NuGet restore, so a hand-rolled
    // reader/writer for the small, well-known schema is the lightest option. It supports
    // exactly the subset the contract needs (objects, arrays, strings, numbers, bools, null)
    // and emits stable, deterministic output (insertion order preserved) so diffs stay clean.
    public abstract class JsonValue
    {
        public static JsonValue Parse(string text)
        {
            var parser = new JsonParser(text);
            JsonValue value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                throw new FormatException("Trailing content after JSON document.");
            return value;
        }

        public abstract void Write(StringBuilder sb, int indent);

        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb, 0);
            return sb.ToString();
        }

        protected static void WriteIndent(StringBuilder sb, int indent)
        {
            for (int i = 0; i < indent; i++)
                sb.Append("  ");
        }
    }

    public sealed class JsonObject : JsonValue
    {
        // List of pairs (not a Dictionary) so we keep deterministic insertion order in output.
        private readonly List<KeyValuePair<string, JsonValue>> members = new List<KeyValuePair<string, JsonValue>>();
        private readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count => members.Count;
        public IEnumerable<KeyValuePair<string, JsonValue>> Members => members;

        public void Add(string key, JsonValue value)
        {
            index[key] = members.Count;
            members.Add(new KeyValuePair<string, JsonValue>(key, value));
        }

        public bool TryGet(string key, out JsonValue value)
        {
            if (index.TryGetValue(key, out int i))
            {
                value = members[i].Value;
                return true;
            }
            value = null;
            return false;
        }

        public JsonValue Get(string key)
        {
            if (TryGet(key, out JsonValue value))
                return value;
            throw new KeyNotFoundException($"Missing JSON key '{key}'.");
        }

        public override void Write(StringBuilder sb, int indent)
        {
            if (members.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            sb.Append("{\n");
            for (int i = 0; i < members.Count; i++)
            {
                WriteIndent(sb, indent + 1);
                JsonString.WriteEscaped(sb, members[i].Key);
                sb.Append(": ");
                members[i].Value.Write(sb, indent + 1);
                if (i < members.Count - 1)
                    sb.Append(',');
                sb.Append('\n');
            }
            WriteIndent(sb, indent);
            sb.Append('}');
        }
    }

    public sealed class JsonArray : JsonValue
    {
        private readonly List<JsonValue> items = new List<JsonValue>();

        public int Count => items.Count;
        public IReadOnlyList<JsonValue> Items => items;

        public void Add(JsonValue value) => items.Add(value);

        public override void Write(StringBuilder sb, int indent)
        {
            if (items.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append("[\n");
            for (int i = 0; i < items.Count; i++)
            {
                WriteIndent(sb, indent + 1);
                items[i].Write(sb, indent + 1);
                if (i < items.Count - 1)
                    sb.Append(',');
                sb.Append('\n');
            }
            WriteIndent(sb, indent);
            sb.Append(']');
        }
    }

    public sealed class JsonString : JsonValue
    {
        public string Value { get; }
        public JsonString(string value) => Value = value;

        public override void Write(StringBuilder sb, int indent) => WriteEscaped(sb, Value);

        public static void WriteEscaped(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    public sealed class JsonNumber : JsonValue
    {
        public double Value { get; }
        public JsonNumber(double value) => Value = value;

        public override void Write(StringBuilder sb, int indent)
        {
            // Emit integers without a decimal point to keep counts clean.
            if (Value == Math.Floor(Value) && !double.IsInfinity(Value))
                sb.Append(((long)Value).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(Value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public sealed class JsonBool : JsonValue
    {
        public bool Value { get; }
        public JsonBool(bool value) => Value = value;
        public override void Write(StringBuilder sb, int indent) => sb.Append(Value ? "true" : "false");
    }

    public sealed class JsonNull : JsonValue
    {
        public static readonly JsonNull Instance = new JsonNull();
        private JsonNull() { }
        public override void Write(StringBuilder sb, int indent) => sb.Append("null");
    }

    // Recursive-descent parser. Small enough to keep inline; throws FormatException on bad input.
    internal sealed class JsonParser
    {
        private readonly string text;
        private int pos;

        public JsonParser(string text) => this.text = text ?? throw new ArgumentNullException(nameof(text));
        public bool AtEnd => pos >= text.Length;

        public JsonValue ParseValue()
        {
            SkipWhitespace();
            if (AtEnd)
                throw new FormatException("Unexpected end of JSON.");
            char c = text[pos];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return new JsonString(ParseString());
                case 't':
                case 'f': return ParseBool();
                case 'n': ParseLiteral("null"); return JsonNull.Instance;
                default: return ParseNumber();
            }
        }

        private JsonObject ParseObject()
        {
            var obj = new JsonObject();
            pos++; // consume '{'
            SkipWhitespace();
            if (!AtEnd && text[pos] == '}') { pos++; return obj; }
            while (true)
            {
                SkipWhitespace();
                string key = ParseString();
                SkipWhitespace();
                Expect(':');
                JsonValue value = ParseValue();
                obj.Add(key, value);
                SkipWhitespace();
                char c = Next();
                if (c == ',') continue;
                if (c == '}') break;
                throw new FormatException($"Expected ',' or '}}' at position {pos}.");
            }
            return obj;
        }

        private JsonArray ParseArray()
        {
            var arr = new JsonArray();
            pos++; // consume '['
            SkipWhitespace();
            if (!AtEnd && text[pos] == ']') { pos++; return arr; }
            while (true)
            {
                arr.Add(ParseValue());
                SkipWhitespace();
                char c = Next();
                if (c == ',') continue;
                if (c == ']') break;
                throw new FormatException($"Expected ',' or ']' at position {pos}.");
            }
            return arr;
        }

        private string ParseString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw new FormatException("Unterminated string.");
                char c = text[pos++];
                if (c == '"') break;
                if (c == '\\')
                {
                    char e = Next();
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            string hex = text.Substring(pos, 4);
                            pos += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new FormatException($"Invalid escape '\\{e}'.");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private JsonValue ParseNumber()
        {
            int start = pos;
            while (!AtEnd)
            {
                char c = text[pos];
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    pos++;
                else
                    break;
            }
            string token = text.Substring(start, pos - start);
            if (token.Length == 0)
                throw new FormatException($"Invalid JSON token at position {start}.");
            return new JsonNumber(double.Parse(token, CultureInfo.InvariantCulture));
        }

        private JsonValue ParseBool()
        {
            if (text[pos] == 't') { ParseLiteral("true"); return new JsonBool(true); }
            ParseLiteral("false");
            return new JsonBool(false);
        }

        private void ParseLiteral(string literal)
        {
            if (pos + literal.Length > text.Length || text.Substring(pos, literal.Length) != literal)
                throw new FormatException($"Expected '{literal}' at position {pos}.");
            pos += literal.Length;
        }

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(text[pos]))
                pos++;
        }

        private char Next()
        {
            if (AtEnd) throw new FormatException("Unexpected end of JSON.");
            return text[pos++];
        }

        private void Expect(char c)
        {
            char got = Next();
            if (got != c)
                throw new FormatException($"Expected '{c}' but found '{got}' at position {pos - 1}.");
        }
    }
}
