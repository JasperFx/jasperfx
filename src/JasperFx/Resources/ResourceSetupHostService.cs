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

    public ResourceSetupHostService(ResourceSetupOptions options, IEnumerable<ISystemPart> parts, ILogger<ResourceSetupHostService> logger, IEnumerable<IResourceCreator> creators)
    {
        _parts = parts.ToArray();
        _options = options;
        _logger = logger;
        _creators = creators.ToArray();
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
            throw new AggregateException(exceptions);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}