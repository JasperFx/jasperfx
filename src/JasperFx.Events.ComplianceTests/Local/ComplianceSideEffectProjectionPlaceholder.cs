// NOT PACKAGED. See ComplianceQuerySessionPlaceholder.cs for why Local/ exists.
//
// ProjectionSideEffectCompliance declares its projection at file scope, so it cannot reach the
// <TOperations, TQuerySession> pair the suite class is generic over -- the same gap
// ComplianceStringPartyProjectionBase closes for the string identity suite. One more per-consumer
// global alias closes it, a closed generic because the single stream base is generic over both the
// document and its identity:
//
//     global using ComplianceWatchtowerProjectionBase =
//         Marten.Events.Aggregation.SingleStreamProjection<
//             JasperFx.Events.ComplianceTests.ComplianceWatchtower, System.Guid>;
//
// No constructor shim is needed: every product's SingleStreamProjection<TDoc, TId> is parameterless,
// and RaiseSideEffects -- the whole point of that suite -- is declared on the shared
// JasperFxAggregationProjectionBase rather than on any product's subclass. Its first parameter is
// the product's own writable session type, which the shared source names through the existing
// ComplianceOperations alias.

global using ComplianceWatchtowerProjectionBase =
    JasperFx.Events.ComplianceTests.Local.PlaceholderWatchtowerProjection;

using System;
using JasperFx.Events.Aggregation;

namespace JasperFx.Events.ComplianceTests.Local;

public abstract class PlaceholderWatchtowerProjection: JasperFxSingleStreamProjectionBase<ComplianceWatchtower,
    Guid, IPlaceholderOperations, IPlaceholderQuerySession>;
