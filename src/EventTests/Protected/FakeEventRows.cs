using System.Text.Json;
using JasperFx.Events;
using NSubstitute;

namespace EventTests.Protected;

/// <summary>
/// An event table with the properties masking and compaction depend on, for tests of the shared execution
/// (#1048). A row is keyed by tenant and sequence, so sequences can collide across tenants as they do under
/// per-tenant sequencing, and a plain stream read leaves archived rows out as Marten's and Polecat's do.
/// </summary>
public class FakeEventRows
{
    public const string DefaultTenant = "*DEFAULT*";

    public List<EventRow> Rows { get; set; } = [];

    /// <summary>
    /// Hand back one instance per row for the life of a session, where the default is a fresh copy per read.
    /// </summary>
    public bool ReadsShareInstances { get; set; }

    public EventRow Add(long sequence, long version, Guid streamId, object body, string tenant = DefaultTenant,
        bool archived = false, string? streamKey = null)
    {
        var row = new EventRow
        {
            Tenant = tenant,
            Sequence = sequence,
            Version = version,
            StreamId = streamId,
            StreamKey = streamKey,
            IsArchived = archived,
            Body = body
        };

        Rows.Add(row);
        return row;
    }

    public EventRow Row(long sequence, string tenant = DefaultTenant)
        => Rows.Single(x => x.Sequence == sequence && x.Tenant == tenant);

    public IEnumerable<EventRow> RowsFor(string tenant) => Rows.Where(x => x.Tenant == tenant);

    public FakeSession Session(string tenant = DefaultTenant) => new(this, tenant);
}

public class EventRow
{
    public required string Tenant { get; init; }
    public required long Sequence { get; init; }
    public required long Version { get; init; }
    public required Guid StreamId { get; init; }
    public string? StreamKey { get; init; }
    public bool IsArchived { get; init; }
    public required object Body { get; set; }
    public Dictionary<string, object>? Headers { get; set; }

    public EventRow Clone() => (EventRow)MemberwiseClone();

    public IEvent Hydrate()
    {
        // A round trip, as a read off a real table is: nothing a rule does to one read reaches the next
        var type = Body.GetType();
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(Body, type), type)!;

        var @event = (IEvent)Activator.CreateInstance(typeof(Event<>).MakeGenericType(type), copy)!;
        @event.TenantId = Tenant;
        @event.Sequence = Sequence;
        @event.Version = Version;
        @event.StreamId = StreamId;
        @event.StreamKey = StreamKey;
        @event.IsArchived = IsArchived;
        @event.Headers = Headers is null ? null : new Dictionary<string, object>(Headers);

        return @event;
    }
}

public interface IFakeOperations;

/// <summary>
/// A unit of work over <see cref="FakeEventRows" />: writes queue, and <see cref="SaveChangesAsync" /> applies
/// them to a copy that replaces the rows only if every one succeeds, so a failure rolls the whole queue back.
/// </summary>
public class FakeSession : IFakeOperations, IAsyncDisposable
{
    private readonly FakeEventRows _rows;
    private readonly List<(string Name, Action<List<EventRow>> Apply)> _queue = [];
    private readonly List<string> _everQueued = [];
    private readonly Dictionary<long, IEvent> _sharedInstances = new();

    public FakeSession(FakeEventRows rows, string tenantId)
    {
        _rows = rows;
        TenantId = tenantId;

        Events = Substitute.For<IEventStoreOperations>();

        Events.FetchStreamAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<DateTimeOffset?>(), Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call => read(x => x.StreamId == call.ArgAt<Guid>(0) && !x.IsArchived, call.ArgAt<long>(1)));

        Events.FetchStreamAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<DateTimeOffset?>(), Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call => read(x => x.StreamKey == call.ArgAt<string>(0) && !x.IsArchived, call.ArgAt<long>(1)));

        Events.When(x => x.OverwriteEvent(Arg.Any<IEvent>())).Do(call =>
        {
            var @event = call.ArgAt<IEvent>(0);
            var body = @event.Data;
            var headers = @event.Headers;

            Queue("overwrite", rows =>
            {
                // No matching row is no error, as with an update that matched nothing
                var row = rows.SingleOrDefault(x => x.Sequence == @event.Sequence && x.Tenant == TenantId);
                if (row is null)
                {
                    return;
                }

                row.Body = body;
                row.Headers = headers;
            });
        });

        Events.When(x => x.CompletelyReplaceEvent<Arg.AnyType>(Arg.Any<long>(), Arg.Any<Arg.AnyType>())).Do(call =>
        {
            ReplaceFailure?.Invoke();

            var sequence = call.ArgAt<long>(0);
            var body = call.Args()[1]!;

            Queue("replace", rows =>
            {
                var row = rows.SingleOrDefault(x => x.Sequence == sequence && x.Tenant == TenantId);
                if (row is null)
                {
                    return;
                }

                row.Body = body;
                row.Headers = null;
            });
        });
    }

    public string TenantId { get; }

    public IEventStoreOperations Events { get; }

    public bool IsDisposed { get; private set; }

    public int Saves { get; private set; }

    /// <summary>
    /// Everything queued on this session, committed or not.
    /// </summary>
    public IReadOnlyList<string> Queued => _everQueued.ToList();

    /// <summary>
    /// Fails the operation at the moment it is queued, as a store refusing it outright would.
    /// </summary>
    public Action? ReplaceFailure { get; set; }

    /// <summary>
    /// Fails this queued operation at commit, as the database refusing it would.
    /// </summary>
    public string? FailAtCommit { get; set; }

    public void Queue(string name, Action<List<EventRow>> apply)
    {
        _queue.Add((name, apply));
        _everQueued.Add(name);
    }

    public IReadOnlyList<IEvent> Read(Func<EventRow, bool> filter) => read(filter, 0).Result;

    public Task SaveChangesAsync(CancellationToken token) => CommitAsync();

    public Task CommitAsync()
    {
        var rows = _rows.Rows.Select(x => x.Clone()).ToList();

        foreach (var (name, apply) in _queue)
        {
            if (name == FailAtCommit)
            {
                throw new InvalidOperationException($"The database refused the {name}");
            }

            apply(rows);
        }

        _rows.Rows = rows;
        _queue.Clear();
        Saves++;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Apply only the first <paramref name="count" /> queued operations, with no transaction around them: what
    /// a failure part way through would leave if the store's batch were not atomic.
    /// </summary>
    public void ApplyOnly(int count)
    {
        foreach (var (_, apply) in _queue.Take(count))
        {
            apply(_rows.Rows);
        }

        _queue.Clear();
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    private Task<IReadOnlyList<IEvent>> read(Func<EventRow, bool> filter, long version)
    {
        IReadOnlyList<IEvent> events = _rows.RowsFor(TenantId)
            .Where(filter)
            .Where(x => version == 0 || x.Version <= version)
            .OrderBy(x => x.Sequence)
            .Select(hydrate)
            .ToList();

        return Task.FromResult(events);
    }

    private IEvent hydrate(EventRow row)
    {
        if (!_rows.ReadsShareInstances)
        {
            return row.Hydrate();
        }

        if (!_sharedInstances.TryGetValue(row.Sequence, out var @event))
        {
            @event = row.Hydrate();
            _sharedInstances[row.Sequence] = @event;
        }

        return @event;
    }
}
