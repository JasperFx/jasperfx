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
}
