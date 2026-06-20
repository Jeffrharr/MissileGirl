// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Json.cs (Piece C — replay harness)
//
// Contains: a tiny dependency-free JSON value tree (object/array/string/number/
// null) with an emitter, a recursive-descent parser, and typed-field accessor
// extensions.
//
// Used for: round-tripping the Piece A DependencyGraph and Piece B
// PatchClassification documents through their JSON schemas inside the harness.
//
// Why: keeping the harness's only real dependency as XMLDiffPatch means we avoid
// pulling in System.Text.Json/Newtonsoft for two simple, well-known schemas; this
// is just enough JSON to serialize and parse those contracts, nothing more.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gagarin.IncrementalReplay
{
    // Tiny dependency-free JSON value tree + emitter/parser.
    //
    // We deliberately avoid pulling in System.Text.Json / Newtonsoft for this prototype:
    // the Piece A / Piece B contract files are simple, and a self-contained reader keeps
    // the harness's dependency surface to just XMLDiffPatch. This is NOT a general JSON
    // library — just enough to round-trip the two documented schemas.

    internal abstract class JsonValue
    {
        public static JsonObject Obj() => new JsonObject();
        public static JsonArray Arr() => new JsonArray();
        public static JsonValue Of(string s) => new JsonString(s);
        public static JsonValue Of(int i) => new JsonNumber(i);
        public static readonly JsonValue Null = new JsonNull();

        public abstract void Write(StringBuilder sb);

        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }
    }

    internal sealed class JsonNull : JsonValue
    {
        public override void Write(StringBuilder sb) => sb.Append("null");
    }

    internal sealed class JsonString : JsonValue
    {
        private readonly string _v;
        public JsonString(string v) { _v = v; }
        public string Value => _v;
        public override void Write(StringBuilder sb)
        {
            if (_v == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (var c in _v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }

    internal sealed class JsonNumber : JsonValue
    {
        private readonly double _v;
        public JsonNumber(double v) { _v = v; }
        public int AsInt => (int)_v;
        public override void Write(StringBuilder sb)
            => sb.Append(_v.ToString(CultureInfo.InvariantCulture));
    }

    internal sealed class JsonArray : JsonValue
    {
        public readonly List<JsonValue> Items = new List<JsonValue>();
        public JsonArray Add(JsonValue v) { Items.Add(v); return this; }
        public override void Write(StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Items[i].Write(sb);
            }
            sb.Append(']');
        }
    }

    internal sealed class JsonObject : JsonValue
    {
        public readonly List<KeyValuePair<string, JsonValue>> Members
            = new List<KeyValuePair<string, JsonValue>>();

        public JsonObject Set(string key, JsonValue v)
        {
            Members.Add(new KeyValuePair<string, JsonValue>(key, v));
            return this;
        }

        public JsonValue Get(string key)
        {
            foreach (var m in Members)
                if (m.Key == key) return m.Value;
            return null;
        }

        public override void Write(StringBuilder sb)
        {
            sb.Append('{');
            for (int i = 0; i < Members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                new JsonString(Members[i].Key).Write(sb);
                sb.Append(':');
                Members[i].Value.Write(sb);
            }
            sb.Append('}');
        }
    }

    // Recursive-descent parser for the subset above.
    internal sealed class JsonParser
    {
        private readonly string _s;
        private int _i;

        private JsonParser(string s) { _s = s; }

        public static JsonValue Parse(string s)
        {
            var p = new JsonParser(s);
            p.Ws();
            var v = p.Value();
            p.Ws();
            return v;
        }

        private void Ws()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private JsonValue Value()
        {
            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return new JsonString(ParseString());
                case 't': _i += 4; return new JsonNumber(1); // not used in our schema
                case 'f': _i += 5; return new JsonNumber(0);
                case 'n': _i += 4; return JsonValue.Null;
                default: return ParseNumber();
            }
        }

        private JsonObject ParseObject()
        {
            var o = new JsonObject();
            _i++; // {
            Ws();
            if (_s[_i] == '}') { _i++; return o; }
            while (true)
            {
                Ws();
                var key = ParseString();
                Ws();
                _i++; // :
                Ws();
                o.Set(key, Value());
                Ws();
                if (_s[_i] == ',') { _i++; continue; }
                _i++; // }
                break;
            }
            return o;
        }

        private JsonArray ParseArray()
        {
            var a = new JsonArray();
            _i++; // [
            Ws();
            if (_s[_i] == ']') { _i++; return a; }
            while (true)
            {
                Ws();
                a.Add(Value());
                Ws();
                if (_s[_i] == ',') { _i++; continue; }
                _i++; // ]
                break;
            }
            return a;
        }

        private string ParseString()
        {
            var sb = new StringBuilder();
            _i++; // opening quote
            while (_s[_i] != '"')
            {
                char c = _s[_i++];
                if (c == '\\')
                {
                    char e = _s[_i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            _i++; // closing quote
            return sb.ToString();
        }

        private JsonNumber ParseNumber()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '+'
                || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'))
                _i++;
            return new JsonNumber(double.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture));
        }
    }

    // Small helpers for reading typed fields out of a JsonObject.
    internal static class JsonExt
    {
        public static string Str(this JsonObject o, string key)
            => (o.Get(key) as JsonString)?.Value;

        public static JsonArray Array(this JsonObject o, string key)
            => o.Get(key) as JsonArray;

        public static JsonObject ObjAt(this JsonArray a, int i) => a.Items[i] as JsonObject;
    }
}
