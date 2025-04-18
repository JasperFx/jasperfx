﻿using JasperFx.CommandLine.Descriptions;
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
        var list = new List<Exception>();
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
                list.Add(new ResourceSetupException(source, e));
            }
        }

        async ValueTask execute(IStatefulResource r, CancellationToken t)
        {
            try
            {
                await r.Setup(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Ran Setup() on resource {Name} of type {Type}", r.Name, r.Type);

                if (_options.Action == StartupAction.ResetState)
                {
                    await r.ClearState(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Ran ClearState() on resource {Name} of type {Type}", r.Name, r.Type);
                }
            }
            catch (Exception e)
            {
                var wrapped = new ResourceSetupException(r, e);
                _logger.LogError(e, "Failed to setup resource {Name} of type {Type}", r.Name, r.Type);

                list.Add(wrapped);
            }
        }

        foreach (var resource in resources) await execute(resource, cancellationToken).ConfigureAwait(false);

        if (list.Any())
        {
            throw new AggregateException(list);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}