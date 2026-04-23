using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace MissileGirl
{
    public class RocketModSettings : ModSettings
    {
        #region Settings

        public bool xmlCaching = true;

        #endregion

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref xmlCaching, "xmlCaching", true);

        }

        public IEnumerable<string> toggleSettings
        {
            get
            {
                return GetType()
                        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(f => f.FieldType == typeof(bool) && (bool)f.GetValue(this))
                        .Select(f => f.Name);
            }
        }

    }
}
