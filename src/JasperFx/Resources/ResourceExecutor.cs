using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JasperFx.Resources;

internal static class ResourceExecutor
{
    public static async Task SetupResources(IHost host, CancellationToken cancellation = default,
        string? resourceType = null, string? resourceName = null, StartupAction startupAction = StartupAction.SetupOnly)
    {
        var resources = await FindResources(host.Services, resourceType, resourceName);

        var exceptions = new List<Exception>();
        var logger = host.Services.GetRequiredService<ILogger<IStatefulResource>>();
        await ExecuteSetupAsync(logger, resources, startupAction, exceptions, cancellation);

        if (exceptions.Any())
        {
            throw new AggregateException(exceptions);
        }
    }

    internal static async Task<List<IStatefulResource>> FindResources(IServiceProvider services, string? typeName,
        string? resourceName)
    {
        var list = new List<IStatefulResource>();
        var statefulResourceSources = services.GetServices<ISystemPart>().ToArray();
        foreach (var source in statefulResourceSources)
        {
            var sources = await source.FindResources();
            list.AddRange(sources);
        }

        if (resourceName.IsNotEmpty())
        {
            list = list.Where(x => x.Name.EqualsIgnoreCase(resourceName)).ToList();
        }

        if (typeName.IsNotEmpty())
        {
            list = list.Where(x => x.Type.EqualsIgnoreCase(typeName)).ToList();
        }

        return OrderByDependencies(list);
    }

    public static List<IStatefulResource> OrderByDependencies(List<IStatefulResource> resources)
    {
        // Initial sort
        resources = resources.OrderBy(x => x.Type).ThenBy(x => x.Name).ToList();

        if (!resources.OfType<IStatefulResourceWithDependencies>().Any())
        {
            return resources;
        }

        IEnumerable<IStatefulResource> FindDependencies(IStatefulResource resource)
        {
            return resource is IStatefulResourceWithDependencies x
                ? x.FindDependencies(resources)
                : Array.Empty<IStatefulResource>();
        }

        // Again on dependencies
        return resources.TopologicalSort(FindDependencies).ToList();
    }

    public static async ValueTask ExecuteSetupAsync(ILogger logger, IList<IStatefulResource> resources,
        StartupAction action, List<Exception> list, CancellationToken cancellationToken)
    {
        foreach (var resourceCreator in resources.OfType<IResourceCreator>())
        {
            try
            {
                await resourceCreator.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation($"Executed {nameof(IResourceCreator.EnsureCreatedAsync)} on {resourceCreator}");
            }
            catch (Exception e)
            {
                var wrapped = new ResourceSetupException(resourceCreator, e);
                logger.LogError(e, "Failed to execute EnsureCreatedAsync() on {Name} of type {Type}",
                    resourceCreator.Name, resourceCreator.Type);

                list.Add(wrapped);
            }
        }

        foreach (var resource in resources)
        {
            await ExecuteSetupAsync(logger, resource, action, list, cancellationToken);
        }
    }

    public static async ValueTask ExecuteSetupAsync(ILogger logger, IStatefulResource resource, StartupAction action,
        List<Exception> list, CancellationToken cancellationToken)
    {
        try
        {
            await resource.Setup(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Ran Setup() on resource {Name} of type {Type}", resource.Name, resource.Type);

            if (action == StartupAction.ResetState)
            {
                await resource.ClearState(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Ran ClearState() on resource {Name} of type {Type}", resource.Name,
                    resource.Type);
            }
        }
        catch (Exception e)
        {
            var wrapped = new ResourceSetupException(resource, e);
            logger.LogError(e, "Failed to setup resource {Name} of type {Type}", resource.Name, resource.Type);

            list.Add(wrapped);
        }
    }
}