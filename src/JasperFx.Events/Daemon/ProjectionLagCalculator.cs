using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     The provider-neutral correlation behind <see cref="IEventDatabase.FetchProjectionLagAsync" /> —
///     registered shards (the current versions) × the progression rows of one database × the
///     high-water rows, with no database access of its own. Exposed publicly so a caller that already
///     holds both halves (a poller that fetched <c>AllProjectionProgress</c> for its own reasons, a
///     test, a store with a cheaper read) can run the same correlation without a second round trip.
///     See jasperfx#619.
/// </summary>
public static class ProjectionLagCalculator
{
    /// <summary>
    ///     Correlate registered shards against one database's progression rows.
    ///     <para>
    ///     The rules, all of which exist because a real deployment broke on their absence:
    ///     <list type="bullet">
    ///     <item>Anchor on <paramref name="registeredShards" />, not on the rows. A row is only ever
    ///     consulted for a shard that is registered right now at the version it is registered at, so
    ///     a prior version's row can never be mistaken for current progress and non-shard bookkeeping
    ///     rows (marten#5161) can never masquerade as a projection that never advances.</item>
    ///     <item>A registered cell with no row is reported with <c>HasProgressionRow == false</c> and
    ///     <c>Sequence == 0</c> — fully behind, not caught up.</item>
    ///     <item>Tenants are discovered from the <c>HighWaterMark:{tenant}</c> rows. When any exist,
    ///     each registered shard reports one cell per tenant against THAT tenant's mark (marten#4761:
    ///     under per-tenant event partitioning every tenant draws its own sequence, so a store-global
    ///     bar is meaningless).</item>
    ///     <item>A registered shard that is still running store-global under a tenanted store — a
    ///     single <c>:All</c> agent, which records no per-tenant rows at all — keeps its own cell,
    ///     measured against the store-global mark, falling back to the highest tenant mark when the
    ///     store-global row is absent. Without this that agent is invisible.</item>
    ///     </list>
    ///     </para>
    /// </summary>
    /// <param name="registeredShards">
    ///     The shard names registered in the running application, at their current versions — i.e.
    ///     <c>IEventStore&lt;,&gt;.AllShards()</c> / <c>ProjectionGraph.AllShards()</c> projected onto
    ///     <c>Name</c>. Tenant-qualified names are accepted and reduced to their store-global form
    ///     before the tenant fan-out, so passing an already-fanned-out list does not multiply cells.
    /// </param>
    /// <param name="progress">Every progression row of one database, as returned by <c>AllProjectionProgress</c>.</param>
    /// <param name="databaseIdentifier">Stamped onto every result so a multi-database fan-out stays attributable.</param>
    public static IReadOnlyList<ProjectionLag> Calculate(
        IEnumerable<ShardName> registeredShards,
        IReadOnlyList<ShardState> progress,
        string? databaseIdentifier = null)
    {
        var tenantMarks = new Dictionary<string, long>();
        var sequences = new Dictionary<string, long>();
        long globalMark = 0;

        foreach (var state in progress)
        {
            // Anything that isn't a shard identity we understand is bookkeeping, and bookkeeping rows
            // never advance. Dropping them here is what keeps them from being reported as a projection
            // that is permanently behind (marten#5161).
            if (!ShardName.TryParse(state.ShardName, out var parsed) || parsed == null) continue;

            if (parsed.IsHighWaterMark)
            {
                if (parsed.TenantId == null)
                {
                    globalMark = Math.Max(globalMark, state.Sequence);
                }
                else
                {
                    tenantMarks[parsed.TenantId] =
                        Math.Max(tenantMarks.TryGetValue(parsed.TenantId, out var current) ? current : 0,
                            state.Sequence);
                }

                continue;
            }

            sequences[parsed.Identity] =
                Math.Max(sequences.TryGetValue(parsed.Identity, out var existing) ? existing : 0, state.Sequence);
        }

        // Deterministic ordering so a status endpoint's output doesn't churn between polls
        var tenants = tenantMarks.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var globalCeiling = globalMark > 0 ? globalMark : tenantMarks.Values.DefaultIfEmpty(0).Max();

        var results = new List<ProjectionLag>();
        var seen = new HashSet<string>();

        foreach (var shard in registeredShards)
        {
            // A caller may hand us either form; reduce to the store-global identity so the tenant
            // fan-out below is the ONLY thing that produces tenant cells
            var name = shard.TenantId == null ? shard : shard.ForTenant(null);
            if (!seen.Add(name.Identity)) continue;

            if (tenants.Length == 0)
            {
                results.Add(lagFor(name, globalMark));
                continue;
            }

            var hasTenantRows = false;
            foreach (var tenantId in tenants)
            {
                var cell = name.ForTenant(tenantId);
                hasTenantRows |= sequences.ContainsKey(cell.Identity);
                results.Add(lagFor(cell, tenantMarks[tenantId]));
            }

            // A store-global agent under a tenanted store: it owns no per-tenant rows, so without its
            // own cell it would vanish from the report entirely. Only emitted when its row exists —
            // a projection that has simply never run is already fully described by the tenant cells.
            if (!hasTenantRows && sequences.ContainsKey(name.Identity))
            {
                results.Add(lagFor(name, globalCeiling));
            }
        }

        return results;

        ProjectionLag lagFor(ShardName name, long mark)
        {
            var has = sequences.TryGetValue(name.Identity, out var sequence);
            return new ProjectionLag(name, databaseIdentifier, has ? sequence : 0, mark, has);
        }
    }

    /// <summary>
    ///     Narrow a calculated set to the cells addressed by <paramref name="name" />.
    ///     <para>
    ///     Matching is on the projection <see cref="ShardName.Name" /> always; on
    ///     <see cref="ShardName.ShardKey" /> only when the caller supplied something other than
    ///     <see cref="ShardName.All" /> (so the store-global name of a sliced projection means "every
    ///     slice"); and on <see cref="ShardName.TenantId" /> only when the caller supplied one (so a
    ///     tenant-less name means "every tenant"). <see cref="ShardName.Version" /> is deliberately
    ///     NOT matched: the read is anchored on the registry, which only ever holds the current
    ///     version, so a caller does not have to know what that version is to ask the question.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<ProjectionLag> Filter(IEnumerable<ProjectionLag> lags, ShardName name)
    {
        return lags.Where(x =>
                string.Equals(x.Shard.Name, name.Name, StringComparison.OrdinalIgnoreCase)
                && (name.ShardKey == ShardName.All ||
                    string.Equals(x.Shard.ShardKey, name.ShardKey, StringComparison.OrdinalIgnoreCase))
                && (name.TenantId == null || string.Equals(x.Shard.TenantId, name.TenantId)))
            .ToList();
    }
}
