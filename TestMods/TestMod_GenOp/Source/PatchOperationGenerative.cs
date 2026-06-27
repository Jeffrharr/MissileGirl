// A custom PatchOperation for the apply-time-attribution live fixture.
//
// At Apply time it NEWS UP a PatchOperationAdd and applies it — a child op that was never present in
// the static patch tree, i.e. exactly the real-world "unindexed" case (an op generated dynamically
// during a parent op's Apply, which no index-time field walk can see). Provenance capture must
// attribute that generated child to THIS enclosing op via the apply-stack
// ("joof.testharness.genop#N.generated[0]" with this mod's sourceMod), not the collapsed "unindexed#"
// bucket. The child's xpath/value are copied from this op's own XML-loaded fields via reflection
// (the real fields are private on the actual RimWorld assembly at runtime).
using System.Reflection;
using System.Xml;
using Verse;

namespace JoofTest
{
    public class PatchOperationGenerative : PatchOperationPathed
    {
        // Loaded from <value>...</value> in XML, exactly like PatchOperationAdd.value. Assigned by
        // RimWorld's DirectXmlToObject via reflection, never in code.
#pragma warning disable 0649
        private XmlContainer value;
#pragma warning restore 0649

        protected override bool ApplyWorker(XmlDocument xml)
        {
            var add = new PatchOperationAdd();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            // Copy our xpath (PatchOperationPathed.xpath) into the generated child.
            FieldInfo xf = typeof(PatchOperationPathed).GetField("xpath", F);
            xf.SetValue(add, xf.GetValue(this));
            // Copy our value (XmlContainer) into the generated child's value field.
            FieldInfo vf = typeof(PatchOperationAdd).GetField("value", F);
            vf.SetValue(add, value);
            // Apply the generated child — it goes through PatchOperation.Apply (hooked), so capture
            // observes it as a dynamic op with no static id and attributes it to us.
            return add.Apply(xml);
        }
    }
}
