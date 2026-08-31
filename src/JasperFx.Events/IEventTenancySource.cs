using JasperFx.MultiTenancy;

namespace JasperFx.Events;

/// <summary>
/// Optional seam letting a store's query session tell the shared projection bases what tenancy
/// style its <em>event store</em> is configured for, so behavior that depends on it can live in
/// JasperFx.Events rather than being reimplemented in each store's projection subclass.
/// </summary>
/// <remarks>
/// <para>
/// Exists for <see cref="Aggregation.JasperFxSingleStreamProjectionBase{TDoc,TId,TOperations,TQuerySession}.BuildSlicer" />,
/// which needs to know whether to set <c>ForceSingleTenancy</c> on the slicer it builds. That is the
/// fix for wolverine#2053 / marten#4085: on a single-tenanted store, events whose <c>tenant_id</c>
/// values disagree must still fold into one aggregate rather than being sliced per tenant. Marten
/// implemented it by overriding <c>BuildSlicer</c> in its own <c>SingleStreamProjection&lt;TDoc,TId&gt;</c>;
/// Polecat's and Fisher's subclasses are empty class bodies, so they never got it. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/723" />.
/// </para>
/// <para>
/// <b>Deliberately not on <c>IEventRegistry</c>.</b> The registry is reachable from
/// <c>IEventStore.Registry</c>, but not from the <c>TQuerySession</c> that <c>BuildSlicer</c> is
/// handed, and widening that signature would break every store's override. The session is what the
/// method actually has. It is also not on <c>IDocumentReadOperations</c>, whose <c>Events</c> accessor
/// carries a throwing default — a projection cannot afford to probe it.
/// </para>
/// <para>
/// <b>The member is named <c>EventTenancyStyle</c> rather than <c>TenancyStyle</c> on purpose.</b>
/// All three stores already declare a <c>TenancyStyle</c> property somewhere in their options graph
/// (Marten on <c>EventGraph</c>, Polecat and Fisher on <c>EventStoreOptions</c>). A member named
/// <c>TenancyStyle</c> here would bind implicitly on some of them and not others, depending on which
/// type happens to carry it — silently correct in one store and silently absent in the next, which
/// is the exact failure mode this interface exists to end. An unmistakable name forces each store to
/// opt in visibly.
/// </para>
/// <para>
/// <b>Not implementing it is safe.</b> A session that does not implement this interface leaves the
/// projection bases on the behavior they had before it existed — no forcing — so adopting a JasperFx
/// version that introduces it changes nothing until a store opts in. Adoption is one line:
/// </para>
/// <code>
/// public TenancyStyle EventTenancyStyle => Options.Events.TenancyStyle;
/// </code>
/// </remarks>
public interface IEventTenancySource
{
    /// <summary>
    /// The tenancy style the <em>event store</em> is configured for — not the document store's, and
    /// not any single document's. <see cref="TenancyStyle.Single" /> means events are not partitioned
    /// by tenant, so any tenant id appearing on an event is incidental rather than meaningful.
    /// </summary>
    TenancyStyle EventTenancyStyle { get; }
}
