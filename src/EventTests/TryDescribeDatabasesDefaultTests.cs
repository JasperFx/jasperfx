using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventTests;

/// <summary>
/// JasperFx/wolverine#5002 — the database half of TryCreateUsage ships as a default interface member, so
/// out-of-tree stores keep compiling and answer correctly from the full usage until they override it.
/// </summary>
public class TryDescribeDatabasesDefaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Overrides only TryCreateUsage, so TryDescribeDatabasesAsync below runs the default.
    private sealed class BareEventStore(EventStoreUsage? usage) : IEventStore
    {
        public int UsageCalls { get; private set; }

        public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token)
        {
            UsageCalls++;
            return Task.FromResult(usage);
        }

        public Uri Subject => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
            string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
            => throw new NotImplementedException();

        public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id)
            => throw new NotImplementedException();

        public Meter Meter => throw new NotImplementedException();
        public ActivitySource ActivitySource => throw new NotImplementedException();
        public string MetricsPrefix => throw new NotImplementedException();
        public bool HasMultipleTenants => throw new NotImplementedException();
        public EventStoreIdentity Identity => throw new NotImplementedException();
        public DatabaseCardinality DatabaseCardinality => throw new NotImplementedException();
        public IReadOnlyEventStore OpenReadOnlyEventStore() => throw new NotImplementedException();

        public Task CompactStreamAsync(Guid streamId, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task CompactStreamAsync(string streamKey, CancellationToken token = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task the_default_answers_with_the_database_half_of_the_usage()
    {
        var databases = new DatabaseUsage
        {
            MainDatabase = new DatabaseDescriptor { ServerName = "localhost", DatabaseName = "main" },
            Databases =
            {
                new DatabaseDescriptor { ServerName = "localhost", DatabaseName = "tenant1", TenantIds = { "red" } }
            }
        };

        var bare = new BareEventStore(new EventStoreUsage { Database = databases });
        IEventStore store = bare;

        (await store.TryDescribeDatabasesAsync(Token)).ShouldBeSameAs(databases);
        bare.UsageCalls.ShouldBe(1);
    }

    [Fact]
    public async Task the_default_is_null_when_the_store_has_no_usage()
    {
        IEventStore store = new BareEventStore(null);

        (await store.TryDescribeDatabasesAsync(Token)).ShouldBeNull();
    }
}
