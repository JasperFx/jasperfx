#nullable enable
using System.Diagnostics.CodeAnalysis;
using JasperFx.Descriptors;

namespace JasperFx.Events.Projections;

public abstract class ProjectionBase : EventFilterable
{
    private readonly List<Type> _publishedTypes = new();

    protected ProjectionBase()
    {
        Name = GetType().Name;
    }
    
    
    protected void replaceOptions(AsyncOptions sourceOptions)
    {
        Options = sourceOptions;
    }


    [ChildDescription]
    public AsyncOptions Options { get; private set; } = new();

    /// <summary>
    ///     Descriptive name for this projection in the async daemon. The default is the type name of the projection
    /// </summary>
    [DisallowNull]
    public string Name { get; set; } 

    /// <summary>
    /// Specify that this projection is a non 1 version of the original projection definition to opt
    /// into any JasperFx Event Store's parallel blue/green deployment of this projection.
    /// </summary>
    public uint Version { get; set; } = 1;

    [Obsolete("Use Name instead.")]
    public string ProjectionName
    {
        get => Name;
        set => Name = value;
    }
    
    [Obsolete("Use Version instead.")]
    public uint ProjectionVersion
    {
        get => Version;
        set => Version = value;
    }

    /// <summary>
    ///     The projection lifecycle that governs when this projection is executed
    /// </summary>
    public ProjectionLifecycle Lifecycle { get; set; } = ProjectionLifecycle.Async;

    public virtual void AssembleAndAssertValidity()
    {
        // Nothing
    }

    /// <summary>
    ///     Just recording which document types are published by this projection
    /// </summary>
    /// <param name="publishedType"></param>
    protected void RegisterPublishedType(Type publishedType)
    {
        _publishedTypes.Add(publishedType);
    }

    public virtual IEnumerable<Type> PublishedTypes()
    {
        return _publishedTypes.Concat(Options.StorageTypes).Distinct().ToArray();
    }
}
