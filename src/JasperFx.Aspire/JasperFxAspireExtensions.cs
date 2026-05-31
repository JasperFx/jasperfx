using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace JasperFx.Aspire;

/// <summary>
/// AppHost extension methods that surface a JasperFx application's command-line verbs
/// (<c>check-env</c>, <c>describe</c>, <c>codegen</c>, <c>resources</c>, <c>projections</c>) as
/// clickable custom commands on the resource in the .NET Aspire dashboard.
/// </summary>
public static class JasperFxAspireExtensions
{
    /// <summary>
    /// Add the curated, safe-by-default set of JasperFx command buttons to a project resource.
    /// By default this adds the read-only verbs (<c>check-env</c>, <c>describe</c>, and
    /// <c>codegen</c> preview). Set <see cref="JasperFxCommandOptions.IncludeMutatingCommands"/> to
    /// also add the mutating verbs (each behind a confirmation prompt).
    /// </summary>
    public static IResourceBuilder<T> WithJasperFxCommands<T>(
        this IResourceBuilder<T> builder,
        Action<JasperFxCommandOptions>? configure = null)
        where T : IResourceWithEnvironment
    {
        var options = new JasperFxCommandOptions();
        configure?.Invoke(options);

        foreach (var template in ResolveTemplates(builder.Resource, options))
        {
            RegisterCommand(builder, template, options.OverrideFor(template.Verb));
        }

        return builder;
    }

    private static IEnumerable<JasperFxCommandTemplate> ResolveTemplates(
        IResource resource, JasperFxCommandOptions options)
    {
        // Opt-in dynamic discovery: ask the target what verbs it actually has (picks up product-specific
        // and custom commands). Best-effort — any failure falls back to the curated catalog below.
        if (options.DiscoverCommands && resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            var discovered = JasperFxCommandDiscovery.Discover(projectMetadata.ProjectPath, options.DiscoveryTimeout);
            if (discovered != null)
            {
                return JasperFxVerbCatalog.ResolveDiscovered(discovered, options);
            }
        }

        return JasperFxVerbCatalog.Resolve(options);
    }

    /// <summary>
    /// Add a single JasperFx verb as a dashboard button, with optional fixed arguments. Works for
    /// the standard verbs and for product-specific or user-defined commands (unknown verbs are
    /// treated as mutating and get a confirmation prompt by default).
    /// </summary>
    public static IResourceBuilder<T> WithJasperFxCommand<T>(
        this IResourceBuilder<T> builder,
        string verb,
        string? arguments = null,
        Action<JasperFxCommandRegistration>? configure = null)
        where T : IResourceWithEnvironment
    {
        var registration = new JasperFxCommandRegistration { Arguments = arguments };
        configure?.Invoke(registration);

        var template = JasperFxVerbCatalog.TemplateFor(verb, registration.Arguments);
        RegisterCommand(builder, template, registration);

        return builder;
    }

    private static void RegisterCommand<T>(
        IResourceBuilder<T> builder,
        JasperFxCommandTemplate template,
        JasperFxCommandRegistration registration)
        where T : IResourceWithEnvironment
    {
        var resource = builder.Resource;
        var arguments = registration.Arguments ?? template.Arguments;

        var displayName = registration.DisplayName ?? template.DisplayName;
        var description = registration.DisplayDescription ?? template.Description;
        var iconName = registration.IconName ?? template.IconName;
        var confirmationMessage = ResolveConfirmation(template, registration, resource.Name);
        var updateState = registration.UpdateState ?? DefaultUpdateState;

        var executor = new JasperFxCommandExecutor(resource, template.Verb, arguments);

        builder.WithCommand(
            name: template.Key,
            displayName: displayName,
            executeCommand: executor.ExecuteAsync,
            commandOptions: new CommandOptions
            {
                Description = description,
                IconName = iconName,
                IconVariant = IconVariant.Regular,
                IsHighlighted = registration.IsHighlighted ?? false,
                ConfirmationMessage = confirmationMessage,
                UpdateState = updateState
            });
    }

    private static string? ResolveConfirmation(
        JasperFxCommandTemplate template, JasperFxCommandRegistration registration, string resourceName)
    {
        // An explicit override wins verbatim; the catalog default is formatted with the resource name.
        if (registration.ConfirmationMessage != null)
        {
            return registration.ConfirmationMessage;
        }

        return template.ConfirmationMessage == null
            ? null
            : string.Format(CultureInfo.InvariantCulture, template.ConfirmationMessage, resourceName);
    }

    // Enabled while the resource is running; disabled otherwise so the child process has something
    // to run against. Power users can override via JasperFxCommandRegistration.UpdateState.
    private static ResourceCommandState DefaultUpdateState(UpdateCommandStateContext context)
    {
        var state = context.ResourceSnapshot.State?.Text;
        return string.Equals(state, KnownResourceStates.Running, StringComparison.OrdinalIgnoreCase)
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }
}
