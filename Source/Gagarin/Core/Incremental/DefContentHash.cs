// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DefContentHash.cs (staleness substrate — per-def content hashing)
//
// Contains: a PURE, deterministic content hash for a def's resolved serialized value, plus the
// map/diff/serialize helpers that turn it into a persisted per-def fingerprint of a load.
//
// Why this exists: today the only thing that catches "a def's resolved value changed but the dirty
// set didn't dirty it" (staleness — e.g. from an op invisible to capture) is the dirty-set gate's
// per-def diff of the prior vs rebuilt Unified.xml. That works but (a) needs the full ~200MB Unified
// kept around to compare, and (b) is purely diagnostic. A compact, persisted per-def content
// fingerprint (id -> hash) is the substrate for cheaper, durable staleness checks: compare this
// load's fingerprint to the prior load's to get the TRUE content-change set independent of the seed
// machinery, attribute changed defs to the mods that own them, and (longer term) drive a
// sampled-shadow-rebuild self-check at serve time where no full rebuild exists. See issue #26.
//
// The hash is FNV-1a 64-bit over the UTF-16 code units of the def's serialized value — the same
// dependency-free, process-stable scheme MetricsLog.ModListHash uses (NOT string.GetHashCode, which
// is randomized per process), so a fingerprint written one run is comparable the next. Collisions at
// 64 bits over tens of thousands of defs are astronomically unlikely; a collision would only ever
// MASK a change (a false "unchanged"), so this is a diagnostic aid, never a correctness gate on its
// own.
//
// Persistence format is line-oriented TSV ("{id}\t{hash}\n"), not JSON: the schema is two columns,
// def ids never contain a tab or newline, and TSV needs no parser/serializer dependency (net481 has
// no System.Text.Json) and stays greppable.
//
// Why pure: string/dictionary math with no RimWorld types — unit-tested offline like the other
// Incremental pure components. The caller supplies the id -> serialized-value map (the gate already
// has it as UnifiedCacheDiff.IndexById's result).

using System;
using System.Collections.Generic;
using System.Text;

namespace Gagarin
{
    public static class DefContentHash
    {
        // FNV-1a 64-bit over the string's UTF-16 code units, as 16 lowercase hex digits. Deterministic
        // across runs/processes (unlike string.GetHashCode). A null/empty value hashes to the FNV
        // offset basis, which is fine — empty defs are vanishingly rare and still compare stably.
        public static string Hash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    h = (h ^ (byte)(c & 0xFF)) * prime;
                    h = (h ^ (byte)((c >> 8) & 0xFF)) * prime;
                }
            }
            return h.ToString("x16");
        }

        // Hash every def's serialized value, yielding id -> content hash. Input is typically
        // UnifiedCacheDiff.IndexById(unifiedDoc) (id -> OuterXml).
        public static Dictionary<string, string> HashAll(IDictionary<string, string> idToSerialized)
        {
            var result = new Dictionary<string, string>(
                idToSerialized?.Count ?? 0, StringComparer.Ordinal);
            if (idToSerialized != null)
                foreach (KeyValuePair<string, string> kv in idToSerialized)
                    result[kv.Key] = Hash(kv.Value);
            return result;
        }

        // The content-change set between two per-def fingerprints: defs present in both whose hash
        // differs (changed), present only in current (added), present only in prior (removed). This is
        // the TRUE change set of the load, independent of how the dirty set was computed — so it is
        // the oracle a staleness check compares the dirty set against.
        public static void Diff(
            IDictionary<string, string> prior,
            IDictionary<string, string> current,
            out List<string> changed,
            out List<string> added,
            out List<string> removed)
        {
            changed = new List<string>();
            added = new List<string>();
            removed = new List<string>();
            if (current != null)
                foreach (KeyValuePair<string, string> kv in current)
                {
                    if (prior != null && prior.TryGetValue(kv.Key, out string p))
                    {
                        if (!string.Equals(p, kv.Value, StringComparison.Ordinal))
                            changed.Add(kv.Key);
                    }
                    else
                    {
                        added.Add(kv.Key);
                    }
                }
            if (prior != null)
                foreach (string id in prior.Keys)
                    if (current == null || !current.ContainsKey(id))
                        removed.Add(id);
            changed.Sort(StringComparer.Ordinal);
            added.Sort(StringComparer.Ordinal);
            removed.Sort(StringComparer.Ordinal);
        }

        // Serialize id -> hash as line-oriented TSV. Stable (sorted by id) so two runs over the same
        // content produce byte-identical files (easy to eyeball-diff).
        public static string Serialize(IDictionary<string, string> idToHash)
        {
            var ids = new List<string>(idToHash?.Keys ?? (ICollection<string>)Array.Empty<string>());
            ids.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder(ids.Count * 32);
            foreach (string id in ids)
            {
                sb.Append(id);
                sb.Append('\t');
                sb.Append(idToHash[id]);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // Parse the TSV produced by Serialize. Lines without a tab are skipped (robust to truncation).
        public static Dictionary<string, string> Parse(string tsv)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(tsv))
                return result;
            int i = 0;
            while (i < tsv.Length)
            {
                int nl = tsv.IndexOf('\n', i);
                if (nl < 0) nl = tsv.Length;
                int tab = tsv.IndexOf('\t', i);
                if (tab >= 0 && tab < nl)
                    result[tsv.Substring(i, tab - i)] = tsv.Substring(tab + 1, nl - tab - 1);
                i = nl + 1;
            }
            return result;
        }
    }
}
