// NOT PACKAGED. Everything under Local/ exists so this project type-checks the compliance sources
// inside the JasperFx repo, where no concrete event store is available.
//
// The shared self-aggregating fixtures declare EvolveAsync(IEvent, ComplianceQuerySession) so that
// one source file can bind to Marten's IQuerySession in Marten and Polecat's in Polecat -- JasperFx's
// aggregate source generator resolves the parameter by type name, so a per-consumer global alias is
// enough. Each consuming test project supplies its own:
//
//     global using ComplianceQuerySession = Marten.IQuerySession;
//
// This placeholder stands in for that alias here and never leaves the repo.

global using ComplianceQuerySession = JasperFx.Events.ComplianceTests.Local.IPlaceholderQuerySession;

namespace JasperFx.Events.ComplianceTests.Local;

public interface IPlaceholderQuerySession;
