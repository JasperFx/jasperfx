using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CoreTests.MultiTenancy;

// jasperfx#409: uniform dynamic-tenancy admin surface (CritterWatch#209).
public class dynamic_tenancy_admin_surface
{
    [Fact]
    public async Task auto_assign_overload_throws_by_default()
    {
        IDynamicTenantSource<string> source = new ValueOnlyTenantSource();
        await Should.ThrowAsync<NotSupportedException>(() => source.AddTenantAsync("acme"));
    }

    [Fact]
    public async Task auto_assign_overload_is_used_when_a_source_supports_it()
    {
        var source = new AutoAssignTenantSource();
        await ((IDynamicTenantSource<string>)source).AddTenantAsync("acme");
        source.AutoAssigned.ShouldBe(["acme"]);
    }

    [Fact]
    public async Task add_tenant_with_value_dispatches_to_registered_sources()
    {
        var source = new ValueOnlyTenantSource();
        var services = provider(source);

        await services.AddTenantAsync("acme", "Host=db1");

        source.Added.ShouldBe([("acme", "Host=db1")]);
    }

    [Fact]
    public async Task auto_assign_add_dispatches_to_registered_sources()
    {
        var source = new AutoAssignTenantSource();
        var services = provider(source);

        await services.AddTenantAsync("acme");

        source.AutoAssigned.ShouldBe(["acme"]);
    }

    [Fact]
    public async Task disable_enable_remove_dispatch_to_registered_sources()
    {
        var source = new ValueOnlyTenantSource();
        var services = provider(source);

        await services.DisableTenantAsync("acme");
        source.Disabled.ShouldBe(["acme"]);
        (await services.AllDisabledTenantsAsync()).ShouldBe(["acme"]);

        await services.EnableTenantAsync("acme");
        source.Disabled.ShouldBeEmpty();

        await services.RemoveTenantAsync("acme");
        source.Removed.ShouldBe(["acme"]);
    }

    [Fact]
    public async Task dispatches_to_every_registered_dynamic_source()
    {
        var a = new ValueOnlyTenantSource();
        var b = new ValueOnlyTenantSource();
        var services = provider(a, b);

        services.DynamicTenantSources().Count.ShouldBe(2);

        await services.AddTenantAsync("acme", "Host=db1");

        a.Added.ShouldBe([("acme", "Host=db1")]);
        b.Added.ShouldBe([("acme", "Host=db1")]);
    }

    [Fact]
    public async Task is_a_graceful_no_op_when_no_dynamic_source_is_registered()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        services.DynamicTenantSources().ShouldBeEmpty();

        // None of these should throw with no source registered
        await services.AddTenantAsync("acme", "Host=db1");
        await services.AddTenantAsync("acme");
        await services.DisableTenantAsync("acme");
        await services.EnableTenantAsync("acme");
        await services.RemoveTenantAsync("acme");
        (await services.AllDisabledTenantsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ihost_overloads_delegate_to_the_service_provider()
    {
        var source = new ValueOnlyTenantSource();
        IHost host = new FakeHost(provider(source));

        await host.AddTenantAsync("acme", "Host=db1");
        host.DynamicTenantSources().Count.ShouldBe(1);

        source.Added.ShouldBe([("acme", "Host=db1")]);
    }

    private static IServiceProvider provider(params IDynamicTenantSource<string>[] sources)
    {
        var services = new ServiceCollection();
        foreach (var source in sources)
        {
            services.AddSingleton(source);
        }

        return services.BuildServiceProvider();
    }
}

internal class ValueOnlyTenantSource : IDynamicTenantSource<string>
{
    public List<(string TenantId, string Value)> Added { get; } = new();
    public List<string> Disabled { get; } = new();
    public List<string> Enabled { get; } = new();
    public List<string> Removed { get; } = new();

    public DatabaseCardinality Cardinality => DatabaseCardinality.DynamicMultiple;
    public ValueTask<string> FindAsync(string tenantId) => new(tenantId);
    public Task RefreshAsync() => Task.CompletedTask;
    public IReadOnlyList<string> AllActive() => Added.Select(x => x.Value).ToList();

    public IReadOnlyList<Assignment<string>> AllActiveByTenant()
        => Added.Select(x => new Assignment<string>(x.TenantId, x.Value)).ToList();

    public Task AddTenantAsync(string tenantId, string connectionValue)
    {
        Added.Add((tenantId, connectionValue));
        return Task.CompletedTask;
    }

    public Task DisableTenantAsync(string tenantId)
    {
        Disabled.Add(tenantId);
        return Task.CompletedTask;
    }

    public Task RemoveTenantAsync(string tenantId)
    {
        Removed.Add(tenantId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> AllDisabledAsync() => Task.FromResult<IReadOnlyList<string>>(Disabled);

    public Task EnableTenantAsync(string tenantId)
    {
        Enabled.Add(tenantId);
        Disabled.Remove(tenantId);
        return Task.CompletedTask;
    }
}

// Supports the auto-assign provisioning shape (sharded/partitioned style). Re-declares the interface so
// its AddTenantAsync(string, CancellationToken) takes over the default interface member's mapping.
internal class AutoAssignTenantSource : ValueOnlyTenantSource, IDynamicTenantSource<string>
{
    public List<string> AutoAssigned { get; } = new();

    public Task AddTenantAsync(string tenantId, CancellationToken token = default)
    {
        AutoAssigned.Add(tenantId);
        return Task.CompletedTask;
    }
}

internal class FakeHost(IServiceProvider services) : IHost
{
    public IServiceProvider Services { get; } = services;
    public void Dispose() { }
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
