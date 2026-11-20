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

public class projection_floor_compliance
    : ProjectionFloorCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class pending_event_age_compliance
    : PendingEventAgeCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class event_data_query_compliance
    : EventDataQueryCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

// jasperfx#1201: the store runs JasperFx's daemon for async projections so the per-identity rebuild has a reference
// store in this repository
public class identity_rebuild_compliance
    : IdentityRebuildCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

// jasperfx#1215 / #1222: the reference store for the event-range marks, the stored-event rewrite and the backfill
public class event_range_mark_compliance
    : EventRangeMarkCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class event_rewrite_compliance
    : EventRewriteCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

public class backfill_compliance
    : BackfillCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

// marten#6003: the reference store for parking the failing slice
public class parked_identity_compliance
    : ParkedIdentityCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;

// marten#6003 phase 2: the reference store for parking a multi-stream identity -- by stream, by DCB tag, and through the
// slice index -- and for rebuilding one by scan
public class multi_stream_parked_identity_compliance
    : MultiStreamParkedIdentityCompliance<InMemoryComplianceFixture, IInMemoryDocumentSession, IInMemoryQuerySession>;
