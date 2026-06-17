using JasperFx;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Environment;
using JasperFx.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace CommandLineTests.Resources;

public class ResourceMigrationFailureModeTests
{
    // A system part whose resource discovery always fails — the simplest way to force the
    // ResourceSetupHostService to collect a startup exception.
    private sealed class ThrowingSystemPart : ISystemPart
    {
        public string Title => "throwing";
        public Uri SubjectUri { get; } = new("system://throwing");
        public Task WriteToConsole() => Task.CompletedTask;
        public ValueTask<IReadOnlyList<IStatefulResource>> FindResources() =>
            throw new InvalidOperationException("boom");
        public Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results,
            CancellationToken token) => Task.CompletedTask;
    }

    private static ResourceSetupHostService BuildService(params JasperFxOptions[] options) =>
        new(new ResourceSetupOptions(), new ISystemPart[] { new ThrowingSystemPart() },
            NullLogger<ResourceSetupHostService>.Instance, Array.Empty<IResourceCreator>(), options);

    [Fact]
    public async Task fail_fast_is_the_default_and_aborts_startup()
    {
        var service = BuildService(new JasperFxOptions());

        await Should.ThrowAsync<AggregateException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task continue_on_failures_lets_startup_proceed()
    {
        var options = new JasperFxOptions();
        // ActiveProfile defaults to the Development profile (it is the same object reference).
        options.Development.ResourceMigrationFailureMode = ResourceMigrationFailureMode.ContinueOnFailures;

        var service = BuildService(options);

        // Must NOT throw — the failure is logged and startup proceeds.
        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task falls_back_to_fail_fast_when_no_jasperfx_options_registered()
    {
        var service = BuildService(); // no JasperFxOptions resolved

        await Should.ThrowAsync<AggregateException>(() => service.StartAsync(CancellationToken.None));
    }
}
