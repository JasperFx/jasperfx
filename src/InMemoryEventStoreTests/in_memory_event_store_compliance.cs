using JasperFx.Events.ComplianceTests;
using JasperFx.Events.InMemory;

namespace InMemoryEventStoreTests;

/*
 * jasperfx#964: the in-memory prototyping store's enrolment in the cross-store event sourcing compliance
 * suites. Each class is empty on purpose -- the behaviour lives once in JasperFx.Events.ComplianceTests
 * and is closed here over the store's session pair through InMemoryComplianceFixture, as Marten, Polecat
 * and Fisher enrol.
 *
 * Only the suites for what the store is meant to do are enrolled: events, live and inline aggregation,
 * and inline projections. Not enrolled, and out of scope for a prototyping store: the async daemon,
 * subscriptions, the explorer, tags (DCB), archiving and compaction, masking, upcasting, flat tables.
 * StringStreamIdentityCompliance is held back for an archiving fact, and StreamStateQueryCompliance
 * because it reads through Fixture.EventStore.
 */

public class fetch_for_writing_compliance
    : FetchForWritingCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class fetch_latest_compliance
    : FetchLatestCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class live_aggregation_compliance
    : LiveAggregationCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class stream_read_compliance
    : StreamReadCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class event_metadata_compliance
    : EventMetadataCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class activity_correlation_compliance
    : ActivityCorrelationCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class always_enforce_consistency_compliance
    : AlwaysEnforceConsistencyCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class self_aggregating_evolve_compliance
    : SelfAggregatingEvolveCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class auto_discovered_aggregate_compliance
    : AutoDiscoveredAggregateCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class snapshot_lifecycle_compliance
    : SnapshotLifecycleCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class string_identity_single_stream_compliance
    : StringIdentitySingleStreamCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class event_projection_registration_compliance
    : EventProjectionRegistrationCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class event_projection_enrichment_compliance
    : EventProjectionEnrichmentCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class multi_stream_projection_compliance
    : MultiStreamProjectionCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class strong_typed_identity_compliance
    : StrongTypedIdentityCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;
