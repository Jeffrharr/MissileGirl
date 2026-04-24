// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using HarmonyLib;

namespace MissileGirl
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public abstract class IPatch : Attribute
    {
        public string targetMethod;
        public Type targetType;
        public Type[] parameters = null;
        public Type[] generics = null;
        public MethodType methodType;

        public readonly PatchType patchType;

        public IPatch()
        {
            this.patchType = PatchType.empty;
        }

        public IPatch(Type targetType, string targetMethod = null, MethodType methodType = MethodType.Normal, Type[] parameters = null, Type[] generics = null)
        {
            this.patchType = PatchType.normal;
            this.targetType = targetType;
            this.targetMethod = targetMethod;
            this.methodType = methodType;
            this.parameters = parameters;
            this.generics = generics;
        }
    }
}
