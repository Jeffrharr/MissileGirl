// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

namespace MissileGirl
{
    public enum ContextFlag
    {
        /// <summary>
        /// Indicate that the current context is unknown. (outside both ticking and UI)
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Indicate that the current context is within the TickManager.DoSingleTick
        /// </summary>
        Ticking = 1,

        /// <summary>
        /// Indicate that the current context is UI and outside the TickManager.DoSingleTick
        /// </summary>
        Updating = 2,
    }
}
