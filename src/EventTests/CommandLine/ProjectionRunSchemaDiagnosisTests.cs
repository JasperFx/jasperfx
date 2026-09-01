using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Environment;
using JasperFx.Events.CommandLine;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#728 — projection-run builds the host but never starts it, so nothing that normally
/// migrates on startup has run. An un-migrated database is therefore a first-class cause of failure
/// here, and the raw store exception ("relation ... does not exist") says nothing about the fix.
/// These pin that the command turns that into an actionable message, and that it stays quiet when
/// storage is fine.
/// </summary>
public class ProjectionRunSchemaDiagnosisTests
{
    private sealed class Resource(string type, string name, Exception? failure)
        : StatefulResourceBase(type, name, new Uri("marten://main"), new Uri("marten://main/events"))
    {
        public override Task Check(CancellationToken token)
            => failure == null ? Task.CompletedTask : Task.FromException(failure);
    }

    private sealed class Part(params IStatefulResource[] resources): ISystemPart
    {
        public string Title => "Test";
        public Uri SubjectUri => new("marten://main");
        public Task WriteToConsole() => Task.CompletedTask;

        public ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
            => new(resources);

        public Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results,
            CancellationToken token) => Task.CompletedTask;
    }

    private sealed class ThrowingPart: ISystemPart
    {
        public string Title => "Broken";
        public Uri SubjectUri => new("marten://main");
        public Task WriteToConsole() => Task.CompletedTask;

        public ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
            => throw new InvalidOperationException("cannot enumerate resources");

        public Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results,
            CancellationToken token) => Task.CompletedTask;
    }

    private static IServiceProvider withParts(params ISystemPart[] parts)
    {
        var services = new ServiceCollection();
        foreach (var part in parts) services.AddSingleton(part);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task healthy_storage_produces_no_diagnosis()
    {
        // The remedy must not be printed beside an error it has nothing to do with — a projection
        // that threw for its own reasons is not a schema problem.
        var services = withParts(new Part(new Resource("Marten", "Event Store", null)));

        var diagnosis = await ProjectionRunSchemaDiagnosis.TryDiagnoseAsync(services, CancellationToken.None);

        diagnosis.ShouldBeNull();
    }

    [Fact]
    public async Task an_application_with_no_resources_produces_no_diagnosis()
    {
        var diagnosis = await ProjectionRunSchemaDiagnosis
            .TryDiagnoseAsync(withParts(), CancellationToken.None);

        diagnosis.ShouldBeNull();
    }

    [Fact]
    public async Task a_failing_check_names_the_resource_and_both_commands()
    {
        var services = withParts(new Part(
            new Resource("Marten", "Event Store", new InvalidOperationException("relation does not exist"))));

        var diagnosis = await ProjectionRunSchemaDiagnosis.TryDiagnoseAsync(services, CancellationToken.None);

        diagnosis.ShouldNotBeNull();
        diagnosis.ShouldContain("Marten 'Event Store'");

        // resources setup is listed first on purpose: it works on every store, while db-apply only
        // sees a Polecat store from 5.20.0 (polecat#501).
        diagnosis.ShouldContain("resources setup");
        diagnosis.ShouldContain("db-apply");

        // The reason the command does not just migrate for you is part of the message, because
        // otherwise the obvious "why didn't it fix it?" goes unanswered.
        diagnosis.ShouldContain("does not migrate anything itself");
    }

    [Fact]
    public async Task every_failing_resource_is_named()
    {
        var services = withParts(new Part(
            new Resource("Marten", "Event Store", new Exception("no")),
            new Resource("WolverineEnvelopeStorage", "Envelopes", new Exception("no")),
            new Resource("Marten", "Documents", null)));

        var diagnosis = await ProjectionRunSchemaDiagnosis.TryDiagnoseAsync(services, CancellationToken.None);

        diagnosis.ShouldContain("Marten 'Event Store'");
        diagnosis.ShouldContain("WolverineEnvelopeStorage 'Envelopes'");
        diagnosis.ShouldNotContain("Marten 'Documents'");
    }

    [Fact]
    public async Task a_diagnosis_that_itself_fails_is_simply_absent()
    {
        // The caller already has a real error to report. A failure to EXPLAIN it must never replace
        // it — that would turn a clear store exception into an unrelated one.
        var diagnosis = await ProjectionRunSchemaDiagnosis
            .TryDiagnoseAsync(withParts(new ThrowingPart()), CancellationToken.None);

        diagnosis.ShouldBeNull();
    }

    [Fact]
    public async Task a_hung_check_is_bounded()
    {
        // A resource check is a network call, and it is diagnosing a failure that already happened.
        // It must never cost more than the error it explains.
        var previous = ProjectionRunSchemaDiagnosis.CheckTimeout;
        ProjectionRunSchemaDiagnosis.CheckTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            var services = withParts(new Part(new HangingResource()));

            var diagnosis = await ProjectionRunSchemaDiagnosis.TryDiagnoseAsync(services, CancellationToken.None);

            // The cancellation surfaces as a failed check, so the resource is reported rather than
            // the whole diagnosis being lost.
            diagnosis.ShouldNotBeNull();
            diagnosis.ShouldContain("Marten 'Slow'");
        }
        finally
        {
            ProjectionRunSchemaDiagnosis.CheckTimeout = previous;
        }
    }

    private sealed class HangingResource()
        : StatefulResourceBase("Marten", "Slow", new Uri("marten://main"), new Uri("marten://main/slow"))
    {
        public override Task Check(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
    }
}
