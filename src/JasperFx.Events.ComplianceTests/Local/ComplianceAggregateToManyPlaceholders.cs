// NOT PACKAGED. See ComplianceQuerySessionPlaceholder.cs for why Local/ exists.
//
// AggregateToManyCompliance declares its two multi-stream projections at file scope, so they cannot
// reach the <TOperations, TQuerySession> pair the suite class is generic over -- the same gap
// ComplianceMultiStreamProjectionBase closes for the multi-stream suite. Two more per-consumer
// global aliases close it here, closed generics because the multi stream base is generic over both
// the document and its identity:
//
//     global using ComplianceBalanceProjectionBase =
//         Marten.Events.Projections.MultiStreamProjection<
//             JasperFx.Events.ComplianceTests.ComplianceBalance, System.Guid>;
//     global using ComplianceMemberLoyaltyProjectionBase =
//         Marten.Events.Projections.MultiStreamProjection<
//             JasperFx.Events.ComplianceTests.ComplianceMemberLoyalty, System.Guid>;
//
// As with the department projection, no constructor shim is needed: both products'
// MultiStreamProjection<TDoc, TId> are parameterless, and everything the projections use --
// Identity, CustomGrouping, and the shared IJasperFxAggregateGrouper the custom grouper
// implements -- is declared on JasperFxMultiStreamProjectionBase rather than on either product's
// subclass.

global using ComplianceBalanceProjectionBase =
    JasperFx.Events.ComplianceTests.Local.PlaceholderBalanceProjection;
global using ComplianceMemberLoyaltyProjectionBase =
    JasperFx.Events.ComplianceTests.Local.PlaceholderMemberLoyaltyProjection;

using System;
using JasperFx.Events.Aggregation;

namespace JasperFx.Events.ComplianceTests.Local;

public abstract class PlaceholderBalanceProjection: JasperFxMultiStreamProjectionBase<ComplianceBalance,
    Guid, IPlaceholderOperations, IPlaceholderQuerySession>;

public abstract class PlaceholderMemberLoyaltyProjection: JasperFxMultiStreamProjectionBase<
    ComplianceMemberLoyalty, Guid, IPlaceholderOperations, IPlaceholderQuerySession>;
