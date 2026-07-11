using System.Linq;
using System.Xml;
using Gagarin;
using NUnit.Framework;

namespace Gagarin.Tests
{
    [TestFixture]
    public class TopLevelDefsTests
    {
        private static XmlElement Root(string innerXml)
        {
            var doc = new XmlDocument();
            doc.LoadXml($"<Defs>{innerXml}</Defs>");
            return doc.DocumentElement;
        }

        [Test]
        public void Enumerate_NullRoot_ReturnsEmpty()
        {
            Assert.That(TopLevelDefs.Enumerate(null), Is.Empty);
        }

        [Test]
        public void Enumerate_ConcreteDef_YieldsIdAndElement()
        {
            var root = Root("<ThingDef><defName>Steel</defName></ThingDef>");

            var result = TopLevelDefs.Enumerate(root).ToList();

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Id, Is.EqualTo("ThingDef/Steel"));
            Assert.That(result[0].Element.Name, Is.EqualTo("ThingDef"));
        }

        [Test]
        public void Enumerate_AbstractNode_Skipped()
        {
            var root = Root("<ThingDef Name=\"BuildingBase\" Abstract=\"True\"></ThingDef>");

            Assert.That(TopLevelDefs.Enumerate(root), Is.Empty);
        }

        [Test]
        public void Enumerate_TextAndCommentChildren_Skipped()
        {
            var root = Root("text-node<!--comment--><ThingDef><defName>Steel</defName></ThingDef>");

            var result = TopLevelDefs.Enumerate(root).ToList();

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Id, Is.EqualTo("ThingDef/Steel"));
        }

        [Test]
        public void Enumerate_NamespacedElementName_UsesElementNameNotTypeName()
        {
            var root = Root("<JoofTest.PropDef><defName>TC_P1_Prop</defName></JoofTest.PropDef>");

            var result = TopLevelDefs.Enumerate(root).ToList();

            Assert.That(result[0].Id, Is.EqualTo("JoofTest.PropDef/TC_P1_Prop"));
        }
    }
}
