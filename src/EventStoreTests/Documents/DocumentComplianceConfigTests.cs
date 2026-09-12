using JasperFx;
using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// What a document compliance suite declares about the store it needs (jasperfx#672).
/// </summary>
/// <remarks>
/// A suite whose precondition the config cannot carry is a suite that never states it, and each
/// fixture then has to guess — at which point a store implementing the contract correctly still
/// fails. These are cheap guards against that regressing, and they run here rather than downstream
/// because nothing about them needs a real store.
/// </remarks>
public class DocumentComplianceConfigTests
{
    [Fact]
    public void stream_identity_is_unset_until_a_suite_asks_for_one()
    {
        // Null means "leave the store on its own default", which is what every document-only suite
        // wants. A non-null default here would push a rebuild onto fixtures that need nothing.
        new DocumentComplianceConfig().StreamIdentity.ShouldBeNull();
    }

    /// <remarks>
    /// The fix itself. This suite appends by stream key throughout, so it fails on any store
    /// defaulting to Guid identity unless it says so.
    /// </remarks>
    [Fact]
    public void the_document_session_events_suite_declares_string_stream_identity()
    {
        var config = new DocumentComplianceConfig();
        ExposedDocumentSessionEventsCompliance.TheConfiguration(config);

        config.StreamIdentity.ShouldBe(StreamIdentity.AsString);
    }

    /// <remarks>
    /// The other suite that appends by stream key, and the one most likely to be added without the
    /// declaration — it was written before jasperfx#672 gave it anywhere to say this.
    /// </remarks>
    [Fact]
    public void the_pending_stream_actions_suite_declares_string_stream_identity()
    {
        var config = new DocumentComplianceConfig();
        ExposedPendingStreamActionsCompliance.TheConfiguration(config);

        config.StreamIdentity.ShouldBe(StreamIdentity.AsString);
    }

    [Fact]
    public void a_document_only_suite_leaves_stream_identity_alone()
    {
        var config = new DocumentComplianceConfig();
        ExposedDocumentSessionCompliance.TheConfiguration(config);

        config.StreamIdentity.ShouldBeNull();
    }

    /// <remarks>
    /// jasperfx#819 §1. The marker alone is not a complete declaration — the stores disagree about
    /// whether <c>IVersioned</c> is itself the opt-in — so a suite that declared only the marker
    /// would be testing that disagreement rather than the concurrency behavior.
    /// </remarks>
    [Fact]
    public void the_guid_concurrency_suite_declares_optimistic_concurrency()
    {
        var config = new DocumentComplianceConfig();
        ExposedGuidOptimisticConcurrencyCompliance.TheConfiguration(config);

        config.OptimisticConcurrencyTypes.ShouldContain(typeof(ComplianceShipment));
    }

    /// <remarks>
    /// jasperfx#819 §2, and the guard that keeps the two revision suites from testing the same route
    /// twice. The declared suite's document deliberately does not implement <see cref="IRevisioned" />,
    /// so if the config declaration went missing it would not merely test the wrong route — it would
    /// test no route at all, and fail for a reason unrelated to the store's revision handling.
    /// </remarks>
    [Fact]
    public void the_declared_revision_suite_declares_numeric_revisions_through_the_config()
    {
        var config = new DocumentComplianceConfig();
        ExposedDeclaredNumericRevisionCompliance.TheConfiguration(config);

        config.NumericRevisionTypes.ShouldContain(typeof(ComplianceMeterReading));

        typeof(IRevisioned).IsAssignableFrom(typeof(ComplianceMeterReading)).ShouldBeFalse();
    }

    /// <remarks>
    /// The other half of the pair: the original suite declares its document through the marker only,
    /// which is what makes the two suites cover two routes rather than one.
    /// </remarks>
    [Fact]
    public void the_marker_revision_suite_declares_nothing_through_the_config()
    {
        var config = new DocumentComplianceConfig();
        ExposedNumericRevisionCompliance.TheConfiguration(config);

        config.NumericRevisionTypes.ShouldBeEmpty();

        typeof(IRevisioned).IsAssignableFrom(typeof(ComplianceLedgerEntry)).ShouldBeTrue();
    }

    /// <remarks>
    /// Not public, so xunit never collects the inherited facts — the in-memory reference store is
    /// document-only and could not run this suite. All that is wanted is the configuration delegate.
    /// </remarks>
    private class ExposedDocumentSessionEventsCompliance
        : DocumentSessionEventsCompliance<InMemoryDocumentComplianceFixture>
    {
        public static readonly Action<DocumentComplianceConfig> TheConfiguration =
            new ExposedDocumentSessionEventsCompliance().Configuration;
    }

    private class ExposedPendingStreamActionsCompliance
        : PendingStreamActionsCompliance<InMemoryDocumentComplianceFixture>
    {
        public static readonly Action<DocumentComplianceConfig> TheConfiguration =
            new ExposedPendingStreamActionsCompliance().Configuration;
    }

    private class ExposedDocumentSessionCompliance
        : DocumentSessionCompliance<InMemoryDocumentComplianceFixture>
    {
        public static readonly Action<DocumentComplianceConfig> TheConfiguration =
            new ExposedDocumentSessionCompliance().Configuration;
    }

    private class ExposedGuidOptimisticConcurrencyCompliance
        : GuidOptimisticConcurrencyCompliance<InMemoryDocumentComplianceFixture>
    {
        public static readonly Action<DocumentComplianceConfig> TheConfiguration =
            new ExposedGuidOptimisticConcurrencyCompliance().Configuration;
    }

    private class ExposedNumericRevisionCompliance
        : NumericRevisionCompliance<InMemoryDocumentComplianceFixture>
    {
        public static readonly Action<DocumentComplianceConfig> TheConfiguration =
            new ExposedNumericRevisionCompliance().Configuration;
    }

    private class ExposedDeclaredNumericRevisionCompliance
        : DeclaredNumericRevisionCompliance<InMemoryDocumentComplianceFixture>
    {
        public static readonly Action<DocumentComplianceConfig> TheConfiguration =
            new ExposedDeclaredNumericRevisionCompliance().Configuration;
    }
}
