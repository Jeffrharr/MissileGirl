// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// AssemblyOwnerLookup.cs (Piece A — provenance capture, issue #86)
//
// Contains: a static Assembly -> packageIds map, built once per capture session from
// LoadedModManager.RunningModsListForReading.
//
// Used for: RegisterNode, to decide whether a def's .NET Type was supplied by a mod's own
// C# assembly (a custom Def type) rather than the base game. That distinction is what lets
// a later mod-list change dirty every instance of a removed mod's custom Def type, even in
// OTHER mods' unchanged XML (see ProvenanceGraph's typeProviders index and
// DirtySetComputer's Seed 9).
//
// Why a separate lookup rather than checking def.GetType().Assembly directly: vanilla
// Def subclasses all live in Assembly-CSharp (or Unity/BCL assemblies), never in any mod's
// ModAssemblyHandler.loadedAssemblies -- so a miss here IS the "this is a base-game type"
// signal, with no extra filtering needed.
//
// Why a list, not a single packageId: ModAssemblyHandler.ReloadAll loads each mod's DLL via
// Assembly.LoadFrom, and .NET/Mono's LoadFrom binding can resolve two mods that ship a
// byte-identical (or identity-matching) DLL to the SAME Assembly object -- e.g. two mods
// bundling a copy of a shared library. If we recorded only the load-order-last mod, removing
// THAT mod while the other survivor still supplies the identical assembly would wrongly drop
// the def from the recomputed cache (TypeProviderGate would see its one recorded provider
// gone and never learn a second provider is still active). Recording every candidate, in load
// order, lets TypeProviderGate.Passes ask "is ANY of them still active" instead.

using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace Gagarin
{
    internal static class AssemblyOwnerLookup
    {
        private static Dictionary<Assembly, List<string>> map;

        // Assemblies don't change mid-load; the map is rebuilt lazily on first use after
        // each Reset (called alongside the rest of ProvenanceRecorder's per-session state).
        internal static void Reset()
        {
            map = null;
        }

        // Returns every packageId whose loadedAssemblies contains this Assembly object, in
        // load order. Empty (never null) when the assembly isn't owned by any mod (the
        // base-game/vanilla case).
        internal static List<string> PackageIdsFor(Assembly assembly)
        {
            if (assembly == null)
                return EmptyIds;
            if (map == null)
                map = Build();
            return map.TryGetValue(assembly, out List<string> packageIds) ? packageIds : EmptyIds;
        }

        private static readonly List<string> EmptyIds = new List<string>();

        private static Dictionary<Assembly, List<string>> Build()
        {
            var result = new Dictionary<Assembly, List<string>>();
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            if (mods == null)
                return result;
            foreach (ModContentPack mod in mods)
            {
                List<Assembly> loaded = mod?.assemblies?.loadedAssemblies;
                if (loaded == null || string.IsNullOrEmpty(mod.PackageId))
                    continue;
                foreach (Assembly assembly in loaded)
                {
                    if (assembly == null)
                        continue;
                    if (!result.TryGetValue(assembly, out List<string> owners))
                    {
                        owners = new List<string>();
                        result[assembly] = owners;
                    }
                    owners.Add(mod.PackageId);
                }
            }
            return result;
        }
    }
}
