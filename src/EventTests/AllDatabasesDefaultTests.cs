using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventTests;

public class AllDatabasesDefaultTests
{
    // A bare IEventStore that does NOT override AllDatabases(), so the call below exercises the
    // default interface implementation added for jasperfx#387 (the "return empty array" stand-in
    // for stores that don't yet expose their databases store-agnostically). Everything else throws —
    // those members are never invoked here.
    private sealed class BareEventStore : IEventStore
    {
        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token) => throw new NotImplementedException();
        public Uri Subject => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
            string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
            => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
            => throw new NotImplementedException();

        public Meter Meter => throw new NotImplementedException();
        public ActivitySource ActivitySource => throw new NotImplementedException();
        public string MetricsPrefix => throw new NotImplementedException();
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private readonly IEventStore theStore = new BareEventStore();

    [Fact]
    public async Task all_databases_default_returns_empty()
    {
        var databases = await theStore.AllDatabases();
        databases.ShouldBeEmpty();
    }
}
