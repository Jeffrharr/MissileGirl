// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using RimWorld.Planet;
using UnityEngine;
using Verse;
namespace MissileGirl
{
    public class WorldInfoComponent : WorldComponent
    {
        private int initialMapHeight;

        private int initialMapWidth;

        public bool useCustomMapSizes;

        public Vector3 IntialMapSize
        {
            get
            {
                Vector3 vector = new Vector3();
                vector.y = world.info.initialMapSize.y;
                if (!useCustomMapSizes)
                {
                    vector.x = world.info.initialMapSize.x;
                    vector.z = world.info.initialMapSize.z;
                }
                else
                {
                    vector.x = initialMapWidth;
                    vector.z = initialMapHeight;
                }
                return vector;
            }
        }

        public int InitialMapHeight
        {
            get => !useCustomMapSizes ? initialMapHeight = Find.World.info.initialMapSize.z : initialMapHeight;
            set => initialMapHeight = value;
        }

        public int InitialMapWidth
        {
            get => !useCustomMapSizes ? initialMapWidth = Find.World.info.initialMapSize.x : initialMapWidth;
            set => initialMapWidth = value;
        }

        public WorldInfoComponent(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref useCustomMapSizes, "useCustomMapSizes");
            Scribe_Values.Look(ref initialMapWidth, "initialMapWidth", 250);
            Scribe_Values.Look(ref initialMapHeight, "initialMapHeight", 250);
        }
    }
}
