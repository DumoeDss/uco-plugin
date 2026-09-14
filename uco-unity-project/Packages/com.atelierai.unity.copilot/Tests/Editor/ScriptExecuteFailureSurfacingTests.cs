#nullable enable
using System;
using com.AtelierAI.Unity.Copilot.Editor.API;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// COCli-04 pin: script-execute failures must surface as error results
    /// carrying the Roslyn diagnostics or the inner exception type, message,
    /// and stack — never as a generic success envelope.
    /// </summary>
    public sealed class ScriptExecuteFailureSurfacingTests
    {
        [Test]
        public void CompileFailure_SurfacesRoslynDiagnostics()
        {
            var ex = Assert.Throws<Exception>(() => _ = Tool_Script.Execute(
                csharpCode: "public class Broken { public static void Main() { this is not csharp } }",
                className: "Broken",
                methodName: "Main"));

            Assert.That(ex!.Message, Does.Contain("Compilation failed"));
            // Roslyn diagnostic ids and the fix-first guidance ride the message
            // so the envelope keeps them after the bridge forwards the error.
            Assert.That(ex.Message, Does.Match(@"CS\d{4}"));
        }

        [Test]
        public void RuntimeFailure_SurfacesTargetInvocationExceptionWithTypeMessageAndStack()
        {
            var ex = Assert.Throws<Exception>(() => _ = Tool_Script.Execute(
                csharpCode: "throw new System.InvalidOperationException(\"probe exploded\");",
                className: "Script",
                methodName: "Main",
                isMethodBody: true));

            Assert.That(ex!.Message, Does.Contain("TargetInvocationException"));
            Assert.That(ex.Message, Does.Contain("System.InvalidOperationException"));
            Assert.That(ex.Message, Does.Contain("probe exploded"));
            // The stack of the inner exception is preserved for diagnosis
            // (the dynamic method's frame, not the calling test's frame).
            Assert.That(ex.Message, Does.Contain("at Script.Main"));
        }

        [Test]
        public void BodyOnlyMode_MissingReturnIsADiagnosableCompileError()
        {
            var ex = Assert.Throws<Exception>(() => _ = Tool_Script.Execute(
                csharpCode: "var x = 1;",
                className: "Script",
                methodName: "Main",
                isMethodBody: true,
                returnType: "int"));

            Assert.That(ex!.Message, Does.Contain("Compilation failed"));
            Assert.That(ex.Message, Does.Match(@"CS0161"));
        }
    }
}
