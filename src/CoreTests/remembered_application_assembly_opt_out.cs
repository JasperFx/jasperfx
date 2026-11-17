using System.Reflection;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CoreTests;

// These tests read and write the process-wide RememberedApplicationAssembly, and any host another test
// starts at the same time records it, so they run on their own.
[CollectionDefinition(nameof(RememberedApplicationAssemblyCollection), DisableParallelization = true)]
public class RememberedApplicationAssemblyCollection;

// jasperfx#1204: RememberApplicationAssembly = false keeps a host out of the process-wide
// RememberedApplicationAssembly, and ResetRememberedApplicationAssembly() clears it.
[Collection(nameof(RememberedApplicationAssemblyCollection))]
public class remembered_application_assembly_opt_out : IDisposable
{
    private readonly Assembly? _original = JasperFxOptions.RememberedApplicationAssembly;

    public void Dispose()
    {
        JasperFxOptions.RememberedApplicationAssembly = _original;
    }

    [Fact]
    public void remembering_is_on_by_default()
    {
        new JasperFxOptions().RememberApplicationAssembly.ShouldBeTrue();
    }

    [Fact]
    public void reset_clears_the_remembered_assembly()
    {
        JasperFxOptions.RememberedApplicationAssembly = GetType().Assembly;

        JasperFxOptions.ResetRememberedApplicationAssembly();

        JasperFxOptions.RememberedApplicationAssembly.ShouldBeNull();
    }

    [Fact]
    public void an_opted_out_host_does_not_reuse_the_remembered_assembly()
    {
        // An earlier host in the process remembered a different assembly
        JasperFxOptions.RememberedApplicationAssembly = typeof(JasperFxOptions).Assembly;

        var options = new JasperFxOptions
        {
            RememberApplicationAssembly = false,
            RegistrationCallingAssembly = GetType().Assembly
        };

        options.ReadHostEnvironment(new JasperFxOptionsTests.StubHostEnvironment());

        options.ApplicationAssembly.ShouldBe(GetType().Assembly);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();

        // ...and leaves the earlier host's value alone
        JasperFxOptions.RememberedApplicationAssembly.ShouldBe(typeof(JasperFxOptions).Assembly);
    }

    [Fact]
    public async Task a_default_host_records_the_remembered_assembly()
    {
        JasperFxOptions.ResetRememberedApplicationAssembly();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx())
            .UseEnvironment("Development")
            .StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<JasperFxOptions>().ApplicationAssembly.ShouldBe(GetType().Assembly);
        JasperFxOptions.RememberedApplicationAssembly.ShouldBe(GetType().Assembly);
    }

    [Fact]
    public async Task an_opted_out_host_does_not_record_the_remembered_assembly()
    {
        JasperFxOptions.ResetRememberedApplicationAssembly();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx(opts => opts.RememberApplicationAssembly = false))
            .UseEnvironment("Development")
            .StartAsync(TestContext.Current.CancellationToken);

        // The host still finds its own application assembly...
        host.Services.GetRequiredService<JasperFxOptions>().ApplicationAssembly.ShouldBe(GetType().Assembly);

        // ...but leaves nothing behind for the next host
        JasperFxOptions.RememberedApplicationAssembly.ShouldBeNull();
    }

    [Fact]
    public async Task an_opted_out_host_ignores_an_assembly_remembered_by_an_earlier_host()
    {
        JasperFxOptions.RememberedApplicationAssembly = typeof(JasperFxOptions).Assembly;

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx(opts => opts.RememberApplicationAssembly = false))
            .UseEnvironment("Development")
            .StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<JasperFxOptions>();
        options.ApplicationAssembly.ShouldBe(GetType().Assembly);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();

        JasperFxOptions.RememberedApplicationAssembly.ShouldBe(typeof(JasperFxOptions).Assembly);
    }

    [Fact]
    public async Task the_opt_out_can_come_from_any_configuration()
    {
        JasperFxOptions.RememberedApplicationAssembly = typeof(JasperFxOptions).Assembly;

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx())
            .ConfigureServices(s => s.Configure<JasperFxOptions>(opts => opts.RememberApplicationAssembly = false))
            .UseEnvironment("Development")
            .StartAsync(TestContext.Current.CancellationToken);

        // The option can be set by any later configuration, not just the AddJasperFx callback
        host.Services.GetRequiredService<JasperFxOptions>().ApplicationAssembly.ShouldBe(GetType().Assembly);
    }
}
