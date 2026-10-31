using System.Diagnostics;
using JasperFx.OpenTelemetry;

namespace JasperFx.Events;

/// <summary>
/// The event half of what a unit of work committed, which is the part of a change set all three
/// stores declare alike.
/// </summary>
/// <remarks>
/// <para>
/// jasperfx#1054. Marten's, Polecat's and Fisher's <c>IChangeSet</c> each carry these two members with
/// these two signatures, so deriving from this adds nothing for an implementer to write. It stops
/// there on purpose: their document members differ in element type, and
/// <see cref="Documents.IDocumentChangeSet" /> is the shared contract for that half.
/// </para>
/// <para>
/// A separate interface rather than members on <see cref="Documents.IDocumentChangeSet" />, because
/// that one has shipped: a member added to it would need a default, and no default can answer this.
/// </para>
/// </remarks>
public interface IUnitOfWorkChangeSet
{
    /// <summary>Every event appended across every stream in this unit of work.</summary>
    IEnumerable<IEvent> GetEvents();

    /// <summary>Every stream started or appended to in this unit of work.</summary>
    IEnumerable<StreamAction> GetStreams();
}

/// <summary>
/// Change-set counters that need the event half of a change set, which is why these live here and not
/// on <see cref="OpenTelemetryOptions{TChangeSet}" />: JasperFx cannot name <see cref="IEvent" />.
/// </summary>
public static class UnitOfWorkChangeSetMetrics
{
    /// <summary>
    /// Export a counter incremented once per event a committed unit of work appended: the shape of
    /// each store's <c>TrackEventCounters</c>.
    /// </summary>
    /// <param name="options">The store's options.</param>
    /// <param name="name">The counter's name.</param>
    /// <param name="tags">
    /// The tags for one event. Supplied by the store, because the tag names are its published series:
    /// <c>event.type</c> on Marten, <c>fisher.event.type</c> on Fisher.
    /// </param>
    /// <param name="units">The counter's units.</param>
    public static void ExportEventCounterOnChangeSets<TChangeSet>(this OpenTelemetryOptions<TChangeSet> options,
        string name, Func<IEvent, TagList> tags, string units = "events")
        where TChangeSet : IUnitOfWorkChangeSet
    {
        ArgumentNullException.ThrowIfNull(tags);

        options.ExportCounterOnChangeSets<long>(name, units, (counter, commit) =>
        {
            foreach (var e in commit.GetEvents())
            {
                counter.Add(1, tags(e));
            }
        });
    }
}
