// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DependencyGraphModel.cs (Piece D — dirty-set diagnostic)
//
// Contains: the READ-side model of DependencyGraph.json (the artifact ProvenanceGraph
// writes during a capture), plus a small dependency-free JSON parser, so a later load can
// reload a prior build's graph and feed it to DirtySetComputer.
//
// Used for: loading the persisted graph on an incremental load. ProvenanceGraph is the
// write side; this is the read side. Kept free of any RimWorld dependency so it (and the
// dirty-set algorithm that consumes it) can be unit-tested offline.
//
// Why: net481 has no System.Text.Json, and pulling a NuGet serializer into the mod is
// unwanted; the schema is small and fixed, so a focused recursive-descent parser is enough.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gagarin
{
    public sealed class GraphNode
    {
        public string Id;
        public string DefType;
        public string DefName;
        public string SourceMod;
        public string SourceFile;
    }

    public sealed class GraphPatchEdge
    {
        public string PatchId;
        public string SourceMod;
        public string OperationType;
        public string Xpath;
        public List<string> MatchedNodeIds = new List<string>();
        public List<string> ModifiedNodeIds = new List<string>();
    }

    public sealed class GraphInheritanceEdge
    {
        public string ChildNodeId;
        public string ParentName;
        public string ParentNodeId;   // null when unresolved
    }

    // The deserialized DependencyGraph.json. Mirrors ProvenanceGraph's emitted schema.
    public sealed class DependencyGraphData
    {
        public int Version;
        public readonly List<GraphNode> Nodes = new List<GraphNode>();
        public readonly List<GraphPatchEdge> PatchEdges = new List<GraphPatchEdge>();
        public readonly List<GraphInheritanceEdge> InheritanceEdges = new List<GraphInheritanceEdge>();

        // MayRequire index (P4): packageId -> def node ids gated on that mod's presence.
        // Case-insensitive to match how RimWorld compares packageIds and how the capture
        // side keyed them (authors reference the same mod with varying casing). Empty when
        // the graph predates P4 (the field is simply absent from older DependencyGraph.json).
        public readonly Dictionary<string, List<string>> MayRequireIndex =
            new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);

        public static DependencyGraphData Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static DependencyGraphData Parse(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null)
                throw new FormatException("DependencyGraph.json root is not an object");

            var data = new DependencyGraphData
            {
                Version = (int)AsNumber(Get(root, "version"))
            };

            foreach (var n in AsArray(Get(root, "nodes")))
            {
                var o = (Dictionary<string, object>)n;
                data.Nodes.Add(new GraphNode
                {
                    Id = AsString(Get(o, "id")),
                    DefType = AsString(Get(o, "defType")),
                    DefName = AsString(Get(o, "defName")),
                    SourceMod = AsString(Get(o, "sourceMod")),
                    SourceFile = AsString(Get(o, "sourceFile"))
                });
            }

            foreach (var e in AsArray(Get(root, "patchEdges")))
            {
                var o = (Dictionary<string, object>)e;
                var edge = new GraphPatchEdge
                {
                    PatchId = AsString(Get(o, "patchId")),
                    SourceMod = AsString(Get(o, "sourceMod")),
                    OperationType = AsString(Get(o, "operationType")),
                    Xpath = AsString(Get(o, "xpath"))
                };
                AddStrings(edge.MatchedNodeIds, Get(o, "matchedNodeIds"));
                AddStrings(edge.ModifiedNodeIds, Get(o, "modifiedNodeIds"));
                data.PatchEdges.Add(edge);
            }

            foreach (var e in AsArray(Get(root, "inheritanceEdges")))
            {
                var o = (Dictionary<string, object>)e;
                data.InheritanceEdges.Add(new GraphInheritanceEdge
                {
                    ChildNodeId = AsString(Get(o, "childNodeId")),
                    ParentName = AsString(Get(o, "parentName")),
                    ParentNodeId = AsString(Get(o, "parentNodeId"))
                });
            }

            // MayRequire index (P4). An object of packageId -> [nodeId, ...]. Absent in
            // pre-P4 graphs, in which case AsObject yields an empty map and Seed 6 no-ops.
            foreach (var kv in AsObject(Get(root, "mayRequire")))
            {
                var ids = new List<string>();
                AddStrings(ids, kv.Value);
                data.MayRequireIndex[kv.Key] = ids;
            }

            return data;
        }

        private static IEnumerable<KeyValuePair<string, object>> AsObject(object v)
            => v as Dictionary<string, object> ?? new Dictionary<string, object>();

        private static object Get(Dictionary<string, object> o, string key)
            => o.TryGetValue(key, out var v) ? v : null;

        private static string AsString(object v) => v as string; // JsonNull -> null

        private static double AsNumber(object v) => v is double d ? d : 0d;

        private static IEnumerable<object> AsArray(object v)
            => v as List<object> ?? new List<object>();

        private static void AddStrings(List<string> dest, object arr)
        {
            foreach (var item in AsArray(arr))
                if (item is string s)
                    dest.Add(s);
        }
    }

    // Minimal recursive-descent JSON parser. Returns Dictionary<string,object> for objects,
    // List<object> for arrays, string / double / bool / null for scalars. Sufficient for the
    // fixed DependencyGraph.json schema; not a general-purpose library.
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            object value = ParseValue(text, ref i);
            SkipWhitespace(text, ref i);
            if (i != text.Length)
                throw new FormatException("Trailing content after JSON value at " + i);
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
                throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            switch (c)
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

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                obj[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                char c = s[i++];
                if (c == '}') break;
                if (c != ',') throw new FormatException("Expected ',' or '}' at " + (i - 1));
            }
            return obj;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var arr = new List<object>();
            i++; // [
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return arr; }
            while (true)
            {
                arr.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                char c = s[i++];
                if (c == ']') break;
                if (c != ',') throw new FormatException("Expected ',' or ']' at " + (i - 1));
            }
            return arr;
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
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
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("Bad escape \\" + e + " at " + (i - 1));
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new FormatException("Expected '" + literal + "' at " + i);
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }
    }
}
