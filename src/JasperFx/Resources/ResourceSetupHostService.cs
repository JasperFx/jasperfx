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
    private readonly ResourceSetupOptions _options;
    private readonly ISystemPart[] _parts;

    public ResourceSetupHostService(ResourceSetupOptions options, IEnumerable<ISystemPart> parts, ILogger<ResourceSetupHostService> logger)
    {
        _parts = parts.ToArray();
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();
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