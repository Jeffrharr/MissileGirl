// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
namespace MissileGirl
{
    public static class FieldsUtility
    {
        public static IEnumerable<FieldInfo> GetFields<T>() where T : Attribute
        {
            foreach (FieldInfo field in RocketAssembliesInfo.Assemblies
                    .Where(ass => !ass.FullName.Contains("System") && !ass.FullName.Contains("VideoTool"))
                    .SelectMany(a => a.GetTypes())
                    .Where(t => !t.IsAbstract && !t.IsInterface)
                    .SelectMany(t => t.GetFields())
                    .Where(f => f.HasAttribute<T>() && f.IsStatic)
                    .ToArray())
            {
                if (Prefs.DevMode && RocketDebugPrefs.Debug)
                    Logger.Message(string.Format("MissileGirl: Found <color=yellow>settings fields</color> with {0}, {1}:{2}", typeof(T).Name,
                                                             field.DeclaringType.Name, field.Name));
                yield return field;
            }
        }
    }
}
