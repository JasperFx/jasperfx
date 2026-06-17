using JasperFx.CommandLine.Descriptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JasperFx.Resources;

public class ResourceSetupOptions
{
    public StartupAction Action { get; set; } = StartupAction.SetupOnly;
}

public class ResourceSetupHostService : IHostedService
{
    private readonly ILogger<ResourceSetupHostService> _logger;
    private readonly IResourceCreator[] _creators;
    private readonly ResourceSetupOptions _options;
    private readonly ISystemPart[] _parts;
    private readonly JasperFxOptions? _jasperFxOptions;

    public ResourceSetupHostService(ResourceSetupOptions options, IEnumerable<ISystemPart> parts,
        ILogger<ResourceSetupHostService> logger, IEnumerable<IResourceCreator> creators,
        IEnumerable<JasperFxOptions> jasperFxOptions)
    {
        _parts = parts.ToArray();
        _options = options;
        _logger = logger;
        _creators = creators.ToArray();

        // Optional: JasperFxOptions is registered whenever the JasperFx host integration is used (the
        // normal case). Resolving it via IEnumerable keeps standalone AddResourceSetupOnStartup usage
        // working even when JasperFxOptions was never registered — in which case we fall back to FailFast.
        _jasperFxOptions = jasperFxOptions.FirstOrDefault();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();

        foreach (var creator in _creators)
        {
            try
            {
                await creator.EnsureCreatedAsync(cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to ensure created from {Creator}", creator);
                exceptions.Add(new ResourceSetupException(creator, e));
            }
        }
        
        var resources = new List<IStatefulResource>();

        foreach (var source in _parts)
        {
            try
            {
                resources.AddRange(await source.FindResources());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to find resource sources from {Source}", source);
                exceptions.Add(new ResourceSetupException(source, e));
            }
        }

        // Order first by dependencies
        resources = ResourceExecutor.OrderByDependencies(resources);

        // Using this will catch IResourceCreator too
        await ResourceExecutor.ExecuteSetupAsync(_logger, resources, _options.Action, exceptions, cancellationToken).ConfigureAwait(false);

        if (exceptions.Any())
        {
            var aggregate = new AggregateException(exceptions);

            // The active profile may opt to keep the application starting up despite resource/migration
            // failures (e.g. a replica that loses the migration lock during a rolling deploy). The default
            // is FailFast, which throws and aborts startup as before.
            var failureMode = _jasperFxOptions?.ActiveProfile.ResourceMigrationFailureMode
                              ?? ResourceMigrationFailureMode.FailFast;

            if (failureMode == ResourceMigrationFailureMode.ContinueOnFailures)
            {
                _logger.LogError(aggregate,
                    "One or more resources failed to set up or migrate during startup. Continuing startup anyway because the active profile's ResourceMigrationFailureMode is ContinueOnFailures.");
                return;
            }

            throw aggregate;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}