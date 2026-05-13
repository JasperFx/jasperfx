using JasperFx.Descriptors;
using JasperFx.Environment;
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

    Uri SubjectUri { get; }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Default implementation derives an OptionsDescription via reflection over the runtime type's public properties; consumers writing custom system-part descriptions reflectively must keep their properties alive.")]
    Task WriteToConsole();

    ValueTask<IReadOnlyList<IStatefulResource>> FindResources();

    Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results, CancellationToken token);
}

public abstract class SystemPartBase : ISystemPart
{
    public string Title { get; }
    public Uri SubjectUri { get; }

    protected SystemPartBase(string title, Uri subjectUri)
    {
        Title = title;
        SubjectUri = subjectUri;
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Derives an OptionsDescription via reflection over this instance's runtime type's public properties.")]
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

    public virtual Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
