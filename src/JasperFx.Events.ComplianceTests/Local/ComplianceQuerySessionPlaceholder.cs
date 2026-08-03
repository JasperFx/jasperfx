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
// This placeholder stands in for that alias here and never leaves the repo. Members are declared
// only where a shared suite actually calls them, and their shapes are the intersection of what
// Marten and Polecat already expose -- binding against the real session types in the consumers is
// what validates them for real.

global using ComplianceQuerySession = JasperFx.Events.ComplianceTests.Local.IPlaceholderQuerySession;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace JasperFx.Events.ComplianceTests.Local;

public interface IPlaceholderQuerySession
{
    /// <summary>
    /// Called by the enrichment suite's database-lookup projection, which reads a document from
    /// inside <c>EnrichEventsAsync</c>.
    /// </summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class;
}
