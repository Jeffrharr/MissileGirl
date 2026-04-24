using System;
using System.Collections.Generic;
//using System.Security.Cryptography.Xml;
using MissileGirl;
using UnityEngine;
using Verse;

namespace Soyuz
{
    public abstract class IPawnModel
    {
        public Grapher grapher;

        public List<Tuple<float, float, bool>> queue = new List<Tuple<float, float, bool>>();

        public IPawnModel(string name)
        {
            this.grapher = new Grapher(name.CapitalizeFirst());
        }

    }
}
