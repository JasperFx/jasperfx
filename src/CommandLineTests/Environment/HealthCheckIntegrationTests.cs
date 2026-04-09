using JasperFx.Environment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace CommandLineTests.Environment;

public class HealthCheckIntegrationTests
{
    [Fact]
    public async Task healthy_check_registers_success()
    {
        var services = new ServiceCollection();
        services.CheckEnvironmentHealthCheck<AlwaysHealthyCheck>();
        var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services.BuildServiceProvider());

        results.Succeeded().ShouldBeTrue();
        results.Successes.ShouldContain(s => s.Contains("AlwaysHealthyCheck"));
    }

    [Fact]
    public async Task unhealthy_check_registers_failure()
    {
        var services = new ServiceCollection();
        services.CheckEnvironmentHealthCheck<AlwaysUnhealthyCheck>();
        var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services.BuildServiceProvider());

        results.Succeeded().ShouldBeFalse();
        results.Failures.ShouldContain(f => f.Description.Contains("AlwaysUnhealthyCheck"));
    }

    [Fact]
    public async Task degraded_check_registers_success_with_warning()
    {
        var services = new ServiceCollection();
        services.CheckEnvironmentHealthCheck<DegradedCheck>();
        var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services.BuildServiceProvider());

        results.Succeeded().ShouldBeTrue();
        results.Successes.ShouldContain(s => s.Contains("degraded"));
    }

    [Fact]
    public async Task mixed_health_checks_and_environment_checks()
    {
        var services = new ServiceCollection();
        services.CheckEnvironment("Manual check", _ => { });
        services.CheckEnvironmentHealthCheck<AlwaysHealthyCheck>();
        services.CheckEnvironmentHealthCheck<AlwaysUnhealthyCheck>();
        var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services.BuildServiceProvider());

        results.Succeeded().ShouldBeFalse();
        results.Successes.Length.ShouldBeGreaterThanOrEqualTo(2);
        results.Failures.Length.ShouldBe(1);
    }

    [Fact]
    public async Task throwing_health_check_registers_failure()
    {
        var services = new ServiceCollection();
        services.CheckEnvironmentHealthCheck<ThrowingCheck>();
        var results = await EnvironmentChecker.ExecuteAllEnvironmentChecks(services.BuildServiceProvider());

        results.Succeeded().ShouldBeFalse();
        results.Failures.ShouldContain(f => f.Description.Contains("ThrowingCheck"));
    }
}

public class AlwaysHealthyCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(HealthCheckResult.Healthy("All good"));
}

public class AlwaysUnhealthyCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(HealthCheckResult.Unhealthy("Something is wrong"));
}

public class DegradedCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(HealthCheckResult.Degraded("Running slow"));
}

public class ThrowingCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => throw new InvalidOperationException("Check exploded");
}
