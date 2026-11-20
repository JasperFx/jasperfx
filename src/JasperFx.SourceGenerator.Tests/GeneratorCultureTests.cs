using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

// jasperfx#1248: the generators run on the build machine, so a build on a tr-TR box lower-cased
// the I in IsEnabledFlag or InitCommand to a dotless ı and baked that into the generated code,
// which then never matched the runtime's invariant lookup (#1245).
public class GeneratorCultureTests
{
    private const string Source = """
        using JasperFx.CommandLine;

        namespace App;

        public class InitInput
        {
            public bool IsEnabledFlag { get; set; }
            public string InputFileFlag { get; set; } = "";
        }

        public class InitCommand : JasperFxCommand<InitInput>
        {
            public override bool Execute(InitInput input) => true;
        }
        """;

    // tr-TR lower-cases I to the dotless ı
    private static T inTurkish<T>(Func<T> func)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            return func();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static CSharpCompilation compile()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))
            .Append(MetadataReference.CreateFromFile(typeof(IJasperFxExtension).Assembly.Location))
            .ToList();

        return CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    [Fact]
    public void generated_flag_aliases_do_not_depend_on_the_build_machine_culture()
    {
        var parser = inTurkish(() =>
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new InputParserGenerator());
            driver = driver.RunGenerators(compile());

            return driver.GetRunResult().GeneratedTrees
                .Select(t => t.GetText().ToString())
                .Single(t => t.Contains("class InitInputParser"));
        });

        parser.ShouldContain("LongForm = \"--is-enabled\", ShortForm = \"-i\"");
        parser.ShouldContain("LongForm = \"--input-file\", ShortForm = \"-i\"");
        parser.ShouldNotContain("ı");
    }

    [Fact]
    public void discovered_command_names_do_not_depend_on_the_build_machine_culture()
    {
        var commandNames = inTurkish(() =>
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create([new CommandDiscoveryGenerator().AsSourceGenerator()],
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true));
            driver = driver.RunGenerators(compile());

            // The command name is not written into the manifest, so read it off the pipeline's
            // CommandInfo values instead
            var values = new List<object>();
            var seen = new HashSet<IncrementalGeneratorRunStep>();
            foreach (var step in driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(x => x.Value))
            {
                collect(step, values, seen);
            }

            return values
                .Where(x => x.GetType().Name == "CommandInfo")
                .Select(x => (string)x.GetType().GetProperty("CommandName")!.GetValue(x)!)
                .Distinct()
                .ToArray();
        });

        commandNames.ShouldBe(["init"]);
    }

    private static void collect(IncrementalGeneratorRunStep step, List<object> values,
        HashSet<IncrementalGeneratorRunStep> seen)
    {
        if (!seen.Add(step)) return;

        foreach (var (value, _) in step.Outputs) flatten(value, values);
        foreach (var (source, _) in step.Inputs) collect(source, values, seen);
    }

    private static void flatten(object? value, List<object> values)
    {
        switch (value)
        {
            case null:
                return;
            case string:
                values.Add(value);
                return;
            case ITuple tuple:
                for (var i = 0; i < tuple.Length; i++) flatten(tuple[i], values);
                return;
            case IEnumerable enumerable:
                foreach (var item in enumerable) flatten(item, values);
                return;
            default:
                values.Add(value);
                return;
        }
    }
}
