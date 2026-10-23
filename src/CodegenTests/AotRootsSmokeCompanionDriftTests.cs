using System;
using System.IO;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using Shouldly;
using Xunit;

namespace CodegenTests;

/// <summary>
/// jasperfx#777: src/JasperFx.AotRootsSmoke carries a checked-in AOT roots companion, which the
/// smoke-test-aot-roots gate publishes under Native AOT. This keeps it equal to what
/// <see cref="AotRootsCompanionExtensions.AddAotRoots(GeneratedAssembly, string, System.Collections.Generic.IEnumerable{string})" /> emits today, so
/// the gate always proves the current emitter rather than a stale copy of it. Regenerate with
/// JASPERFX_UPDATE_AOT_ROOTS_SMOKE=true.
/// </summary>
public class AotRootsSmokeCompanionDriftTests
{
    private static string expected()
    {
        var assembly = new GeneratedAssembly(new GenerationRules("Internal.Generated"), "Internal.Generated.Widgets");
        assembly.AddAotRoots("AotRoots", new[] { "Internal.Generated.Widgets.WidgetMaker" });

        // The negative control compiles the companion out with -p:AotRootsDisabled=true
        return "#if !AOT_ROOTS_DISABLED\n" + assembly.GenerateCode() + "\n#endif\n";
    }

    private static string companionPath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "JasperFx.AotRootsSmoke", "Internal", "Generated",
            "AotRoots.cs");

    private static string normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    [Fact]
    public void the_checked_in_companion_matches_the_emitter()
    {
        if (Environment.GetEnvironmentVariable("JASPERFX_UPDATE_AOT_ROOTS_SMOKE") == "true")
        {
            File.WriteAllText(companionPath(), expected());
            return;
        }

        normalize(File.ReadAllText(companionPath())).ShouldBe(normalize(expected()),
            "src/JasperFx.AotRootsSmoke/Internal/Generated/AotRoots.cs has drifted from what AddAotRoots emits. Rerun with JASPERFX_UPDATE_AOT_ROOTS_SMOKE=true and commit the file.");
    }
}
