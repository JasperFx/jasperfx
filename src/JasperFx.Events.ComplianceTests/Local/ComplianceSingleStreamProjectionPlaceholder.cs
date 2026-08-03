// NOT PACKAGED. See ComplianceQuerySessionPlaceholder.cs for why Local/ exists.
//
// StringIdentitySingleStreamCompliance declares a custom single stream projection at file scope, so
// it cannot reach the <TOperations, TQuerySession> pair its suite class is generic over -- the same
// gap ComplianceEventProjection closes for the EventProjection suites. Here the alias has to name a
// *closed* generic, because the projection base is generic over both the document and its identity:
//
//     global using ComplianceStringPartyProjectionBase =
//         Marten.Events.Aggregation.SingleStreamProjection<
//             JasperFx.Events.ComplianceTests.StringQuestParty, string>;
//
// An alias rather than a shared base class for the same reason as the others: each product's
// SingleStreamProjection<TDoc, TId> carries store-specific members, and the shared source wants the
// product's own type whatever it is.

global using ComplianceStringPartyProjectionBase =
    JasperFx.Events.ComplianceTests.Local.PlaceholderStringPartyProjection;

using JasperFx.Events.Aggregation;

namespace JasperFx.Events.ComplianceTests.Local;

public class PlaceholderStringPartyProjection: JasperFxSingleStreamProjectionBase<StringQuestParty, string,
    IPlaceholderOperations, IPlaceholderQuerySession>;
