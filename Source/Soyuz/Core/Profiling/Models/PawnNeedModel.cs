// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MissileGirl;
using UnityEngine;
using Verse;

namespace Soyuz.Profiling
{
    public class PawnNeedModel : IPawnModel
    {
        public PawnNeedModel(string name) : base(name)
        {
            this.grapher.TimeWindowSize = 18000;
        }
    }
}
