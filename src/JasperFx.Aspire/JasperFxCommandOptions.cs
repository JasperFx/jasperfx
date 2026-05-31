namespace JasperFx.Aspire;

/// <summary>
/// Configures which JasperFx command-line verbs are projected as Aspire dashboard buttons, and
/// lets you tweak the presentation of individual verbs.
/// </summary>
public sealed class JasperFxCommandOptions
{
    private readonly Dictionary<string, JasperFxCommandRegistration> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit allow-list of verbs to project. When non-empty this replaces the default selection
    /// (read-only verbs plus, if opted in, the mutating verbs). Leave empty to use the defaults.
    /// </summary>
    public ISet<string> IncludeVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Verbs to remove from whatever selection is otherwise produced.</summary>
    public ISet<string> ExcludeVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opt in to the mutating verbs (<c>codegen write</c>, <c>resources</c>, <c>projections</c>),
    /// each gated behind a confirmation prompt. Defaults to <c>false</c> — only read-only verbs
    /// (<c>check-env</c>, <c>describe</c>, <c>codegen preview</c>) are added by default.
    /// </summary>
    public bool IncludeMutatingCommands { get; set; }

    /// <summary>
    /// Get (creating on first use) the presentation overrides for a verb so you can tweak its
    /// label, icon, or confirmation message, e.g.
    /// <c>opts.For("projections").ConfirmationMessage = "…"</c>.
    /// </summary>
    public JasperFxCommandRegistration For(string verb)
    {
        if (!_overrides.TryGetValue(verb, out var registration))
        {
            registration = new JasperFxCommandRegistration();
            _overrides[verb] = registration;
        }

        return registration;
    }

    internal JasperFxCommandRegistration OverrideFor(string verb)
        => _overrides.TryGetValue(verb, out var registration) ? registration : new JasperFxCommandRegistration();
}
