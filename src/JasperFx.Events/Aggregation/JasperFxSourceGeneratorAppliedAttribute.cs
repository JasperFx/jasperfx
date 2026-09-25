using System.Reflection;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Emitted into every assembly the JasperFx.Events source generator is attached to, whether or not
/// it found anything to generate. Its only job is to make "the generator never ran here"
/// distinguishable from "the generator ran and declined your type" — two failures with opposite
/// fixes that used to arrive as one nine-line paragraph. See jasperfx#887.
/// </summary>
/// <remarks>
/// <para>
/// <c>AllowMultiple</c> is true on purpose. The analyzer can be loaded twice over one compilation
/// when it arrives from two referenced packages (see jasperfx#462), and a second application of a
/// single-use assembly attribute is a compile error (CS0579) — a marker that breaks the build it
/// was added to diagnose would be worse than no marker.
/// </para>
/// <para>
/// Absence does not prove absence: an assembly built by a generator older than this attribute
/// carries no marker either. <see cref="SourceGeneratorMarker" /> resolves that as far as it can by
/// falling back to <see cref="GeneratedEvolverAttribute" />, and the wording it produces never
/// claims more than it knows.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class JasperFxSourceGeneratorAppliedAttribute : Attribute;

/// <summary>
/// What an assembly's metadata says about whether the JasperFx.Events source generator ran over it.
/// </summary>
public enum SourceGeneratorEvidence
{
    /// <summary>
    /// The generator left <see cref="JasperFxSourceGeneratorAppliedAttribute" /> — it ran over this
    /// assembly. Anything it did not generate, it declined.
    /// </summary>
    Confirmed,

    /// <summary>
    /// No marker, but the assembly carries <see cref="GeneratedEvolverAttribute" /> registrations, so
    /// a generator did run — one older than the marker. Same conclusion as
    /// <see cref="Confirmed" /> for diagnostic purposes, reported separately because the remedy may
    /// be a package bump.
    /// </summary>
    OlderGenerator,

    /// <summary>
    /// Neither a marker nor any generated registration. Most likely the analyzer never ran over this
    /// assembly — but an older generator that found no candidates leaves exactly this trace too, so
    /// nothing downstream should state it as fact.
    /// </summary>
    Unconfirmed
}

/// <summary>
/// Reads the marker jasperfx#887 added. Called only on a failure path — once, while composing an
/// exception message — so it reflects over the assembly's attributes rather than caching.
/// </summary>
public static class SourceGeneratorMarker
{
    /// <summary>
    /// What <paramref name="assembly" />'s metadata says about the generator having run over it.
    /// </summary>
    public static SourceGeneratorEvidence EvidenceFor(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (assembly.GetCustomAttributes<JasperFxSourceGeneratorAppliedAttribute>().Any())
        {
            return SourceGeneratorEvidence.Confirmed;
        }

        return assembly.GetCustomAttributes<GeneratedEvolverAttribute>().Any()
            ? SourceGeneratorEvidence.OlderGenerator
            : SourceGeneratorEvidence.Unconfirmed;
    }

    /// <summary>
    /// The half of a "no source-generated dispatcher" message that depends on whether the generator
    /// ran: a csproj problem and a code-shape problem need opposite fixes, and until jasperfx#887
    /// the message had to describe both at once because it could not tell which one the reader had.
    /// </summary>
    /// <param name="assemblies">
    /// The assemblies the runtime actually scans for a dispatcher — the aggregate's, and the
    /// projection's when it differs. Confirmation in any one of them means the generator ran where
    /// it could have emitted.
    /// </param>
    /// <param name="declinedTypeDescription">How to name the type the generator would have had to accept.</param>
    public static string DescribeGeneratorReach(IReadOnlyList<Assembly> assemblies, string declinedTypeDescription)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var evidence = assemblies.Select(EvidenceFor).ToArray();
        var names = assemblies.Select(x => x.GetName().Name ?? x.FullName ?? "<unknown>").ToArray();
        var subject = names.Length == 1 ? $"assembly {names[0]}" : $"assemblies {string.Join(" and ", names)}";

        if (evidence.Any(x => x != SourceGeneratorEvidence.Unconfirmed))
        {
            return $"The JasperFx.Events source generator DID run over {subject}, so it saw " +
                   $"{declinedTypeDescription} and declined it — this is a code-shape problem, not a build " +
                   "configuration one. Most projections do NOT need to be `partial` — neither a self-aggregating " +
                   "type registered via Snapshot<T> / SingleStreamProjection<T> / AggregateStream<T>, nor an " +
                   "aggregation projection subclass, whose dispatcher is generated as a separate type. `partial` " +
                   "is required only where the dispatcher has to be generated into the projection class itself: " +
                   "an EventProjection, or a projection whose conventional methods are instance methods and which " +
                   "has no public parameterless constructor (a DI-activated projection, for instance). The " +
                   "generator reports JFXEVT003 in those cases. Alternatively, override Evolve / EvolveAsync / " +
                   "DetermineAction / DetermineActionAsync directly.";
        }

        return $"The JasperFx.Events source generator left no trace in {subject} — no marker attribute and no " +
               "generated registration — so it most likely never ran there. Reference Marten (or " +
               "JasperFx.Events.SourceGenerator) directly from that project, and check the reference chain for " +
               "ExcludeAssets=\"analyzers\" or PrivateAssets=\"all\", which suppress the analyzer for downstream " +
               "projects while the build still succeeds with no warning. Aggregate types are usually plain POCOs " +
               "that name no store type, so such a project compiles perfectly happily with no analyzer at all. " +
               "(A generator older than this marker also leaves no trace when it finds no candidates, so if you " +
               "are certain the analyzer runs there, bump the JasperFx.Events / Marten package and look for a " +
               "JFXEVT diagnostic instead.)";
    }
}
