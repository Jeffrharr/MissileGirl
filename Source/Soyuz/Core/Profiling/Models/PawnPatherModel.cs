using System;
using Verse;

namespace Soyuz.Profiling
{
    public class PawnPatherModel : IPawnModel
    {
        public PawnPatherModel(string name) : base(name)
        {
            this.grapher.TimeWindowSize = 2500;
        }

    }
}
