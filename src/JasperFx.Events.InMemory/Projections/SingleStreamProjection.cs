using JasperFx.Events.Aggregation;

namespace JasperFx.Events.InMemory.Projections;

/// <summary>
/// A single-stream projection on the in-memory prototyping store (jasperfx#964): one document per
/// stream, folded from that stream's events.
/// </summary>
/// <remarks>
/// Named like Marten's and Fisher's so a projection class moves between stores with a <c>using</c>
/// change. The base is store-specific by design (decided on jasperfx#962): a projection class is closed
/// over its store's session types.
/// </remarks>
public class SingleStreamProjection<TDoc, TId>
    : JasperFxSingleStreamProjectionBase<TDoc, TId, IInMemoryDocumentSession, IInMemoryQuerySession>,
        IInMemoryBackfillSource
    where TDoc : notnull
    where TId : notnull
{
    private readonly List<InMemoryBackfill<TDoc>> _backfills = new();

    /// <summary>
    /// jasperfx#1222: declare a backfill that fills a new member on the documents this projection already wrote,
    /// from only the events that produce it. It runs as a Wolverine job through
    /// <see cref="IEventStore.RunBackfillAsync" />, so the store refuses the projection unless
    /// <see cref="EventRegistry.HasJobHost" /> is set. Start the projection's new version with
    /// <see cref="JasperFx.Events.Projections.AsyncOptions.SubscribeFromPriorVersionMark" /> and the same name.
    /// </summary>
    public InMemoryBackfill<TDoc> Backfill(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_backfills.Any(x => x.Name == name))
        {
            throw new InvalidOperationException($"Projection {Name} already declares a backfill named '{name}'.");
        }

        var backfill = new InMemoryBackfill<TDoc>(name);
        _backfills.Add(backfill);
        return backfill;
    }

    IReadOnlyList<IInMemoryBackfill> IInMemoryBackfillSource.Backfills => _backfills;

    Type IInMemoryBackfillSource.DocumentType => typeof(TDoc);
}

/// <summary>
/// jasperfx#1222: a projection that declares backfills on the in-memory prototyping store.
/// </summary>
internal interface IInMemoryBackfillSource
{
    string Name { get; }
    IReadOnlyList<IInMemoryBackfill> Backfills { get; }
    Type DocumentType { get; }
}

internal interface IInMemoryBackfill
{
    string Name { get; }
    bool Handles(object eventData);
    void Apply(object document, object eventData);
}

/// <summary>
/// jasperfx#1222: a backfill declared with <see cref="SingleStreamProjection{TDoc,TId}.Backfill" />. Each handler
/// changes a document that already exists. Documents that are missing or deleted are skipped.
/// </summary>
public sealed class InMemoryBackfill<TDoc> : IInMemoryBackfill where TDoc : notnull
{
    private readonly List<(Type EventType, Action<TDoc, object> Apply)> _handlers = new();

    internal InMemoryBackfill(string name)
    {
        Name = name;
    }

    /// <summary>The backfill's name.</summary>
    public string Name { get; }

    /// <summary>
    /// Fill the member from each <typeparamref name="TEvent" /> up to the backfill's ceiling.
    /// </summary>
    public InMemoryBackfill<TDoc> On<TEvent>(Action<TDoc, TEvent> apply) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(apply);
        _handlers.Add((typeof(TEvent), (doc, e) => apply(doc, (TEvent)e)));
        return this;
    }

    bool IInMemoryBackfill.Handles(object eventData) => _handlers.Any(x => x.EventType.IsInstanceOfType(eventData));

    void IInMemoryBackfill.Apply(object document, object eventData)
    {
        foreach (var handler in _handlers.Where(x => x.EventType.IsInstanceOfType(eventData)))
        {
            handler.Apply((TDoc)document, eventData);
        }
    }
}
