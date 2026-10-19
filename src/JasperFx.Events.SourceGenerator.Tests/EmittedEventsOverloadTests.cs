using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// jasperfx#1021 — the manifest keyed a handler method by name only, so every overload of one name was
/// credited with the union of their events. Wolverine's most common handler shape is exactly that: one
/// static class with a <c>Handle</c> overload per command. Each overload now gets its own entry, told
/// apart by <c>ParameterTypes</c>, unless one of its parameter types cannot be named from assembly scope.
/// </summary>
public class EmittedEventsOverloadTests
{
    private const string ManifestFileName = "JasperFxEmittedEvents.g.cs";

    private const string Preamble = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;

namespace App;

public class Events : List<object>, ICarriesEvents;

public class Claim { public Guid Id { get; set; } }

public record ClaimNode(Guid Id);
public record ReleaseNode(Guid Id);
public record AskPerson(Guid Id, string Question);

public record NodeClaimed;
public record ClaimRenewed;
public record ClaimReleased;
public record QuestionAsked;
";

    private const string Stream = "typeof(global::JasperFx.Events.IEventStream<global::App.Claim>)";

    private static string Manifest(string body) =>
        GeneratorHarness.GeneratedSource(Preamble + body, ManifestFileName);

    private static void ShouldCompile(string body) =>
        GeneratorHarness.GeneratedCodeErrors(Preamble + body).ShouldBeEmpty();

    [Fact]
    public void each_overload_gets_its_own_entry_with_only_its_own_events()
    {
        const string body = @"
public static class ClaimHandler
{
    public static void Handle(ClaimNode command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new NodeClaimed());
        stream.AppendOne(new ClaimRenewed());
    }

    public static void Handle(ReleaseNode command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new ClaimReleased());
    }

    public static void Handle(AskPerson command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new QuestionAsked());
    }
}
";
        var manifest = Manifest(body);

        manifest.ShouldContain(
            "[assembly: global::JasperFx.Events.EmittedEvents(typeof(global::App.ClaimHandler), \"Handle\", typeof(global::App.NodeClaimed), typeof(global::App.ClaimRenewed), " +
            $"ParameterTypes = new global::System.Type[] {{ typeof(global::App.ClaimNode), {Stream} }})]");
        manifest.ShouldContain(
            "[assembly: global::JasperFx.Events.EmittedEvents(typeof(global::App.ClaimHandler), \"Handle\", typeof(global::App.ClaimReleased), " +
            $"ParameterTypes = new global::System.Type[] {{ typeof(global::App.ReleaseNode), {Stream} }})]");
        manifest.ShouldContain(
            "[assembly: global::JasperFx.Events.EmittedEvents(typeof(global::App.ClaimHandler), \"Handle\", typeof(global::App.QuestionAsked), " +
            $"ParameterTypes = new global::System.Type[] {{ typeof(global::App.AskPerson), {Stream} }})]");

        ShouldCompile(body);
    }

    [Fact]
    public void the_parameter_types_read_back_as_the_methods_own_parameter_types()
    {
        // What a consumer such as Wolverine does: match an entry to a MethodInfo by its parameters. The
        // nullable reference annotation, the tuple element names and the nullable value type must all come
        // out as the runtime types GetParameters() reports.
        const string body = @"
public static class ClaimHandler
{
    public static void Handle(ClaimNode command, IEventStream<Claim> stream, string? note, int? attempt,
        (Guid Id, string Name) owner, Guid[] related)
    {
        stream.AppendOne(new NodeClaimed());
    }

    public static void Handle(ReleaseNode command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new ClaimReleased());
    }
}
";
        ShouldCompile(body);

        var assembly = CompileAndLoad(body);
        var handler = assembly.GetType("App.ClaimHandler")!;
        var entries = assembly.GetCustomAttributes<EmittedEventsAttribute>().ToArray();

        entries.Length.ShouldBe(2);
        foreach (var method in handler.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Handle"))
        {
            var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var entry = entries.Single(e => e.ParameterTypes!.SequenceEqual(parameterTypes));

            entry.HandlerType.ShouldBe(handler);
            entry.EventTypes.Single().Name.ShouldBe(parameterTypes[0] == assembly.GetType("App.ClaimNode")
                ? "NodeClaimed"
                : "ClaimReleased");
        }
    }

    [Fact]
    public void a_method_with_no_parameters_records_an_empty_parameter_list()
    {
        const string body = @"
public static class StartHandler
{
    public static Events Handle() => new Events { new NodeClaimed() };
}
";
        Manifest(body).ShouldContain(
            "typeof(global::App.StartHandler), \"Handle\", typeof(global::App.NodeClaimed), ParameterTypes = new global::System.Type[0])]");
        ShouldCompile(body);
    }

    [Fact]
    public void overloads_whose_parameter_types_cannot_be_named_fall_back_to_one_entry_by_name()
    {
        // typeof at assembly scope cannot reach a private nested type or a file-local one, and cannot
        // express a ref parameter. Those overloads merge into one name-only entry, as every overload did
        // before; the nameable overload still gets its own.
        const string body = @"
file record Secret;

public static class ClaimHandler
{
    private record Hidden;

    public static void Handle(ClaimNode command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new NodeClaimed());
    }

    private static void Handle(Hidden command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new ClaimRenewed());
    }

    public static void Handle(ref ReleaseNode command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new ClaimReleased());
    }

    internal static void Handle(Secret command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new QuestionAsked());
    }
}
";
        var manifest = Manifest(body);

        manifest.ShouldContain(
            "typeof(global::App.ClaimHandler), \"Handle\", typeof(global::App.NodeClaimed), " +
            $"ParameterTypes = new global::System.Type[] {{ typeof(global::App.ClaimNode), {Stream} }})]");
        manifest.ShouldContain(
            "typeof(global::App.ClaimHandler), \"Handle\", typeof(global::App.ClaimRenewed), typeof(global::App.ClaimReleased), typeof(global::App.QuestionAsked))]");
        manifest.ShouldNotContain("Hidden");
        manifest.ShouldNotContain("Secret");

        ShouldCompile(body);
    }

    [Fact]
    public void a_double_loaded_generator_still_compiles_with_per_overload_entries()
    {
        // AllowMultiple is what keeps two copies of the generator (jasperfx#462) from failing the build;
        // repeating an entry that carries a named argument must not change that.
        const string body = @"
public static class ClaimHandler
{
    public static void Handle(ClaimNode command, IEventStream<Claim> stream) => stream.AppendOne(new NodeClaimed());

    public static void Handle(ReleaseNode command, IEventStream<Claim> stream) => stream.AppendOne(new ClaimReleased());
}
";
        GeneratorHarness.DoubleLoadGeneratedCodeErrors(Preamble + body).ShouldBeEmpty();
    }

    // Compiles the source with the generator, then loads the result where the manifest can be read the way
    // a consumer reads it. JasperFx.Events resolves from the default context, so the attribute type is the
    // one this test references.
    private static Assembly CompileAndLoad(string body)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create(
            "OverloadManifest" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Preamble + body)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll"))
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver.Create(new AggregateEvolverGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        result.Success.ShouldBeTrue(string.Join(System.Environment.NewLine, result.Diagnostics));

        stream.Position = 0;
        return new AssemblyLoadContext("jasperfx-1021", isCollectible: true).LoadFromStream(stream);
    }
}
