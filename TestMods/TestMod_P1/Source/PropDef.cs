// A minimal custom Def subclass for the P1 live test fixture.
//
// The point is purely the TYPE NAME vs ELEMENT NAME divergence: instances are authored in XML as
// <JoofTest.PropDef> (the fully-qualified element name RimWorld resolves via GenTypes), whose simple
// type name is "PropDef". Provenance capture must key the node by the element name ("JoofTest.PropDef")
// so the def is dirtiable when its file changes; the pre-P1 code keyed it by GetType().Name ("PropDef")
// and the dirty set never matched the gate/Unified id. The p1Tag field just gives the def a value the
// harness can flip between Run A and Run B to force a real change.
using Verse;

namespace JoofTest
{
    public class PropDef : Def
    {
        public string p1Tag;
    }
}
