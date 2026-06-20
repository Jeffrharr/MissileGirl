// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchIdWalkerTests.cs (Piece A — provenance capture)
//
// Contains: NUnit fixture exercising PatchIdWalker's pure recursion against a
// synthetic node tree — hierarchical id construction for list children and named
// children, the first-visit-wins cycle guard, and id stability across runs.
//
// Used for: offline verification of the load-bearing id-assignment logic that
// IndexPatches relies on, without RimWorld. The thin RimWorld-specific reflection
// that produces (label, child) pairs from real PatchOperation fields is validated
// in-game, not here.
//
// Why: collapsing every nested operation into one "unindexed#{type}" bucket was
// the bug this walker fixes; these tests prove distinct nested ops get distinct,
// stable ids so RecordPatch keeps them separate and correctly attributed.

using System.Collections.Generic;

namespace Gagarin.Tests
{
    [TestFixture]
    public class PatchIdWalkerTests
    {
        // A synthetic stand-in for PatchOperation: it can hold an ordered list of
        // children (like PatchOperationSequence.operations) and named match/nomatch
        // children (like PatchOperationConditional). The walker is generic, so it
        // never sees this type — it only sees the (label, child) pairs we yield.
        private sealed class Node
        {
            public readonly string name;
            public readonly List<Node> operations = new List<Node>();
            public Node match;
            public Node nomatch;

            public Node(string name) => this.name = name;
        }

        // Mirrors GetChildPatches' labelling scheme: list children get
        // ".operations[i]", named children get ".match" / ".nomatch".
        private static IEnumerable<(string label, Node child)> GetChildren(Node n)
        {
            for (int i = 0; i < n.operations.Count; i++)
                yield return ($".operations[{i}]", n.operations[i]);
            if (n.match != null)
                yield return (".match", n.match);
            if (n.nomatch != null)
                yield return (".nomatch", n.nomatch);
        }

        private static Dictionary<Node, string> Walk(Node root, string rootId)
        {
            var ids = new Dictionary<Node, string>();
            PatchIdWalker.AssignIds(root, rootId, GetChildren, ids);
            return ids;
        }

        [Test]
        public void AssignIds_RootGetsRootId()
        {
            Node root = new Node("root");
            Dictionary<Node, string> ids = Walk(root, "mod#3");

            Assert.That(ids[root], Is.EqualTo("mod#3"));
        }

        [Test]
        public void AssignIds_SequenceChildren_GetDistinctIndexedIds()
        {
            Node root = new Node("seq");
            Node a = new Node("a");
            Node b = new Node("b");
            Node c = new Node("c");
            root.operations.Add(a);
            root.operations.Add(b);
            root.operations.Add(c);

            Dictionary<Node, string> ids = Walk(root, "mod#3");

            // Each child gets its own positional id; none collapse together.
            Assert.That(ids[a], Is.EqualTo("mod#3.operations[0]"));
            Assert.That(ids[b], Is.EqualTo("mod#3.operations[1]"));
            Assert.That(ids[c], Is.EqualTo("mod#3.operations[2]"));
        }

        [Test]
        public void AssignIds_ConditionalChildren_GetMatchAndNomatchIds()
        {
            Node root = new Node("cond");
            Node yes = new Node("yes");
            Node no = new Node("no");
            root.match = yes;
            root.nomatch = no;

            Dictionary<Node, string> ids = Walk(root, "mod#7");

            Assert.That(ids[yes], Is.EqualTo("mod#7.match"));
            Assert.That(ids[no], Is.EqualTo("mod#7.nomatch"));
        }

        [Test]
        public void AssignIds_NestedContainers_BuildHierarchicalPaths()
        {
            // A sequence whose second child is itself a conditional: ids must
            // compose along the full path.
            Node root = new Node("seq");
            Node first = new Node("first");
            Node cond = new Node("cond");
            Node deep = new Node("deep");
            root.operations.Add(first);
            root.operations.Add(cond);
            cond.match = deep;

            Dictionary<Node, string> ids = Walk(root, "mod#0");

            Assert.That(ids[first], Is.EqualTo("mod#0.operations[0]"));
            Assert.That(ids[cond], Is.EqualTo("mod#0.operations[1]"));
            Assert.That(ids[deep], Is.EqualTo("mod#0.operations[1].match"));
        }

        [Test]
        public void AssignIds_SharedChild_VisitedExactlyOnce_FirstVisitWins()
        {
            // The same node reachable via two paths (a DAG) must be assigned once;
            // the first path encountered during the walk wins.
            Node root = new Node("seq");
            Node shared = new Node("shared");
            root.operations.Add(shared); // first path: .operations[0]
            root.match = shared;          // second path: .match (should be ignored)

            Dictionary<Node, string> ids = Walk(root, "mod#1");

            Assert.That(ids.Count, Is.EqualTo(2)); // root + shared, no duplicate
            Assert.That(ids[shared], Is.EqualTo("mod#1.operations[0]"));
        }

        [Test]
        public void AssignIds_Cycle_TerminatesAndAssignsEachOnce()
        {
            // A cycle (child points back at an ancestor) must not loop forever; the
            // guard means the back-edge is simply skipped.
            Node root = new Node("seq");
            Node child = new Node("child");
            root.operations.Add(child);
            child.operations.Add(root); // back-edge

            Dictionary<Node, string> ids = Walk(root, "mod#2");

            Assert.That(ids.Count, Is.EqualTo(2));
            Assert.That(ids[root], Is.EqualTo("mod#2"));
            Assert.That(ids[child], Is.EqualTo("mod#2.operations[0]"));
        }

        [Test]
        public void AssignIds_IsDeterministic_AcrossRepeatedRuns()
        {
            // Stable ids across runs are required for the graph to be comparable
            // run-to-run. Build the same tree twice and assert identical id sets.
            static Node BuildTree()
            {
                Node r = new Node("seq");
                Node x = new Node("x");
                Node y = new Node("y");
                r.operations.Add(x);
                r.operations.Add(y);
                y.match = new Node("ym");
                return r;
            }

            Dictionary<Node, string> first = Walk(BuildTree(), "mod#5");
            Dictionary<Node, string> second = Walk(BuildTree(), "mod#5");

            // Compare by the structural label (Node identity differs per build),
            // which is the user-visible, run-to-run-stable part of each id.
            List<string> firstIds = new List<string>(first.Values);
            List<string> secondIds = new List<string>(second.Values);
            firstIds.Sort();
            secondIds.Sort();
            Assert.That(firstIds, Is.EqualTo(secondIds));
            Assert.That(firstIds, Is.EquivalentTo(new[]
            {
                "mod#5",
                "mod#5.operations[0]",
                "mod#5.operations[1]",
                "mod#5.operations[1].match",
            }));
        }

        [Test]
        public void AssignIds_PreExistingRoot_NotReassigned()
        {
            // If the root already has an id (e.g. it appeared as another op's child
            // first), the walk must not overwrite it.
            Node root = new Node("root");
            var ids = new Dictionary<Node, string> { [root] = "existing#9" };

            PatchIdWalker.AssignIds(root, "mod#0", GetChildren, ids);

            Assert.That(ids[root], Is.EqualTo("existing#9"));
        }

        [Test]
        public void AssignIds_NullRoot_NoOp()
        {
            var ids = new Dictionary<Node, string>();
            PatchIdWalker.AssignIds<Node>(null, "mod#0", GetChildren, ids);
            Assert.That(ids, Is.Empty);
        }
    }
}
