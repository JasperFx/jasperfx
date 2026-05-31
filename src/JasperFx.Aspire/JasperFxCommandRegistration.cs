using Aspire.Hosting.ApplicationModel;

namespace JasperFx.Aspire;

/// <summary>
/// Per-verb presentation overrides for a JasperFx dashboard command. Every property is optional;
/// anything left null falls back to the curated catalog default for that verb.
/// </summary>
public sealed class JasperFxCommandRegistration
{
    /// <summary>Button label shown on the resource tile. Defaults to the catalog display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Longer description shown in the dashboard. Defaults to the catalog description.</summary>
    public string? DisplayDescription { get; set; }

    /// <summary>
    /// Fluent UI Blazor icon name (e.g. <c>"CheckmarkCircle"</c>). Defaults to the catalog icon.
    /// </summary>
    public string? IconName { get; set; }

    /// <summary>
    /// Confirmation prompt shown before the command runs. Defaults to the catalog confirmation for
    /// mutating verbs (read-only verbs have none). Set to <c>null</c> via the catalog default; set a
    /// value here to force a confirmation on any verb.
    /// </summary>
    public string? ConfirmationMessage { get; set; }

    /// <summary>Render the button as highlighted/primary. Defaults to <c>false</c>.</summary>
    public bool? IsHighlighted { get; set; }

    /// <summary>
    /// Override fixed CLI arguments passed after the verb (e.g. <c>"setup"</c>, <c>"write"</c>).
    /// Only honored by <see cref="JasperFxAspireExtensions.WithJasperFxCommand{T}"/>.
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// Override the dashboard enabled/disabled state callback. Defaults to "enabled while the
    /// resource is running". Provided the raw Aspire context so power users have full control.
    /// </summary>
    public Func<UpdateCommandStateContext, ResourceCommandState>? UpdateState { get; set; }
}
