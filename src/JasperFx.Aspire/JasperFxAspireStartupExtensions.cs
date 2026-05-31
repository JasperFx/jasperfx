using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace JasperFx.Aspire;

/// <summary>
/// AppHost extension methods that run a JasperFx app's provisioning verbs (<c>resources setup</c>,
/// <c>codegen write</c>, opt-in <c>check-env</c>) as run-to-completion Aspire resources that finish
/// <em>before</em> the owning service starts, wired via <c>WaitForCompletion</c>. The orchestration-time
/// complement to <see cref="JasperFxAspireExtensions.WithJasperFxCommands{T}"/>.
/// </summary>
public static class JasperFxAspireStartupExtensions
{
    /// <summary>
    /// Add a JasperFx verb as a run-to-completion startup gate that the owning service waits on before
    /// starting. When <paramref name="arguments"/> is null a sensible default is used for known
    /// provisioning verbs (<c>resources</c> → <c>setup</c>, <c>codegen</c> → <c>write</c>).
    /// </summary>
    public static IResourceBuilder<T> WithJasperFxStartup<T>(
        this IResourceBuilder<T> builder,
        string verb,
        string? arguments = null,
        Action<JasperFxStartupGate>? gate = null)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        return ApplyGates(builder, [new JasperFxGateSpec(verb, arguments, gate)]);
    }

    /// <summary>
    /// Declare several JasperFx startup gates with ordering control. Gates run in declaration order
    /// (each waiting for the previous) unless a gate opts into <see cref="JasperFxStartupGate.Parallel"/>.
    /// </summary>
    public static IResourceBuilder<T> WithJasperFxStartup<T>(
        this IResourceBuilder<T> builder,
        Action<JasperFxStartupBuilder> configure)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        var startup = new JasperFxStartupBuilder();
        configure(startup);
        return ApplyGates(builder, startup.Specs);
    }

    private static IResourceBuilder<T> ApplyGates<T>(
        IResourceBuilder<T> parent, IReadOnlyList<JasperFxGateSpec> specs)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        if (!parent.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            throw new InvalidOperationException(
                $"WithJasperFxStartup requires a project resource; '{parent.Resource.Name}' has no project metadata. " +
                "Add the gate to a resource created with AddProject<T>(...).");
        }

        // Snapshot the parent's reference/env annotations ONCE, before any gate wiring. WaitForCompletion
        // adds WaitAnnotations to the parent as we go; cloning from a pre-wiring snapshot keeps those from
        // leaking onto later gates.
        var envAnnotations = parent.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray();
        var waitAnnotations = parent.Resource.Annotations.OfType<WaitAnnotation>().ToArray();
        var relationshipAnnotations = parent.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ToArray();

        var executionContext = parent.ApplicationBuilder.ExecutionContext;
        IResourceBuilder<ProjectResource>? previousSequentialGate = null;

        foreach (var spec in specs)
        {
            var gate = new JasperFxStartupGate();
            spec.Configure?.Invoke(gate);

            if (gate.RunWhen != null && !gate.RunWhen(executionContext))
            {
                continue; // environment-gated out (e.g. local-only)
            }

            var arguments = spec.Arguments ?? DefaultGateArguments(spec.Verb);
            var gateName = gate.ResourceName ?? BuildGateName(parent.Resource.Name, spec.Verb, arguments);

            var gateBuilder = parent.ApplicationBuilder
                .AddProject(gateName, projectMetadata.ProjectPath)
                .WithArgs(BuildArgs(spec.Verb, arguments));

            CloneReferences(gateBuilder.Resource, envAnnotations, waitAnnotations, relationshipAnnotations);

            gate.ConfigureGate?.Invoke(gateBuilder);

            // Sequential ordering: this gate waits for the previously declared (non-parallel) gate.
            if (!gate.Parallel && previousSequentialGate != null)
            {
                gateBuilder.WaitForCompletion(previousSequentialGate);
            }

            // The owning service waits for the gate to finish with exit 0 (fail fast). An advisory gate
            // (BlockOnFailure = false) still runs but does not block startup.
            if (gate.BlockOnFailure)
            {
                parent.WaitForCompletion(gateBuilder);
            }

            if (!gate.Parallel)
            {
                previousSequentialGate = gateBuilder;
            }
        }

        return parent;
    }

    private static void CloneReferences(
        IResource gate,
        IEnumerable<EnvironmentCallbackAnnotation> envAnnotations,
        IEnumerable<WaitAnnotation> waitAnnotations,
        IEnumerable<ResourceRelationshipAnnotation> relationshipAnnotations)
    {
        // The gate is the same binary as the parent, so it needs the same connection strings / env and
        // should wait for the same dependencies. Re-apply the parent's reference annotations.
        foreach (var annotation in envAnnotations)
        {
            gate.Annotations.Add(annotation);
        }
        foreach (var annotation in waitAnnotations)
        {
            gate.Annotations.Add(annotation);
        }
        foreach (var annotation in relationshipAnnotations)
        {
            gate.Annotations.Add(annotation);
        }
    }

    // Default arguments for the provisioning verbs used as startup gates. Distinct from A1's catalog
    // defaults (where `codegen` defaults to the read-only `preview`); a startup gate wants `codegen write`.
    internal static string? DefaultGateArguments(string verb) => verb.ToLowerInvariant() switch
    {
        "resources" => "setup",
        "codegen" => "write",
        _ => null
    };

    internal static string BuildGateName(string parentName, string verb, string? arguments)
    {
        var parts = new List<string> { parentName, verb };
        parts.AddRange(Tokenize(arguments));
        return string.Join('-', parts).ToLowerInvariant();
    }

    internal static string[] BuildArgs(string verb, string? arguments)
    {
        var args = new List<string> { verb };
        args.AddRange(Tokenize(arguments));
        return args.ToArray();
    }

    private static IEnumerable<string> Tokenize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
