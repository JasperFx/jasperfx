using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #1241: a self-aggregating type that declares its own <c>Evolve</c> / <c>EvolveAsync</c> got a
/// dispatcher that sent every event to that method and never looked at the conventional
/// <c>Apply</c> / <c>Create</c> / <c>ShouldDelete</c> methods it inherits from a base class. Their event
/// types were left out of <c>EventTypes</c> as well, so the events were not even fetched. The
/// conventional path has collected inherited methods since #295; the Evolve path now does too.
///
/// <para>Routing: an event type an inherited conventional method handles goes to that method; every
/// other event goes to the type's own Evolve. When the Evolve body also names the event type (or a
/// base type or interface of it), or names no event type at all and so already took every event, the
/// inherited method runs first and Evolve second. Methods the type declares itself are still left to
/// its Evolve.</para>
/// </summary>
public class EvolveWithInheritedConventionalMethodsTests
{
    private const string Events = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;

namespace Test;

public class BaseHandled { }
public class BaseAsyncHandled { }
public class Shared : ISharedMarker { }
public interface ISharedMarker { }
public class Own { }
public class Removed { }
";

    private const string Base = @"
public abstract class WorkflowBase
{
    public System.Collections.Generic.List<string> Log { get; } = new();
    public void Apply(BaseHandled e) => Log.Add(""base:BaseHandled"");
    public void Apply(Shared e) => Log.Add(""base:Shared"");
}
";

    private static string EvolverFor(string[] generatedSources, string typeName)
    {
        return generatedSources.Single(x => x.Contains($"file sealed class {typeName}EvolveEvolver"));
    }

    [Fact]
    public void inherited_apply_methods_are_dispatched_ahead_of_a_mutable_object_evolve()
    {
        var source = Events + Base + @"
public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(object e)
    {
        switch (e)
        {
            case Own own:
                Log.Add(""evolve:Own"");
                break;
        }
    }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("IGeneratedSyncEvolver<global::Test.Workflow, global::System.Guid>");
        generated.ShouldContain("case global::Test.BaseHandled data:");
        generated.ShouldContain("case global::Test.Shared data:");
        generated.ShouldContain("snapshot.Apply(data);");
        generated.ShouldContain("snapshot.Evolve(e.Data);");

        // The inherited event types are fetched as well as the ones the Evolve body names.
        generated.ShouldContain("typeof(global::Test.Own)");
        generated.ShouldContain("typeof(global::Test.BaseHandled)");
        generated.ShouldContain("typeof(global::Test.Shared)");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_event_type_both_paths_handle_goes_to_the_inherited_method_first_then_evolve()
    {
        var source = Events + Base + @"
public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(IEvent e)
    {
        switch (e)
        {
            case IEvent<Own> own:
                Log.Add(""evolve:Own"");
                break;
            case IEvent<Shared> shared:
                Log.Add(""evolve:Shared"");
                break;
        }
    }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        var sharedArm = generated.Substring(generated.IndexOf("case global::Test.Shared data:", StringComparison.Ordinal));
        sharedArm = sharedArm.Substring(0, sharedArm.IndexOf("return snapshot;", StringComparison.Ordinal));
        sharedArm.IndexOf("snapshot.Apply(data);", StringComparison.Ordinal)
            .ShouldBeLessThan(sharedArm.IndexOf("snapshot.Evolve(e);", StringComparison.Ordinal));

        // BaseHandled is not named by Evolve, so its arm never reaches Evolve.
        var baseArm = generated.Substring(generated.IndexOf("case global::Test.BaseHandled data:", StringComparison.Ordinal));
        baseArm = baseArm.Substring(0, baseArm.IndexOf("return snapshot;", StringComparison.Ordinal));
        baseArm.ShouldNotContain("Evolve(");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void evolve_naming_an_interface_of_an_inherited_event_type_also_receives_it()
    {
        var source = Events + Base + @"
public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(object e)
    {
        if (e is ISharedMarker marker) Log.Add(""evolve:Marker"");
    }
}
";
        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        var sharedArm = generated.Substring(generated.IndexOf("case global::Test.Shared data:", StringComparison.Ordinal));
        sharedArm = sharedArm.Substring(0, sharedArm.IndexOf("return snapshot;", StringComparison.Ordinal));
        sharedArm.ShouldContain("snapshot.Evolve(e.Data);");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_catch_all_evolve_keeps_an_empty_event_type_filter()
    {
        // An Evolve body that names no event type takes everything: the empty EventTypes is what says
        // "do not filter". Adding the inherited types would narrow it to exactly those.
        var source = Events + Base + @"
public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(object e) => Count++;
}
";
        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("public global::System.Type[] EventTypes => [];");

        // It received every event before, so it still does, after the inherited method.
        var baseArm = generated.Substring(generated.IndexOf("case global::Test.BaseHandled data:", StringComparison.Ordinal));
        baseArm = baseArm.Substring(0, baseArm.IndexOf("return snapshot;", StringComparison.Ordinal));
        baseArm.IndexOf("snapshot.Apply(data);", StringComparison.Ordinal)
            .ShouldBeLessThan(baseArm.IndexOf("snapshot.Evolve(e.Data);", StringComparison.Ordinal));

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void immutable_evolve_returning_the_aggregate()
    {
        var source = Events + Base + @"
public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }

    public Workflow Evolve(IEvent e)
    {
        if (e is IEvent<Own>) Log.Add(""evolve:Own"");
        return this;
    }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("case global::Test.BaseHandled data:");
        generated.ShouldContain("return (snapshot ?? new global::Test.Workflow()).Evolve(e);");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("public async Task EvolveAsync(object e, CancellationToken token) { await Task.Yield(); if (e is Own) Log.Add(\"evolve:Own\"); }", "await snapshot.EvolveAsync(e.Data, cancellation);")]
    [InlineData("public async ValueTask<Workflow> EvolveAsync(IEvent e) { await Task.Yield(); if (e is IEvent<Own>) Log.Add(\"evolve:Own\"); return this; }", "return await (snapshot ?? new global::Test.Workflow()).EvolveAsync(e);")]
    public void async_evolve_forms(string evolve, string expectedCall)
    {
        var source = Events + Base + @"
public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }

    " + evolve + @"
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("IGeneratedAsyncEvolver<global::Test.Workflow, global::System.Guid>");
        generated.ShouldContain("case global::Test.BaseHandled data:");
        generated.ShouldContain(expectedCall);

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_async_inherited_apply_makes_a_sync_evolve_dispatcher_async()
    {
        var source = Events + @"
public abstract class WorkflowBase
{
    public int Count { get; set; }
    public Task Apply(BaseAsyncHandled e) { Count++; return Task.CompletedTask; }
}

public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }
    public void Evolve(object e) { if (e is Own) Count += 10; }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("IGeneratedAsyncEvolver<global::Test.Workflow, global::System.Guid>");
        generated.ShouldContain("await snapshot.Apply(data);");
        generated.ShouldContain("snapshot.Evolve(e.Data);");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_inherited_should_delete_uses_the_determine_action_contract()
    {
        // The runtime only binds a DetermineAction contract once it sees a ShouldDelete method on the
        // aggregate, inherited ones included, so a plain evolver here would have no dispatcher at all.
        var source = Events + @"
public abstract class WorkflowBase
{
    public int Count { get; set; }
    public void Apply(BaseHandled e) => Count++;
    public bool ShouldDelete(Removed e) => true;
}

public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }
    public void Evolve(object e) { if (e is Own) Count += 10; }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("IGeneratedSyncDetermineAction<global::Test.Workflow, global::System.Guid>");
        generated.ShouldContain("case global::Test.Removed data:");
        generated.ShouldContain("snapshot.ShouldDelete(data)");
        generated.ShouldContain("typeof(global::Test.Removed)");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_inherited_should_delete_with_an_async_evolve_uses_the_async_determine_action_contract()
    {
        var source = Events + @"
public abstract class WorkflowBase
{
    public int Count { get; set; }
    public bool ShouldDelete(Removed e) => true;
}

public class Workflow : WorkflowBase
{
    public Guid Id { get; set; }
    public Task EvolveAsync(object e) { if (e is Own) Count += 10; return Task.CompletedTask; }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldContain("IGeneratedAsyncDetermineAction<global::Test.Workflow, global::System.Guid>");
        generated.ShouldContain("snapshot.ShouldDelete(data)");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void conventional_methods_the_type_declares_itself_are_still_left_to_evolve()
    {
        var source = Events + @"
public class Workflow
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public void Apply(BaseHandled e) => Count++;
    public void Evolve(object e) { if (e is Own) Count += 10; }
}
";
        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = EvolverFor(generatedSources, "Workflow");

        generated.ShouldNotContain("case global::Test.BaseHandled data:");
        generated.ShouldNotContain("typeof(global::Test.BaseHandled)");
    }
}
