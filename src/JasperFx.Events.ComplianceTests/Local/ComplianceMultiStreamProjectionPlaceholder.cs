// NOT PACKAGED. See ComplianceQuerySessionPlaceholder.cs for why Local/ exists.
//
// MultiStreamProjectionCompliance declares its projection at file scope, so it cannot reach the
// <TOperations, TQuerySession> pair its suite class is generic over -- the same gap
// ComplianceStringPartyProjectionBase closes for the string identity suite. The alias names a
// *closed* generic here too, because the multi stream base is generic over both the document and
// its identity:
//
//     global using ComplianceMultiStreamProjectionBase =
//         Marten.Events.Projections.MultiStreamProjection<
//             JasperFx.Events.ComplianceTests.ComplianceDepartment, string>;
//
// Unlike the flat-table suite this needs no constructor shim in the consumer: both products'
// MultiStreamProjection<TDoc, TId> are parameterless subclasses of JasperFxMultiStreamProjectionBase,
// and every grouping construct the suite uses -- Identity, Identities, FanOut -- is declared on that
// shared base rather than on either product's subclass.

global using ComplianceMultiStreamProjectionBase =
    JasperFx.Events.ComplianceTests.Local.PlaceholderDepartmentProjection;

using JasperFx.Events.Aggregation;

namespace JasperFx.Events.ComplianceTests.Local;

public abstract class PlaceholderDepartmentProjection: JasperFxMultiStreamProjectionBase<ComplianceDepartment,
    string, IPlaceholderOperations, IPlaceholderQuerySession>;
