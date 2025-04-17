using Spectre.Console;
using Spectre.Console.Rendering;

namespace JasperFx.Resources;

/// <summary>
///     Base class with empty implementations for IStatefulResource.
/// </summary>
public abstract class StatefulResourceBase : IStatefulResource
{
    protected StatefulResourceBase(string type, string name, Uri subjectUri, Uri resourceUri)
    {
        Type = type;
        Name = name;
        SubjectUri = subjectUri;
        ResourceUri = resourceUri;
    }

    public virtual Task Check(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public virtual Task ClearState(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public virtual Task Teardown(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public virtual Task Setup(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public virtual Task<IRenderable> DetermineStatus(CancellationToken token)
    {
        return Task.FromResult((IRenderable)new Markup("Okay"));
    }

    public string Type { get; }
    public string Name { get; }
    public Uri SubjectUri { get; }
    public Uri ResourceUri { get; }
}