// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using Verse;

namespace Gagarin
{
    public static class LoadableXmlAssetUtility
    {
        public static string GetLoadableId(this LoadableXmlAsset loadable) => loadable.FullFilePath;
    }
}
