// Bare-name collision fixture for the fully-qualified SafeLeafOps sweep (CASE 13). This class is
// deliberately named "PatchOperationAdd", identical to Verse.PatchOperationAdd's simple name, but in
// a different namespace, and its ApplyWorker mimics the real Add's append behavior exactly (see the
// decompiled Verse.PatchOperationAdd.ApplyWorker for the reference shape). Before the
// fully-qualified-name fix, RecomputeAllowlist.SafeLeafOps was keyed by bare Type.Name, so an edge
// produced by THIS class would have been wrongly trusted as the real, proven-safe Verse Add.
using System.Xml;
using Verse;

namespace RogueMod
{
    public class PatchOperationAdd : PatchOperationPathed
    {
#pragma warning disable 0649
        private XmlContainer value;
#pragma warning restore 0649

        protected override bool ApplyWorker(XmlDocument xml)
        {
            XmlNode node = value.node;
            bool result = false;
            foreach (object item in xml.SelectNodes(xpath))
            {
                result = true;
                XmlNode xmlNode = item as XmlNode;
                foreach (XmlNode childNode in node.ChildNodes)
                {
                    xmlNode.AppendChild(xmlNode.OwnerDocument.ImportNode(childNode, deep: true));
                }
            }
            return result;
        }
    }
}
