using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// jasperfx#990 — the generator infers the events a handler appends from its body, keyed off
/// JasperFx.Events marker types (<c>IEventStream&lt;T&gt;</c>, <c>IRefersToAggregate</c>,
/// <c>ICarriesEvents</c>), and records them as <c>[assembly: EmittedEvents(...)]</c> so Wolverine can
/// stop scaffolding <c>[Emits]</c>.
/// </summary>
public class EmittedEventsManifestTests
{
    private const string ManifestFileName = "JasperFxEmittedEvents.g.cs";

    /// <summary>
    /// Stand-ins for Wolverine's store-agnostic attribute and event-carrying return types. They are
    /// defined here, not referenced, because the point is that the generator needs nothing from
    /// Wolverine: only the JasperFx.Events markers they implement.
    /// </summary>
    private const string Preamble = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace App;

public class WriteModelAttribute : Attribute, IRefersToAggregate;

public class Events : List<object>, ICarriesEvents;

public class StartStream : ICarriesEvents
{
    public StartStream(Guid streamId, params object[] events) { }
}

public static class Storage
{
    public static StartStream StartStream<T>(Guid id, params object[] events) => new(id, events);
}

public class Appointment { public Guid Id { get; set; } }
public class Patient { public Guid Id { get; set; } }

public record Booked;
public record Confirmed;
public record Cancelled;
public record PatientNotified;
";

    private static string Manifest(string body) =>
        GeneratorHarness.GeneratedSource(Preamble + body, ManifestFileName);

    private static string[] FileNames(string body) => GeneratorHarness.GeneratedFileNames(Preamble + body);

    private static void ShouldCompile(string body) =>
        GeneratorHarness.GeneratedCodeErrors(Preamble + body).ShouldBeEmpty();

    [Fact]
    public void append_one_on_an_event_stream()
    {
        const string body = @"
public static class ConfirmHandler
{
    public static void Handle(string command, IEventStream<Appointment> stream)
    {
        stream.AppendOne(new Confirmed());
    }
}
";
        Manifest(body).ShouldContain(
            "[assembly: global::JasperFx.Events.EmittedEvents(typeof(global::App.ConfirmHandler), \"Handle\", typeof(global::App.Confirmed))]");
        ShouldCompile(body);
    }

    [Fact]
    public void append_many_with_several_events()
    {
        const string body = @"
public static class BookHandler
{
    public static void Handle(string command, IEventStream<Appointment> stream)
    {
        var confirmed = new Confirmed();
        stream.AppendMany(new Booked(), confirmed);
        stream.AppendMany(new object[] { new Cancelled() });
    }
}
";
        Manifest(body).ShouldContain(
            "typeof(global::App.BookHandler), \"Handle\", typeof(global::App.Booked), typeof(global::App.Confirmed), typeof(global::App.Cancelled))]");
        ShouldCompile(body);
    }

    [Fact]
    public void multi_stream_with_two_event_stream_parameters()
    {
        const string body = @"
public static class TransferHandler
{
    public static void Handle(string command, IEventStream<Appointment> appointment, IEventStream<Patient> patient)
    {
        appointment.AppendOne(new Cancelled());
        patient.AppendOne(new PatientNotified());
    }
}
";
        Manifest(body).ShouldContain(
            "typeof(global::App.TransferHandler), \"Handle\", typeof(global::App.Cancelled), typeof(global::App.PatientNotified))]");
        ShouldCompile(body);
    }

    [Fact]
    public void a_factory_returning_an_event_carrier()
    {
        const string body = @"
public static class StartHandler
{
    public static StartStream Handle(string command)
    {
        return Storage.StartStream<Appointment>(Guid.NewGuid(), new Booked(), new Confirmed());
    }
}

public static class StartByConstructorHandler
{
    public static StartStream Handle(string command) => new StartStream(Guid.NewGuid(), new Booked());
}
";
        var manifest = Manifest(body);

        // The Guid stream id is not an event: only object-typed slots are read.
        manifest.ShouldContain(
            "typeof(global::App.StartHandler), \"Handle\", typeof(global::App.Booked), typeof(global::App.Confirmed))]");
        manifest.ShouldContain(
            "typeof(global::App.StartByConstructorHandler), \"Handle\", typeof(global::App.Booked))]");
        manifest.ShouldNotContain("typeof(global::System.Guid)");
        ShouldCompile(body);
    }

    [Fact]
    public void a_collection_initializer()
    {
        const string body = @"
public static class InitializerHandler
{
    public static Events Handle(string command, [WriteModel] Appointment appointment)
    {
        return new Events { new Confirmed(), new PatientNotified() };
    }
}
";
        Manifest(body).ShouldContain(
            "typeof(global::App.InitializerHandler), \"Handle\", typeof(global::App.Confirmed), typeof(global::App.PatientNotified))]");
        ShouldCompile(body);
    }

    [Fact]
    public void add_on_a_local()
    {
        const string body = @"
public static class AddHandler
{
    public static Events Handle(string command, bool cancel)
    {
        var events = new Events();
        events.Add(new Booked());
        if (cancel) events.Add(new Cancelled());
        return events;
    }
}
";
        Manifest(body).ShouldContain(
            "typeof(global::App.AddHandler), \"Handle\", typeof(global::App.Booked), typeof(global::App.Cancelled))]");
        ShouldCompile(body);
    }

    [Fact]
    public void a_tuple_return()
    {
        const string body = @"
public static class TupleHandler
{
    public static System.Threading.Tasks.Task<(Events, string)> Handle(string command)
    {
        var events = new Events { new Booked() };
        return System.Threading.Tasks.Task.FromResult((events, ""done""));
    }
}
";
        Manifest(body).ShouldContain(
            "typeof(global::App.TupleHandler), \"Handle\", typeof(global::App.Booked))]");
        ShouldCompile(body);
    }

    [Fact]
    public void an_object_typed_event_is_skipped()
    {
        const string body = @"
public static class ObjectHandler
{
    public static void Handle(string command, IEventStream<Appointment> stream)
    {
        object unknown = new Booked();
        stream.AppendOne(unknown);
        stream.AppendOne(new Confirmed());
    }
}
";
        var manifest = Manifest(body);

        manifest.ShouldContain("typeof(global::App.ObjectHandler), \"Handle\", typeof(global::App.Confirmed))]");
        manifest.ShouldNotContain("typeof(object)");
        manifest.ShouldNotContain("global::App.Booked");
        ShouldCompile(body);
    }

    [Fact]
    public void a_method_with_no_candidates_produces_no_manifest()
    {
        const string body = @"
public static class NotAnEventHandler
{
    // Appends nothing a marker identifies: a plain List<object> is not an ICarriesEvents.
    public static List<object> Handle(string command)
    {
        return new List<object> { new Booked() };
    }
}

public static class CandidateWithNoEvents
{
    // A candidate by parameter, but it appends nothing — still no entry.
    public static void Handle(string command, IEventStream<Appointment> stream) { }
}
";
        FileNames(body).ShouldNotContain(ManifestFileName);
    }

    [Fact]
    public void the_manifest_is_skipped_when_jasperfx_events_is_not_referenced()
    {
        GeneratorHarness.GeneratedFileNamesWithoutJasperFxReference(@"
public static class Handler
{
    public static void Handle(string command) { }
}
").ShouldBeEmpty();
    }

    [Fact]
    public void an_unrelated_edit_does_not_reanalyze_other_methods()
    {
        const string handlerFile = @"
using JasperFx.Events;

namespace App;

public static class ConfirmHandler
{
    public static void Handle(string command, IEventStream<Appointment> stream)
    {
        stream.AppendOne(new Confirmed());
    }
}
";
        const string otherFile = @"
namespace App;

public static class Unrelated
{
    public static int Compute(int x) => x + 1;
}
";

        var parseOptions = CSharpParseOptions.Default;
        var compilation = CSharpCompilation.Create(
            "IncrementalAssembly",
            [
                CSharpSyntaxTree.ParseText(Preamble, parseOptions),
                CSharpSyntaxTree.ParseText(handlerFile, parseOptions),
                CSharpSyntaxTree.ParseText(otherFile, parseOptions)
            ],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll"))
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new AggregateEvolverGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        var otherTree = compilation.SyntaxTrees.Last();
        var editedTree = otherTree.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(
            otherFile.Replace("x + 1", "x + 2")));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(otherTree, editedTree));

        var steps = driver.GetRunResult().Results.Single().TrackedSteps["JasperFxEmittedEvents"];
        var outputs = steps.SelectMany(s => s.Outputs).ToArray();

        // The handler's analysis came out of the cache; nothing was recomputed for it.
        outputs.ShouldNotBeEmpty();
        outputs.ShouldAllBe(o => o.Reason == IncrementalStepRunReason.Cached || o.Reason == IncrementalStepRunReason.Unchanged);
        outputs.ShouldContain(o => o.Reason == IncrementalStepRunReason.Cached);
    }
}

/// <summary>
/// jasperfx#990, part 1 — pass 2's syntax filter let a method through only when a parameter
/// attribute had "Aggregate" in its name, so Wolverine's [WriteModel] / [ReadModel] / [DcbModel]
/// were never semantically checked for <c>IRefersToAggregate</c> at all.
/// </summary>
public class RefersToAggregateByMarkerNotNameTests
{
    // An aggregate built only through an event constructor declares no Apply/Create, so pass 1
    // never sees it — the parameter attribute is the only way it reaches the generator.
    private const string Source = @"
using System;
using JasperFx.Events.Aggregation;

namespace App;

public class WriteModelAttribute : Attribute, IRefersToAggregate;

public record AppointmentBooked(Guid AppointmentId);

public class Appointment
{
    public Guid Id { get; set; }

    public Appointment(AppointmentBooked e) { Id = e.AppointmentId; }
}

public static class BookHandler
{
    public static void Handle(string command, [WriteModel] Appointment appointment) { }
}
";

    [Fact]
    public void an_irefers_to_aggregate_attribute_without_aggregate_in_its_name_is_analyzed()
    {
        var (_, generatedSources) = GeneratorHarness.Run(Source);

        generatedSources.ShouldContain(s => s.Contains("typeof(global::App.Appointment)"));
        GeneratorHarness.GeneratedCodeErrors(Source).ShouldBeEmpty();
    }
}
