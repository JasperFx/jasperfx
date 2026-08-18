using System;
using System.Collections.Generic;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Store-neutral description of the document store configuration a document compliance suite needs.
/// A suite fills one of these in; the fixture replays it against its own store.
/// </summary>
/// <remarks>
/// <para>
/// Far thinner than <see cref="ComplianceStoreConfig" />, and that is the point rather than an
/// oversight. The document contract (jasperfx#647) is eight operations, so a suite needs nothing but
/// a schema to live in, the document types it will exercise, and the strong-typed identifiers those
/// documents are keyed by. Still no registrar interface: unlike the event side, every one of those is
/// expressible as a <see cref="Type" /> the fixture replays against its own options.
/// </para>
/// <para>
/// Being thin is not the same as being silent, though, and jasperfx#672 is what drew the line. Every
/// precondition a suite has on the store must be expressible here, because a precondition the config
/// cannot carry becomes one the suite never states and each fixture has to guess — at which point a
/// store that implements the contract correctly still fails. <see cref="StreamIdentity" /> was the
/// first of those to surface, and it surfaced as soon as the document config had to describe
/// anything about the event store.
/// </para>
/// </remarks>
public sealed class DocumentComplianceConfig
{
    /// <summary>
    /// Optional schema/namespace override. When null the fixture picks its own.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// The stream identity style the suite needs. Null leaves the store on its own default, which is
    /// <see cref="Events.StreamIdentity.AsGuid" /> in every current store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain property rather than anything cleverer, matching
    /// <see cref="ComplianceStoreConfig.StreamIdentity" />: the value is the shared
    /// <see cref="Events.StreamIdentity" /> enum, but the options object it hangs off is the store's
    /// own event graph, and the fixture is already the place that knows which.
    /// </para>
    /// <para>
    /// It is here because a suite that appends by stream <em>key</em> has an undeclared precondition
    /// otherwise, and a correctly-implemented store then fails it. That is a bug in the suite by
    /// definition — the whole point of a shared compliance library is that implementing the contract
    /// is sufficient to pass. See <see href="https://github.com/JasperFx/jasperfx/issues/672" />:
    /// <see cref="DocumentSessionEventsCompliance{TFixture}" /> appends by string key throughout, so
    /// three of its five facts failed on every store defaulting to Guid identity, with an error
    /// naming stream identity but not the suite's requirement.
    /// </para>
    /// <para>
    /// A fixture must replay this, exactly as it replays <see cref="ValueTypes" />. Ignoring it does
    /// not make the affected suites skip — they fail.
    /// </para>
    /// </remarks>
    public StreamIdentity? StreamIdentity { get; set; }

    /// <summary>
    /// The document types the suite will store, query and delete. Stores that create document
    /// storage on demand may ignore this; stores that need to be told up front use it.
    /// </summary>
    public List<Type> DocumentTypes { get; } = new();

    public DocumentComplianceConfig AddDocumentType<T>() where T : notnull
    {
        DocumentTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Strong-typed identifier wrappers used as document identities in this configuration.
    /// </summary>
    /// <remarks>
    /// Needed by <see cref="Documents.IDocumentReadOperations.LoadAsync{T}(object,System.Threading.CancellationToken)" />,
    /// which is the only member of the document contract whose behavior depends on store
    /// configuration the contract itself does not carry. Every Critter Stack store spells the
    /// registration <c>StoreOptions.RegisterValueType(Type)</c>, so a fixture replays this with a
    /// loop; stores that discover value types automatically can ignore it.
    /// </remarks>
    public List<Type> ValueTypes { get; } = new();

    public DocumentComplianceConfig RegisterValueType<TValue>() where TValue : notnull
    {
        ValueTypes.Add(typeof(TValue));
        return this;
    }

    /// <summary>
    /// Event types the suite will append through a session's <c>Events</c> accessor.
    /// </summary>
    /// <remarks>
    /// Only <see cref="DocumentSessionEventsCompliance{TFixture}" /> populates this, and only stores
    /// that are event stores enroll in that suite — so a fixture for a document-only store can
    /// ignore it entirely. It is here rather than on <see cref="ComplianceStoreConfig" /> because
    /// jasperfx#669 is precisely the seam where the two halves meet: the accessor is reached from a
    /// <em>document</em> session, so it has to be exercised by a document fixture.
    /// </remarks>
    public List<Type> EventTypes { get; } = new();

    public DocumentComplianceConfig AddEventType<T>() where T : notnull
    {
        EventTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Post-commit listeners the store must invoke — <see cref="Documents.IDocumentCommitListener" />
    /// (jasperfx#679).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one config member that is not a <see cref="Type" />, and it has to be: a listener is
    /// registered as an <em>instance</em> on every product (<c>StoreOptions.Listeners</c> on all
    /// three), and the suite has to hold the same instance it registered in order to read back what
    /// the store handed it. Nothing else in the document compliance surface needs that, which is why
    /// this is the first departure from the "everything is a Type the fixture replays" rule.
    /// </para>
    /// <para>
    /// It also has to exist at all, rather than the suite registering a listener through a session
    /// it was handed. Registration happens when the store is <em>built</em> — before any session
    /// exists — so without a slot here the suite for jasperfx#679 is not merely awkward to write, it
    /// is unwritable. That is the jasperfx#672 rule restated: a precondition the config cannot carry
    /// is one each fixture has to guess at.
    /// </para>
    /// <para>
    /// A fixture replays this by adapting each entry onto its own listener type and adding it to
    /// <c>StoreOptions.Listeners</c>. Ignoring it does not make
    /// <c>DocumentCommitListenerCompliance</c> skip — every fact in it fails, because a listener
    /// that was never registered never fires, which is indistinguishable from a store that does not
    /// implement the contract.
    /// </para>
    /// </remarks>
    public List<Documents.IDocumentCommitListener> CommitListeners { get; } = new();

    public DocumentComplianceConfig AddCommitListener(Documents.IDocumentCommitListener listener)
    {
        CommitListeners.Add(listener);
        return this;
    }
}
