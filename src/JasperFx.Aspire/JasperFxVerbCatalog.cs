namespace JasperFx.Aspire;

/// <summary>
/// A single dashboard-command template: a JasperFx verb (plus any fixed arguments) and the
/// default presentation metadata for its Aspire button. <see cref="ConfirmationMessage"/> may
/// contain a single <c>{0}</c> placeholder which is filled with the resource name at registration.
/// </summary>
internal sealed record JasperFxCommandTemplate(
    string Key,
    string Verb,
    string? Arguments,
    bool Mutating,
    string DisplayName,
    string Description,
    string IconName,
    string? ConfirmationMessage);

/// <summary>
/// The curated, statically-known catalog of standard JasperFx command-line verbs that make sense
/// as Aspire dashboard buttons. Phase 1 ships this knowledge directly so the integration works for
/// any JasperFx app with zero runtime coupling; a later phase can discover verbs dynamically from
/// the target via a machine-readable <c>describe --json</c>.
/// </summary>
internal static class JasperFxVerbCatalog
{
    /// <summary>Read-only verbs added by <c>WithJasperFxCommands()</c> with no opt-in.</summary>
    public static readonly IReadOnlyList<JasperFxCommandTemplate> ReadOnly =
    [
        new("jasperfx-check-env", "check-env", null, false,
            "Check environment",
            "Run all of the application's environment checks.",
            "CheckmarkCircle",
            null),

        new("jasperfx-describe", "describe", null, false,
            "Describe",
            "Write out a description of the application's configuration to the logs.",
            "DocumentBulletList",
            null),

        new("jasperfx-codegen-preview", "codegen", "preview", false,
            "Preview generated code",
            "Preview the code JasperFx would generate at runtime (read-only).",
            "Code",
            null)
    ];

    /// <summary>Mutating verbs added only when <see cref="JasperFxCommandOptions.IncludeMutatingCommands"/> is set.</summary>
    public static readonly IReadOnlyList<JasperFxCommandTemplate> Mutating =
    [
        new("jasperfx-codegen-write", "codegen", "write", true,
            "Write generated code",
            "Generate the runtime code ahead of time and write it to disk.",
            "Code",
            "Write the generated code for '{0}' to disk? This overwrites the generated source files."),

        new("jasperfx-resources", "resources", "setup", true,
            "Apply resources",
            "Create or update the stateful resources (database schema, queues, tables, …) this application needs.",
            "DatabasePlugConnected",
            "Apply resource setup for '{0}'? This creates/updates databases, schema objects, and other infrastructure."),

        new("jasperfx-projections", "projections", "rebuild", true,
            "Rebuild projections",
            "Rebuild all asynchronous projections from the event store.",
            "ArrowSync",
            "Rebuild all projections for '{0}'? This reprocesses the entire event store and may take a while.")
    ];

    public static IEnumerable<JasperFxCommandTemplate> All => ReadOnly.Concat(Mutating);

    /// <summary>
    /// Resolve the set of command templates to register for the given options:
    /// the read-only defaults (plus mutating verbs when opted in), or an explicit
    /// <see cref="JasperFxCommandOptions.IncludeVerbs"/> allow-list, minus
    /// <see cref="JasperFxCommandOptions.ExcludeVerbs"/>.
    /// </summary>
    public static IEnumerable<JasperFxCommandTemplate> Resolve(JasperFxCommandOptions options)
    {
        IEnumerable<JasperFxCommandTemplate> selected;

        if (options.IncludeVerbs.Count > 0)
        {
            selected = All.Where(t => options.IncludeVerbs.Contains(t.Verb));
        }
        else
        {
            selected = options.IncludeMutatingCommands ? All : ReadOnly;
        }

        return selected.Where(t => !options.ExcludeVerbs.Contains(t.Verb)).ToArray();
    }

    /// <summary>
    /// Verbs that are never rendered as dashboard buttons (the long-running service itself / help).
    /// </summary>
    private static readonly HashSet<string> NonButtonVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "help"
    };

    /// <summary>
    /// Map a set of verbs discovered from the target app (via <c>help --json</c>) to command templates —
    /// one button per verb. Known verbs get their catalog metadata (correct mutating flag, icon); unknown
    /// product-specific verbs are treated as mutating. Honors the same include/exclude/mutating gating as
    /// <see cref="Resolve"/>; <c>run</c> and <c>help</c> are never included.
    /// </summary>
    public static IEnumerable<JasperFxCommandTemplate> ResolveDiscovered(
        IEnumerable<string> verbs, JasperFxCommandOptions options)
    {
        var templates = verbs
            .Where(v => !NonButtonVerbs.Contains(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(v => TemplateFor(v, null));

        if (options.IncludeVerbs.Count > 0)
        {
            templates = templates.Where(t => options.IncludeVerbs.Contains(t.Verb));
        }
        else if (!options.IncludeMutatingCommands)
        {
            templates = templates.Where(t => !t.Mutating);
        }

        return templates.Where(t => !options.ExcludeVerbs.Contains(t.Verb)).ToArray();
    }

    /// <summary>
    /// Find the catalog template for a verb (optionally matching fixed arguments), or synthesize a
    /// generic one for an unknown/product-specific verb so <c>WithJasperFxCommand</c> still works.
    /// </summary>
    public static JasperFxCommandTemplate TemplateFor(string verb, string? arguments)
    {
        var match = All.FirstOrDefault(t =>
            string.Equals(t.Verb, verb, StringComparison.OrdinalIgnoreCase) &&
            (arguments == null || string.Equals(t.Arguments, arguments, StringComparison.OrdinalIgnoreCase)));

        if (match != null)
        {
            return arguments == null ? match : match with { Arguments = arguments };
        }

        var key = arguments.IsNotEmpty()
            ? $"jasperfx-{verb}-{arguments!.Replace(' ', '-')}"
            : $"jasperfx-{verb}";

        var display = Capitalize(verb.Replace('-', ' '));

        return new JasperFxCommandTemplate(
            key,
            verb,
            arguments,
            // Unknown verbs are treated as mutating (safe-by-default → require confirmation).
            Mutating: true,
            DisplayName: display,
            Description: $"Run the JasperFx '{verb}' command.",
            IconName: "Console",
            ConfirmationMessage: $"Run '{verb}{(arguments.IsNotEmpty() ? " " + arguments : "")}' against '{{0}}'?");
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static bool IsNotEmpty(this string? value) => !string.IsNullOrWhiteSpace(value);
}
