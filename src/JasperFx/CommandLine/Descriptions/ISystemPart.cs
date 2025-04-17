using JasperFx.Core.Descriptors;
using JasperFx.Resources;

namespace JasperFx.CommandLine.Descriptions;

/// <summary>
///     Base class for a "described" part of your application.
///     Implementations of this type should be registered in your
///     system's DI container to be exposed through the "describe"
///     command
/// </summary>
public interface ISystemPart
{
    /// <summary>
    ///     A descriptive title to be shown in the rendered output
    /// </summary>
    string Title { get; }
    
    Task WriteToConsole();
    
    ValueTask<IReadOnlyList<IStatefulResource>> FindResources();
}

public abstract class SystemPartBase : ISystemPart
{
    public string Title { get; }

    protected SystemPartBase(string title)
    {
        Title = title;
    }

    public virtual Task WriteToConsole()
    {
        var description = OptionsDescription.For(this);
        OptionDescriptionWriter.Write(description);

        return Task.CompletedTask;
    }

    public virtual ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
    {
        return new ValueTask<IReadOnlyList<IStatefulResource>>([]);
    }
}
