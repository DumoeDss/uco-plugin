/*
 * Portions of this file are derived from MCP for Unity (CoplayDev/unity-mcp),
 * Copyright (c) Coplay Inc., licensed under the MIT License.
 * Original: https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/unity_docs.py
 */

/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using com.AtelierAI.Unity.Copilot.Editor.API;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// Offline unit coverage for the docs-* tools:
    /// - Version extraction
    /// - ScriptReference / Manual / Package URL construction
    /// - HTML parser smoke fixtures (ScriptReference + Manual)
    ///
    /// No network is touched. Reach into <see cref="Tool_Docs"/> via InternalsVisibleTo.
    /// </summary>
    public class DocsUrlAndParserTests
    {
        // ---------------- Version extraction ----------------

        [Test]
        public void ExtractVersion_Strips_Suffix_From_Patch()
        {
            Assert.AreEqual("6000.0", Tool_Docs.HtmlParser.ExtractVersion("6000.0.38f1"));
            Assert.AreEqual("2022.3", Tool_Docs.HtmlParser.ExtractVersion("2022.3.45f1"));
            Assert.AreEqual("6000.1", Tool_Docs.HtmlParser.ExtractVersion("6000.1.0b2"));
        }

        [Test]
        public void ExtractVersion_Returns_Null_For_Empty_Or_Null()
        {
            Assert.IsNull(Tool_Docs.HtmlParser.ExtractVersion(null));
            Assert.IsNull(Tool_Docs.HtmlParser.ExtractVersion(""));
            Assert.IsNull(Tool_Docs.HtmlParser.ExtractVersion("   "));
        }

        [Test]
        public void ExtractVersion_Passthrough_When_Already_Short()
        {
            Assert.AreEqual("6000", Tool_Docs.HtmlParser.ExtractVersion("6000"));
        }

        // ---------------- URL builders ----------------

        [Test]
        public void ScriptRefUrl_Versioned_Class_Only()
        {
            Assert.AreEqual(
                "https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.html",
                Tool_Docs.HtmlParser.BuildScriptRefUrl("Physics", null, "6000.0"));
        }

        [Test]
        public void ScriptRefUrl_Versioned_With_Member_Uses_Dot()
        {
            Assert.AreEqual(
                "https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.Raycast.html",
                Tool_Docs.HtmlParser.BuildScriptRefUrl("Physics", "Raycast", "6000.0"));
        }

        [Test]
        public void ScriptRefUrl_Unversioned()
        {
            Assert.AreEqual(
                "https://docs.unity3d.com/ScriptReference/Transform.html",
                Tool_Docs.HtmlParser.BuildScriptRefUrl("Transform", null, null));
        }

        [Test]
        public void PropertyUrl_Uses_Dash_Separator()
        {
            Assert.AreEqual(
                "https://docs.unity3d.com/ScriptReference/Transform-position.html",
                Tool_Docs.HtmlParser.BuildPropertyUrl("Transform", "position", null));
            Assert.AreEqual(
                "https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Transform-position.html",
                Tool_Docs.HtmlParser.BuildPropertyUrl("Transform", "position", "6000.0"));
        }

        [Test]
        public void ManualUrl_Versioned_And_Unversioned()
        {
            Assert.AreEqual(
                "https://docs.unity3d.com/Manual/execution-order.html",
                Tool_Docs.HtmlParser.BuildManualUrl("execution-order", null));
            Assert.AreEqual(
                "https://docs.unity3d.com/6000.0/Documentation/Manual/execution-order.html",
                Tool_Docs.HtmlParser.BuildManualUrl("execution-order", "6000.0"));
        }

        [Test]
        public void PackageUrl_Encodes_Package_Version()
        {
            Assert.AreEqual(
                "https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/2d-index.html",
                Tool_Docs.HtmlParser.BuildPackageUrl("com.unity.render-pipelines.universal", "2d-index", "17.0"));
        }

        // ---------------- HTML parser smoke fixtures ----------------

        private const string ScriptRefFixture = @"
<html><body>
<div class='content'>
  <h1 class='heading'>Physics.Raycast</h1>
  <div class='subsection'>
    <div class='signature-CS'>
      Declaration
      public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance);
    </div>
    <h3>Parameters</h3>
    <table class='list'>
      <tr><td class='name lbl'>origin</td><td class='desc'>The starting point of the ray in world coordinates.</td></tr>
      <tr><td class='name lbl'>direction</td><td class='desc'>The direction of the ray.</td></tr>
      <tr><td class='name lbl'>maxDistance</td><td class='desc'>The max distance the ray should check.</td></tr>
    </table>
    <h3>Returns</h3>
    <p>true if the ray intersects with a Collider, otherwise false.</p>
    <h3>Description</h3>
    <p>Casts a ray, from point origin, in direction direction.</p>
  </div>
  <pre class='codeExampleCS'>using UnityEngine; public class Example { void Update() { Physics.Raycast(transform.position, Vector3.forward); } }</pre>
</div>
</body></html>";

        [Test]
        public void ParseScriptReference_Extracts_Description_Signature_Params_Returns_Example()
        {
            var parsed = Tool_Docs.HtmlParser.ParseScriptReference(ScriptRefFixture);

            StringAssert.Contains("Casts a ray", parsed.Description);
            Assert.IsTrue(parsed.Signatures.Length >= 1, "expected at least one signature");
            StringAssert.Contains("Raycast(Vector3 origin", parsed.Signatures[0]);
            Assert.AreEqual(3, parsed.Parameters.Length);
            Assert.AreEqual("origin", parsed.Parameters[0].Name);
            StringAssert.Contains("starting point", parsed.Parameters[0].Description);
            StringAssert.Contains("true if the ray", parsed.Returns);
            Assert.AreEqual(1, parsed.Examples.Length);
            StringAssert.Contains("Physics.Raycast", parsed.Examples[0]);
        }

        private const string ManualFixture = @"
<html><body>
<h1>Execution Order of Event Functions</h1>
<p>This page describes the lifecycle of a MonoBehaviour.</p>
<h2>Initialization</h2>
<p>Awake is called when the script instance is being loaded.</p>
<p>Start is called before the first frame update.</p>
<h2>Update loop</h2>
<p>Update is called once per frame.</p>
<pre>void Update() { transform.position += Vector3.forward * Time.deltaTime; }</pre>
</body></html>";

        [Test]
        public void ParseManualPage_Extracts_Title_Sections_And_Code_Example()
        {
            var parsed = Tool_Docs.HtmlParser.ParseManualPage(ManualFixture);

            Assert.AreEqual("Execution Order of Event Functions", parsed.Title);
            // 3 sections: Introduction (the lead-in p before any h2), Initialization, Update loop.
            Assert.GreaterOrEqual(parsed.Sections.Length, 2, "expected at least 2 sections");
            var initSection = System.Array.Find(parsed.Sections, s => s.Heading == "Initialization");
            Assert.IsNotNull(initSection, "Initialization section missing");
            StringAssert.Contains("Awake is called", initSection!.Content);
            StringAssert.Contains("Start is called", initSection.Content);
            var updateSection = System.Array.Find(parsed.Sections, s => s.Heading == "Update loop");
            Assert.IsNotNull(updateSection);
            StringAssert.Contains("Update is called once per frame", updateSection!.Content);
            Assert.AreEqual(1, parsed.CodeExamples.Length);
            StringAssert.Contains("Time.deltaTime", parsed.CodeExamples[0]);
        }

        [Test]
        public void ParseManualPage_Handles_Empty_Html()
        {
            var parsed = Tool_Docs.HtmlParser.ParseManualPage(string.Empty);
            Assert.AreEqual(string.Empty, parsed.Title);
            Assert.AreEqual(0, parsed.Sections.Length);
            Assert.AreEqual(0, parsed.CodeExamples.Length);
        }
    }
}
