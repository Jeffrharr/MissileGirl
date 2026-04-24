// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using MissileGirl;
using UnityEngine;
using Verse;

namespace Soyuz.Profiling
{
    public class PawnPerformanceModel : IPawnModel
    {
        public PawnPerformanceModel(string name) : base(name)
        {
            this.grapher.TimeWindowSize = 2;
        }

    }
}
