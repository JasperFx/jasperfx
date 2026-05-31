namespace JasperFx.Aspire;

/// <summary>
/// Fluent builder for declaring several JasperFx startup gates with ordering control. Gates run in
/// the order they are declared here (unless a gate opts into <see cref="JasperFxStartupGate.Parallel"/>).
/// </summary>
public sealed class JasperFxStartupBuilder
{
    internal List<JasperFxGateSpec> Specs { get; } = [];

    /// <summary>
    /// Declare a startup gate for a JasperFx verb (e.g. <c>Run("resources", "setup")</c>). When
    /// <paramref name="arguments"/> is null a sensible default is used for known provisioning verbs
    /// (<c>resources</c> → <c>setup</c>, <c>codegen</c> → <c>write</c>).
    /// </summary>
    public JasperFxStartupBuilder Run(string verb, string? arguments = null, Action<JasperFxStartupGate>? gate = null)
    {
        Specs.Add(new JasperFxGateSpec(verb, arguments, gate));
        return this;
    }

    /// <summary>
    /// Declare a <c>check-env</c> startup gate. Blocks startup on a failed environment check
    /// (<see cref="JasperFxStartupGate.BlockOnFailure"/> defaults to true).
    /// </summary>
    public JasperFxStartupBuilder Check(Action<JasperFxStartupGate>? gate = null)
    {
        Specs.Add(new JasperFxGateSpec("check-env", null, gate));
        return this;
    }
}

internal sealed record JasperFxGateSpec(string Verb, string? Arguments, Action<JasperFxStartupGate>? Configure);
