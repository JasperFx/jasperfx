using JasperFx.Environment;

namespace JasperFx.Resources;

internal class ResourceEnvironmentCheck : IEnvironmentCheck
{
    private readonly IStatefulResource _resource;

    public ResourceEnvironmentCheck(IStatefulResource resource)
    {
        _resource = resource;
    }

    public string Description => $"Resource {_resource.Name} ({_resource.Type})";

    public Task Assert(IServiceProvider services, CancellationToken cancellation)
    {
        return _resource.Check(cancellation);
    }
}