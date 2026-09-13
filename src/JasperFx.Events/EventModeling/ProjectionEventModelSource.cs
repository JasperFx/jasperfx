using JasperFx.Descriptors;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// The store-derived rung: one <see cref="SlicePattern.View"/> slice per registered projection,
/// read straight out of the event store's own registry (jasperfx#825).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this fills.</b> Bobcat declares slices, Wolverine derives Command and Automation slices
/// from its chains, CritterWatch observes a running system — and <em>nobody derived from the
/// store</em>. There was no Marten-, Polecat- or Fisher-derived event model source at all, so
/// <see cref="EventModelSliceDescriptor.ProjectionTypes"/> was populated only by declarations and by
/// CritterWatch's generator, and a View slice — event → projection → read model — appeared on a canvas
/// only when a human had written one down.
/// </para>
/// <para>
/// Yet the store knows this exactly: every registered projection, its lifecycle, the document it
/// produces, and the event types its <c>Apply</c> / <c>Create</c> / <c>Evolve</c> methods take. That
/// is the whole role set of a State View slice, derivable with no guessing.
/// </para>
/// <para>
/// <b>Store-agnostic by construction.</b> Everything below comes from
/// <see cref="IEventStore.TryCreateUsage"/> and <see cref="SubscriptionDescriptor"/>, which every
/// store already fills through shared code in this assembly — so no store needs its own copy of this,
/// and the applied-event set can never disagree with the one
/// <see cref="AggregateDescriptor.AppliedEvents"/> reports, because the same reader feeds both.
/// </para>
/// <para>
/// <b>Slices are named after the document type</b>, which is how Bobcat's <c>{readmodel}</c> capture
/// and the curated model file already name View slices. That is deliberate rather than convenient: a
/// spec-declared <c>AccountBalance</c> slice and the store-derived one then <b>merge by name</b> into
/// one slice carrying both a <see cref="EventModelProvenance.Declared"/> and a
/// <see cref="EventModelProvenance.Derived"/> claim, rather than showing up as two stickies that say
/// the same thing.
/// </para>
/// <para>
/// <b>Every slice carries the store it came from</b> in
/// <see cref="EventModelSliceDescriptor.Origin"/> (jasperfx#836), so naming slices after a document's
/// simple name stays safe in a modular monolith: when two stores project an <c>AuditEntry</c> apiece,
/// the merge still folds them into one slice, but the drop is now a
/// <see cref="HotspotOrigin.SourceDisagreement"/> hotspot naming both stores rather than one of them
/// vanishing without trace.
/// </para>
/// </remarks>
public sealed class ProjectionEventModelSource : IEventModelDefinitionSource
{
    private readonly Func<IServiceProvider, IEnumerable<IEventStore>> _stores;

    /// <summary>
    /// Read the projections of every <see cref="IEventStore" /> resolvable from the container.
    /// </summary>
    /// <remarks>
    /// Resolution is a constructor argument rather than a fixed <c>GetServices&lt;IEventStore&gt;()</c>
    /// because a store registers itself under its own interface — Marten's <c>IDocumentStore</c>,
    /// Polecat's and Fisher's equivalents — and an ancillary store registers under a marker type
    /// again. The store's own <c>AddXxx</c> is the only place that knows which, so it supplies the
    /// resolver; the default covers a host that registered the shared interface directly.
    /// </remarks>
    public ProjectionEventModelSource()
        : this(services => services.GetServices<IEventStore>())
    {
    }

    /// <inheritdoc cref="ProjectionEventModelSource()" />
    /// <param name="stores">Resolves the event stores whose projections to describe.</param>
    public ProjectionEventModelSource(Func<IServiceProvider, IEnumerable<IEventStore>> stores)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
    }

    /// <summary>One store, already resolved. The shape a store's own registration uses.</summary>
    public ProjectionEventModelSource(IEventStore store)
        : this(_ => [store ?? throw new ArgumentNullException(nameof(store))])
    {
    }

    /// <summary>The model name used when a caller does not choose one.</summary>
    public const string DefaultModelName = "EventModel";

    /// <summary>
    /// Name of the model these slices contribute to. Must match what the other sources call it, since
    /// <see cref="EventModelDiscovery.Assemble" /> groups descriptors by name before merging slices.
    /// </summary>
    public string ModelName { get; init; } = DefaultModelName;

    /// <summary>The subject used when a caller does not supply one.</summary>
    public static readonly Uri DefaultSubject = new("event-model://projections");

    /// <summary>
    /// Identifies this source. A store registering its own instance should mint a distinct subject
    /// per store (<c>event-model://projections/{store}</c>), because it is the fallback attribution
    /// stamped onto <see cref="EventModelSliceDescriptor.Origin"/> when a store's usage carries no
    /// <c>SubjectUri</c> of its own (jasperfx#836).
    /// </summary>
    public Uri Subject { get; init; } = DefaultSubject;

    /// <summary>
    /// <see cref="EventModelProvenance.Derived" />: these roles are read out of the store's
    /// registry, not written down by anybody. That is what lets them win over a declaration that
    /// disagrees, and what makes a disagreement a recorded hotspot rather than a silent drop.
    /// </summary>
    public EventModelProvenance Provenance => EventModelProvenance.Derived;

    public async Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var slices = new List<EventModelSliceDescriptor>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var store in _stores(services))
        {
            if (store is null) continue;

            var usage = await store.TryCreateUsage(token).ConfigureAwait(false);

            // A store that cannot describe itself is a legitimate answer, and contributing nothing is
            // the right response to it -- an empty slice list would read as "this store has no
            // projections", which is a different and wrong claim.
            if (usage is null) continue;

            // jasperfx#836. The store's own subject is the precise attribution and the one the
            // telemetry half of the stack already keys on; the source subject covers a store whose
            // usage carries none, and is per-store whenever the store registered its own source.
            var origin = usage.SubjectUri ?? Subject;

            foreach (var subscription in usage.Subscriptions)
            {
                if (ToSlice(subscription) is not { } slice) continue;

                slice = slice with { Origin = origin };

                if (!indexByName.TryGetValue(slice.Name, out var index))
                {
                    indexByName[slice.Name] = slices.Count;
                    slices.Add(slice);
                    continue;
                }

                var kept = slices[index];

                // Two views of ONE store's registration agree by construction, so folding them would
                // record a disagreement between a thing and itself. Taking the first is still right
                // there, and still silent.
                if (Equals(kept.Origin, origin)) continue;

                // Two DIFFERENT stores projecting a document of the same simple name is ordinary in a
                // modular monolith and is a real loss: the slices merge by name, so one of them
                // disappears from the canvas. Every other merge in the system records a dropped claim
                // (jasperfx#704) -- this one used to be the exception (jasperfx#836).
                slices[index] = kept with
                {
                    Hotspots = [..kept.Hotspots, HotspotDescriptor.SourceDisagreement(
                        EventModelRole.Origin,
                        new EventModelClaim(Provenance, kept.Origin!.OriginalString),
                        new EventModelClaim(Provenance, origin.OriginalString))],
                };
            }
        }

        return slices.Count == 0 ? null : new EventModelDescriptor(ModelName, slices);
    }

    /// <summary>
    /// The View slice for one registered projection, or null when the registration is not one —
    /// a bare subscription, or a projection with no document type to name a slice after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public and static so a store, a test or a tool can run the mapping over a descriptor it already
    /// holds without standing up a container.
    /// </para>
    /// <para>
    /// <b>A subscription is not a View slice.</b> <see cref="SubscriptionType.Subscription" /> has no
    /// read model and produces no document — it is a side effect, and inventing a green sticky for it
    /// would put something on the canvas that a reader cannot click through to. Same for a projection
    /// whose document type the store could not report: naming a slice after the projection class
    /// instead would break the merge-by-name with a declared slice, which is the one thing this source
    /// exists to get right.
    /// </para>
    /// <para>
    /// <b>Stream lifecycle events are dropped</b> — see <see cref="IsStreamLifecycleEvent" />. Same
    /// argument one row down: a slice role is what a reader can click through to, which is a narrower
    /// question than what the registry holds.
    /// </para>
    /// </remarks>
    public static EventModelSliceDescriptor? ToSlice(SubscriptionDescriptor subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.SubscriptionType == SubscriptionType.Subscription) return null;
        if (subscription.AggregateType is not { } document) return null;

        return EventModelSliceDescriptor.Named(document.Name) with
        {
            Pattern = SlicePattern.View,
            ProjectionTypes = subscription.ImplementationType is { } projection ? [projection] : [],
            ReadModelTypes = [document],
            ConsumedEvents = subscription.AppliedEvents.Where(x => !IsStreamLifecycleEvent(x)).ToArray(),
        };
    }

    private static readonly string _lifecycleAssembly = typeof(Archived).Assembly.GetName().Name ?? string.Empty;

    /// <summary>
    /// Is this one of the stream lifecycle events every aggregation projection handles whether or not
    /// the aggregate declares an <c>Apply</c> for it — <see cref="Archived" /> and
    /// <see cref="Compacted{T}" /> (jasperfx#829)?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JasperFxSingleStreamProjectionBase.determineEventTypes()</c> concatenates both onto every
    /// non-empty apply set, so an aggregate declaring exactly <c>Apply(Credited)</c> and
    /// <c>Apply(Debited)</c> reports four event types. That is <em>correct</em> about what the
    /// projection handles, and it is not what an Event Model canvas means by the events a read model
    /// consumes: they render as orange stickies for events the application never wrote, which no
    /// command slice emits, and which therefore link to nothing.
    /// </para>
    /// <para>
    /// <b>This is a ruling, and the filter sits here deliberately</b> rather than in the reader that
    /// fills <see cref="SubscriptionDescriptor.AppliedEvents" />. "These are synthetic, drop them" is
    /// not quite true — <see cref="Compacted{T}" /> is a real stored event on every store — and a
    /// monitoring console asking "what does this projection handle" genuinely wants both. The same
    /// reader also feeds <see cref="AggregateDescriptor.AppliedEvents" />, so filtering there would
    /// change an answer two other consumers rely on. What is narrow is the <em>canvas</em> question,
    /// so the judgement belongs with the thing that knows it is drawing one.
    /// </para>
    /// <para>
    /// Matched on identity rather than on a name list: the assembly is
    /// <see cref="Archived" />'s own, and the generic name comes from <c>typeof(Compacted&lt;&gt;)</c>,
    /// so a rename upstream cannot leave a stale string behind here. An application type that happens
    /// to be called <c>Archived</c> is unaffected, because it is in the application's assembly.
    /// </para>
    /// </remarks>
    public static bool IsStreamLifecycleEvent(TypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!string.Equals(type.AssemblyName, _lifecycleAssembly, StringComparison.Ordinal)) return false;

        return string.Equals(type.Name, typeof(Archived).Name, StringComparison.Ordinal)
               || string.Equals(type.Name, typeof(Compacted<>).Name, StringComparison.Ordinal);
    }
}
