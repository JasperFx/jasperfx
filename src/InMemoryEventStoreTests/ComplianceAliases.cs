// jasperfx#964. The shared compliance suites declare types at file scope -- self-aggregating aggregates
// whose EvolveAsync convention method takes the store's read session, and projection classes that cannot
// reach the <TOperations, TQuerySession> pair their suite class is generic over. The aggregate source
// generator resolves those parameters by type name, so these global aliases bind the shared sources to
// the in-memory prototyping store's own types, exactly as Marten's, Polecat's and Fisher's test projects
// bind them to theirs.

global using ComplianceQuerySession = JasperFx.Events.InMemory.IInMemoryQuerySession;
global using ComplianceOperations = JasperFx.Events.InMemory.IInMemoryDocumentSession;
global using ComplianceEventProjection = JasperFx.Events.InMemory.Projections.EventProjection;

// Closed generics: the single- and multi-stream bases are generic over the identity as well as the document
global using ComplianceStringPartyProjectionBase =
    JasperFx.Events.InMemory.Projections.SingleStreamProjection<JasperFx.Events.ComplianceTests.StringQuestParty, string>;
global using ComplianceMultiStreamProjectionBase =
    JasperFx.Events.InMemory.Projections.MultiStreamProjection<JasperFx.Events.ComplianceTests.ComplianceDepartment, string>;
global using ComplianceWatchtowerProjectionBase =
    JasperFx.Events.InMemory.Projections.SingleStreamProjection<JasperFx.Events.ComplianceTests.ComplianceWatchtower, System.Guid>;
global using ComplianceBalanceProjectionBase =
    JasperFx.Events.InMemory.Projections.MultiStreamProjection<JasperFx.Events.ComplianceTests.ComplianceBalance, System.Guid>;
global using ComplianceMemberLoyaltyProjectionBase =
    JasperFx.Events.InMemory.Projections.MultiStreamProjection<JasperFx.Events.ComplianceTests.ComplianceMemberLoyalty, System.Guid>;
