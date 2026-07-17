// A minimal custom Def subclass for the type-provider live test fixture (issue #86).
//
// The point is purely that this .NET Type is supplied by THIS mod's assembly, not the base game
// and not TestMod_TypeConsumer (which authors the XML instances). When this mod is removed from
// the load, the Type disappears with it, so RimWorld can no longer construct <JoofTest.GadgetDef>
// anywhere -- including TestMod_TypeConsumer's own unchanged, unpatched XML. That is the real
// oppey.eyegenes2 / nals.facialanimation shape (FacialAnimation.EyeballColorDef), reproduced here
// with a throwaway type and mod pair.
using Verse;

namespace JoofTest
{
    public class GadgetDef : Def
    {
    }
}
