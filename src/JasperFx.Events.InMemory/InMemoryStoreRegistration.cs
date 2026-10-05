using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Events.InMemory;

/// <summary>
/// Options for <see cref="InMemoryStoreRegistration.AddInMemoryStoreForPrototyping"/> (jasperfx#965).
/// </summary>
public sealed class InMemoryStoreOptions
{
    internal List<Action<InMemoryDocumentStore>> StoreConfigurations { get; } = new();

    /// <summary>
    /// Let the store start when the host environment is <c>Production</c>. Off by default, and meant to
    /// stay off: the in-memory store persists nothing.
    /// </summary>
    /// <remarks>
    /// The case this exists for is a test host that never sets <c>DOTNET_ENVIRONMENT</c> — the generic
    /// host then defaults to <c>Production</c>. Setting the environment of that host to
    /// <c>Development</c> (or <c>Testing</c>) is the better fix.
    /// </remarks>
    public bool AllowStartingInTheProductionEnvironment { get; set; }

    /// <summary>
    /// Configure the document store: conjoined tenancy, optimistic concurrency, soft deletes,
    /// sub-class hierarchies.
    /// </summary>
    public InMemoryStoreOptions ConfigureDocuments(Action<InMemoryDocumentStore> configure)
    {
        StoreConfigurations.Add(configure);
        return this;
    }
}

/// <summary>
/// Registers the in-memory prototyping store (jasperfx#965).
/// </summary>
public static class InMemoryStoreRegistration
{
    /// <summary>
    /// Register the in-memory store for <b>prototyping</b> a stub-first application before choosing
    /// Marten, Polecat or Fisher. The name is deliberately awkward: this is a step to move on from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every host start logs a warning that the in-memory store is in use, and a host whose environment
    /// is <c>Production</c> refuses to start unless
    /// <see cref="InMemoryStoreOptions.AllowStartingInTheProductionEnvironment"/> is set. Nothing the
    /// store holds survives a restart.
    /// </para>
    /// <para>
    /// The store is registered as a singleton <see cref="InMemoryDocumentStore"/> and as the
    /// <see cref="IDocumentSessionFactory"/> the rest of the Critter Stack resolves.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddInMemoryStoreForPrototyping(this IServiceCollection services,
        Action<InMemoryStoreOptions>? configure = null)
    {
        var options = new InMemoryStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddSingleton(_ =>
        {
            var store = new InMemoryDocumentStore();
            foreach (var configuration in options.StoreConfigurations) configuration(store);
            return store;
        });
        services.TryAddSingleton<IDocumentSessionFactory>(s => s.GetRequiredService<InMemoryDocumentStore>());

        // A hosted service, so the guard runs as the host starts -- before anything has written to a
        // store that is about to vanish -- and a refusal stops the host rather than logging past it.
        services.AddHostedService<InMemoryStoreGuard>();

        return services;
    }
}

/// <summary>
/// Refuses the <c>Production</c> environment and warns on every start (jasperfx#965).
/// </summary>
internal sealed class InMemoryStoreGuard : IHostedService
{
    private readonly InMemoryStoreOptions _options;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<InMemoryStoreGuard> _logger;

    public InMemoryStoreGuard(InMemoryStoreOptions options, IServiceProvider services)
    {
        _options = options;
        _environment = services.GetService<IHostEnvironment>();
        _logger = services.GetService<ILogger<InMemoryStoreGuard>>() ?? NullLogger<InMemoryStoreGuard>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The literal "Production" name, not JasperFx's ActiveProfile: ActiveProfile is Production for
        // EVERY non-Development environment, which would refuse Staging and Testing hosts too. Only
        // "Production" is refused, and the message names the fix for the test host that is there by
        // default (the generic host's default environment).
        if (_environment?.IsProduction() == true && !_options.AllowStartingInTheProductionEnvironment)
        {
            throw new NotSupportedException(
                "The in-memory prototyping store (AddInMemoryStoreForPrototyping) refuses to start in the " +
                "Production environment: it persists nothing, and every write is lost on restart. Replace it " +
                "with Marten, Polecat or Fisher before going to production. If this is a test or local host " +
                "that never set its environment (the generic host defaults to Production), set " +
                "DOTNET_ENVIRONMENT to Development -- or opt in with " +
                "AddInMemoryStoreForPrototyping(x => x.AllowStartingInTheProductionEnvironment = true).");
        }

        _logger.LogWarning(
            "The in-memory prototyping store is in use{Environment}. It persists nothing and is meant to be " +
            "replaced with Marten, Polecat or Fisher once the design settles.",
            _environment is null ? "" : $" in the {_environment.EnvironmentName} environment");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
