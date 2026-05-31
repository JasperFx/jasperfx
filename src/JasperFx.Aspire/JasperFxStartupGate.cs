using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace JasperFx.Aspire;

/// <summary>
/// Configures a single JasperFx startup gate — a run-to-completion Aspire resource that runs a
/// JasperFx verb (e.g. <c>resources setup</c>) and that the owning service waits on before starting.
/// </summary>
public sealed class JasperFxStartupGate
{
    /// <summary>
    /// Override the gate's Aspire resource name. Defaults to <c>"&lt;parent&gt;-&lt;verb&gt;-&lt;arg&gt;"</c>
    /// (e.g. <c>"api-resources-setup"</c>).
    /// </summary>
    public string? ResourceName { get; set; }

    /// <summary>
    /// Run this gate independently rather than chaining it after the previously declared gate. The
    /// owning service still waits for it, but it can run concurrently with the other gates. Defaults
    /// to <c>false</c> (gates run sequentially in declaration order).
    /// </summary>
    public bool Parallel { get; set; }

    /// <summary>
    /// Block the owning service from starting if this gate exits non-zero (fail fast). Defaults to
    /// <c>true</c>. Set to <c>false</c> for an advisory gate that runs but does not block startup.
    /// </summary>
    public bool BlockOnFailure { get; set; } = true;

    /// <summary>
    /// Only create the gate when this predicate returns true for the current execution context — e.g.
    /// <c>g.RunWhen = ctx =&gt; ctx.IsRunMode</c> to run a gate locally but not in a published deployment.
    /// Defaults to always running.
    /// </summary>
    public Func<DistributedApplicationExecutionContext, bool>? RunWhen { get; set; }

    /// <summary>
    /// Escape hatch to further configure the gate's underlying project resource (extra references,
    /// environment, args, …) after JasperFx.Aspire has built it.
    /// </summary>
    public Action<IResourceBuilder<ProjectResource>>? ConfigureGate { get; set; }
}
