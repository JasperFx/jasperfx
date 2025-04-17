using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Spectre.Console.Rendering;

namespace CommandLineTests.Resources;

public abstract class ResourceCommandContext : SystemPartBase
{
    private IServiceCollection _services = new ServiceCollection();
    protected readonly List<IStatefulResource> AllResources = new();
    protected ResourceInput theInput = new ResourceInput();
    private readonly List<IStatefulResource> _resources = new();

    protected ResourceCommandContext() : base(nameof(ResourceCommandContext), new Uri("resources://" + Guid.NewGuid().ToString()))
    {
    }

    public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
    {
        return new ValueTask<IReadOnlyList<IStatefulResource>>(_resources);
    }

    internal void CopyResources(IServiceCollection services)
    {
        services.AddRange(_services);
    }

    internal Task<IHost> buildHost()
    {
        return Host.CreateDefaultBuilder().ConfigureServices(CopyResources).StartAsync();
    }
        
    internal Task<IList<IStatefulResource>> applyTheResourceFiltering()
    {
        theInput.HostBuilder = Host.CreateDefaultBuilder().ConfigureServices(CopyResources);
        var command = new ResourcesCommand();
        using var host = theInput.BuildHost();

        return command.FindResources(theInput, host);
    }
        
    internal async Task theCommandExecutionShouldSucceed()
    {
        theInput.HostBuilder = Host.CreateDefaultBuilder().ConfigureServices(CopyResources);
        var returnCode = await new ResourcesCommand().Execute(theInput);
            
        returnCode.ShouldBeTrue();
    }
        
    internal async Task theCommandExecutionShouldFail()
    {
        theInput.HostBuilder = Host.CreateDefaultBuilder().ConfigureServices(s => s.AddRange(_services));
        var returnCode = await new ResourcesCommand().Execute(theInput);
            
        returnCode.ShouldBeFalse();
    }
        
        
    internal IStatefulResource CreateResource(string name, string type = "Resource")
    {
        var resource = Substitute.For<IStatefulResource>();
        resource.Name.Returns(name);
        resource.Type.Returns(type);
            
        AllResources.Add(resource);

        return resource;
    }

    internal void AddSource(Action<ResourceCollection> configure)
    {
        var collection = new ResourceCollection(this);
        configure(collection);

        AllResources.Fill(collection.Resources);

        _services.AddSingleton<ISystemPart>(collection);
    }

    internal IStatefulResource AddResource(string name, string type = "Resource")
    {
        var resource = CreateResource(name, type);
        
        _resources.Add(resource);

        if (!_services.Any(x => !x.IsKeyedService && x.ImplementationInstance == this))
        {
            _services.AddSingleton<ISystemPart>(this);
        }
        
        return resource;
    }
        
    internal IStatefulResource AddResourceWithDependencies(string name, string type, params string[] dependencyNames)
    {
        var resource = new ResourceWithDependencies
        {
            Name = name,
            Type = type,
            DependencyNames = dependencyNames
        };
        
        _resources.Add(resource);

        if (!_services.Any(x => !x.IsKeyedService && x.ImplementationInstance == this))
        {
            _services.AddSingleton<ISystemPart>(this);
        }

        return resource;
    }

    public class ResourceCollection : SystemPartBase
    {
        private readonly ResourceCommandContext _parent;

        public ResourceCollection(ResourceCommandContext parent) : base(Guid.NewGuid().ToString(), new Uri("resources://" + Guid.NewGuid().ToString()))
        {
            _parent = parent;
        }

        public List<IStatefulResource> Resources { get; } = [];

        public IStatefulResource Add(string name, string type = "Resource")
        {
            var resource = _parent.CreateResource(name, type);
            Resources.Add(resource);

            return resource;
        }

        public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
        {
            return new ValueTask<IReadOnlyList<IStatefulResource>>(Resources);
        }
    }
}

public class ResourceWithDependencies : IStatefulResourceWithDependencies
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string[] DependencyNames { get; set; }

    public Uri SubjectUri { get; } = new Uri("subject://dependencies");
    public Uri ResourceUri { get; } = new Uri("resource://dependencies");

    public Task Check(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task ClearState(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task Teardown(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task Setup(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task<IRenderable> DetermineStatus(CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IStatefulResource> FindDependencies(IReadOnlyList<IStatefulResource> others)
    {
        return others.Where(x => DependencyNames.Contains(x.Name));
    }
}