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
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using Verse;
namespace MissileGirl
{
    public class RocketShip
    {
        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
        public class SkipperPatch : Attribute
        {
            public MethodType methodType;
            public Type targetType;
            public Type[] genericsTypes;
            public Type[] methodArguments;
            public string targetMethod;
            public readonly Type[] modsCompatiblityHandlers;

            private MethodInfo method;
            private bool found;

            public SkipperPatch(Type type, string methodName, MethodType methodType = MethodType.Normal,
                Type[] methodArguments = null, Type[] genericsTypes = null, Type[] modsCompatiblityHandlers = null)
            {
                targetMethod = methodName;
                targetType = type;
                this.methodType = methodType;
                this.methodArguments = methodArguments;
                this.genericsTypes = genericsTypes;
                this.modsCompatiblityHandlers = modsCompatiblityHandlers;
            }

            public MethodInfo GetMethodInfo()
            {
                if (found) return method;
                if (methodType == MethodType.Constructor) throw new NotImplementedException();

                if (methodType == MethodType.Normal)
                {
                    MethodInfo m = AccessTools.Method(targetType, targetMethod, methodArguments, genericsTypes);
                    if (m != null) found = true;
                    method = m;
                    return m;
                }

                if (methodType == MethodType.Getter)
                {
                    MethodInfo m = AccessTools.PropertyGetter(targetType, targetMethod);
                    if (m != null) found = true;
                    method = m;
                    return m;
                }

                if (methodType == MethodType.Setter)
                {
                    MethodInfo m = AccessTools.PropertySetter(targetType, targetMethod);
                    if (m != null) found = true;
                    method = m;
                    return m;
                }

                throw new NotImplementedException();
            }
        }

        public class SkipperPatcher
        {
            private static readonly MethodInfo mTranspiler = AccessTools.Method("RocketPatcher:SkipperTranspiler");
            private static Type patchType;
            private static readonly object locker = new object();

            private readonly Harmony harmony;
            public string id;
            public List<MethodInfo> patchedMethods = new List<MethodInfo>();
            public Dictionary<MethodInfo, Type> patches = new Dictionary<MethodInfo, Type>();

            public SkipperPatcher(string id)
            {
                this.id = id;
                harmony = new Harmony(id + ".rocketpatch");
            }

            public void PatchAll()
            {
                IEnumerable<Type> types = GetSkipperPatchTypes();
                foreach (Type t in types)
                {
                    SkipperPatch patchInfo = t.TryGetAttribute<SkipperPatch>();
                    MethodBase method = patchInfo.GetMethodInfo();
                    if (method.IsValidTarget()) Patch(method as MethodInfo, t);
                    else Log.Warning($"MissileGirl: skipper patch target is not valid {method.GetMethodPath()}!");
                }
            }

            public void Patch(MethodInfo target, Type patchType)
            {
                lock (locker)
                {
                    try
                    {
                        SkipperPatcher.patchType = patchType;
                        harmony.Patch(target, transpiler: new HarmonyMethod(mTranspiler));
                        patchedMethods.Add(target);
                        patches.Add(target, patchType);
                        if (RocketDebugPrefs.Debug) Logger.Message(string.Format("MissileGirl: patched target {0}", target));
                    }
                    catch (Exception er)
                    {
                        Log.Error(string.Format("MissileGirl: error in patching {2} with {3} with error {0} at {1}",
                                                er.Message, er.StackTrace, target, patchType));
                    }
                }
            }

            public static IEnumerable<Type> GetSkipperPatchTypes()
            {
                IEnumerable<Type> types = RocketAssembliesInfo.Assemblies.SelectMany(x => x.GetLoadableTypes());
                foreach (Type type in types)
                    if (type.HasAttribute<SkipperPatch>())
                    {
                        if (RocketDebugPrefs.Debug) Logger.Message(string.Format("MissileGirl: found type {0} with skipper patch attributes", type));
                        yield return type;
                    }
            }

            [UsedImplicitly]
            private static IEnumerable<CodeInstruction> SkipperTranspiler(IEnumerable<CodeInstruction> instructions,
                ILGenerator generator, MethodBase original)
            {
                return SetupSkipping(instructions, generator, original,
                                     AccessTools.Method(patchType, "Skipper"),
                                     AccessTools.Method(patchType, "Setter"));
            }


            private static IEnumerable<CodeInstruction> SetupSkipping(IEnumerable<CodeInstruction> instructions,
                ILGenerator generator, MethodBase original, MethodBase skipper, MethodBase setter)
            {
                List<CodeInstruction> codes = instructions.ToList();
                Type returnType = (original as MethodInfo).ReturnType;

                LocalBuilder result = null;
                LocalBuilder state = null;

                if (returnType != typeof(void)) result = generator.DeclareLocal(returnType);

                if (skipper != null)
                {
                    if (TryGetStateType(skipper as MethodInfo, out Type stateType))
                        state = generator.DeclareLocal(stateType);

                    Label start = generator.DefineLabel();
                    if (returnType != typeof(void))
                        yield return new CodeInstruction(OpCodes.Ldloca_S, result.LocalIndex);
                    List<CodeInstruction> extras = CallInside(original, skipper, state).ToList();
                    foreach (CodeInstruction extra in extras)
                        yield return extra;

                    yield return new CodeInstruction(OpCodes.Brtrue_S, start);
                    if (returnType != typeof(void))
                        yield return new CodeInstruction(OpCodes.Ldloc_S, result.LocalIndex);
                    yield return new CodeInstruction(OpCodes.Ret);

                    codes[0].labels.Add(start);
                }

                for (int i = 0; i < codes.Count; i++)
                {
                    CodeInstruction code = codes[i];
                    if (code.opcode == OpCodes.Ret && setter != null)
                    {
                        if (returnType != typeof(void))
                        {
                            yield return new CodeInstruction(OpCodes.Stloc_S, result.LocalIndex);
                            yield return new CodeInstruction(OpCodes.Ldloca_S, result.LocalIndex);
                        }

                        List<CodeInstruction> extras = CallInside(original, setter, state).ToList();
                        extras[0].labels = code.labels;
                        foreach (CodeInstruction extra in extras) yield return extra;

                        if (returnType != typeof(void))
                            yield return new CodeInstruction(OpCodes.Ldloc_S, result.LocalIndex);
                        yield return new CodeInstruction(OpCodes.Ret);
                    }
                    else
                    {
                        yield return code;
                    }
                }
            }

            private static bool TryGetStateType(MethodInfo skipper, out Type stateType)
            {
                if (skipper == null)
                {
                    stateType = typeof(void);
                    return false;
                }

                ParameterInfo[] mParameters = skipper.GetParameters();
                for (int i = 0; i < mParameters.Length; i++)
                    if (mParameters[i].Name.ToLower() == "__state")
                    {
                        stateType = mParameters[i].ParameterType;
                        return true;
                    }

                stateType = typeof(void);
                return false;
            }

            private static IEnumerable<CodeInstruction> CallInside(MethodBase parent, MethodBase method,
                LocalBuilder state = null)
            {
                if (!method.IsStatic)
                    throw new InvalidOperationException(
                        string.Format("MissileGirl: can't use non static method {0} in a patch:CallInside", parent.Name));
                ParameterInfo[] mParameters = method.GetParameters();
                ParameterInfo[] pParameters = parent.GetParameters();

                int paramCounter = 0;
                if (!parent.IsStatic) paramCounter += 1;

                for (int i = 0; i < mParameters.Length; i++)
                {
                    ParameterInfo methodParam = mParameters[i];
                    if (methodParam.Name == "__instance")
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        continue;
                    }

                    if (methodParam.Name == "__state" && state != null)
                    {
                        yield return new CodeInstruction(OpCodes.Ldloca_S, state.LocalIndex);
                        continue;
                    }

                    for (int j = 0; j < pParameters.Length; j++)
                    {
                        ParameterInfo parentParam = pParameters[j];
                        if (methodParam.Name == parentParam.Name)
                        {
                            if (methodParam.ParameterType != parentParam.ParameterType &&
                                    !methodParam.ParameterType.IsByRef)
                                throw new InvalidOperationException(
                                    string.Format(
                                        "MissileGirl: error in patching:CallInside with method {0} with type mismatch {1}",
                                        parent.Name, methodParam.Name));
                            if (methodParam.ParameterType.IsByRef)
                                yield return new CodeInstruction(OpCodes.Ldarga_S, paramCounter);
                            else
                                yield return new CodeInstruction(OpCodes.Ldarg_S, paramCounter);
                            paramCounter++;
                        }
                    }
                }

                yield return new CodeInstruction(OpCodes.Call, method);
            }
        }
    }
}
