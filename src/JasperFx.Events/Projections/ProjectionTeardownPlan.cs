using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections.Composite;

namespace JasperFx.Events.Projections;

/// <summary>
///     The store's half of <see cref="ProjectionTeardownPlan.ResolveTables" />: where the things a plan can only
///     name by type actually live. Both members are required so that a store cannot leave a category out by
///     not looking at it.
/// </summary>
public interface IProjectionTableResolver
{
    /// <summary>
    ///     The table holding documents of <paramref name="documentType" />, or null when the store does not own
    ///     them, such as a type stored through EF Core. A store whose external provider registers the table it
    ///     writes returns that table instead.
    /// </summary>
    string? DocumentTableFor(Type documentType);

    /// <summary>
    ///     The natural key lookup table of <paramref name="aggregateType" />.
    /// </summary>
    string NaturalKeyTableFor(Type aggregateType);
}

/// <summary>
///     Everything a rebuild of one projection has to clear first, in store-neutral terms. The plan says what;
///     the store owns the SQL, the transaction, any existence guard, and tenant scoping. See jasperfx#1046.
/// </summary>
/// <remarks>
///     <para>
///         The stores had each grown part of this. Marten and Polecat walked composites, natural keys and
///         <see cref="AsyncOptions.CleanUps" />, Fisher covered flat tables and storage providers, and each
///         missing category failed the same silent way: the progression row is deleted, so the rebuild restarts
///         from zero and replays onto the previous run's rows. A plan built once holds the union.
///     </para>
///     <para>
///         <b>Document types are <see cref="AsyncOptions.CleanUps" /> plus <c>PublishedTypes()</c>, unless the
///         projection turned off <c>DeletePublishedTypesOnTeardown</c></b>. Marten reads only the clean-ups and
///         misses a hand-written projection that publishes a type and declares no rule. Polecat and Fisher read
///         only <c>PublishedTypes()</c>, which still lists a type after its author opted out, so an append-only
///         audit table the author excluded (jasperfx#626) was truncated anyway. A composite contributes its own
///         clean-ups and leaves its published types to its members, because <c>PublishedTypes()</c> on a composite
///         fans out to them and would reach past a member's own opt-out.
///     </para>
///     <para>
///         <b><see cref="AsyncOptions.TeardownDataOnRebuild" /> governs every data category</b>: no store honoured
///         it across all of them. On a composite it covers the members too, since a composite is rebuilt as a
///         unit and an operator who turned teardown off for it did not mean "except for everything in it". A
///         member can still opt out alone. <see cref="Shards" /> and <see cref="DeadLetterProjectionNames" /> are
///         not data and are listed regardless: a rebuild restarts them either way.
///     </para>
/// </remarks>
public sealed class ProjectionTeardownPlan
{
    private readonly List<Type> _documentTypes = [];
    private readonly List<Type> _naturalKeyAggregateTypes = [];
    private readonly List<ShardName> _shards = [];
    private readonly List<string> _tables = [];

    private ProjectionTeardownPlan(string projectionName)
    {
        ProjectionName = projectionName;
    }

    /// <summary>
    ///     The plan for rebuilding <paramref name="source" />, walking a composite's members at any depth.
    /// </summary>
    public static ProjectionTeardownPlan For<TOperations, TQuerySession>(
        IProjectionSource<TOperations, TQuerySession> source)
        where TOperations : TQuerySession, IStorageOperations
    {
        var plan = new ProjectionTeardownPlan(source.Name);
        plan.collectShards(source);
        plan.collectData(source);

        return plan;
    }

    /// <summary>The projection this plan was built for.</summary>
    public string ProjectionName { get; }

    /// <summary>
    ///     Document types to delete. A store that clears documents by type rather than by table reads this
    ///     directly; any other goes through <see cref="ResolveTables" />.
    /// </summary>
    public IReadOnlyList<Type> DocumentTypes => _documentTypes;

    /// <summary>
    ///     Tables named outright, by <see cref="DeleteTableData" /> or <see cref="IPublishesTables" />.
    /// </summary>
    public IReadOnlyList<string> Tables => _tables;

    /// <summary>
    ///     The aggregate types whose natural key lookup table has to go too, so the rebuild repopulates it.
    /// </summary>
    public IReadOnlyList<Type> NaturalKeyAggregateTypes => _naturalKeyAggregateTypes;

    /// <summary>
    ///     The store-global shards whose progression rows to clear: the projection's own, then a composite's
    ///     members under the names their executions use. A member left at its old ceiling fails the replay's first
    ///     batch as out of order. Under per-tenant partitioning each one also has a row per tenant, reached with
    ///     <see cref="ShardName.ForTenant" />.
    /// </summary>
    public IReadOnlyList<ShardName> Shards => _shards;

    /// <summary>
    ///     The projections whose dead letters to clear, because the replay is going to process those events
    ///     again.
    /// </summary>
    public IReadOnlyList<string> DeadLetterProjectionNames => _shards.Select(x => x.Name).Distinct().ToArray();

    /// <summary>
    ///     Every table to clear, each once, with the store's own answers filled in. A duplicate is only ever
    ///     dropped when the spelling matches exactly: deleting a table twice is harmless, and skipping a
    ///     table is not.
    /// </summary>
    public IReadOnlyList<string> ResolveTables(IProjectionTableResolver resolver)
    {
        var tables = new List<string>();

        tables.AddRange(_documentTypes.Select(resolver.DocumentTableFor).OfType<string>());
        tables.AddRange(_tables);
        tables.AddRange(_naturalKeyAggregateTypes.Select(resolver.NaturalKeyTableFor));

        return tables.Distinct(StringComparer.Ordinal).ToArray();
    }

    private void collectShards<TOperations, TQuerySession>(IProjectionSource<TOperations, TQuerySession> source)
        where TOperations : TQuerySession, IStorageOperations
    {
        foreach (var shard in source.Shards())
        {
            addTo(_shards, shard.Name);

            if (source is CompositeProjection<TOperations, TQuerySession> composite)
            {
                foreach (var member in composite.ConstituentShardNames(shard.Name))
                {
                    addTo(_shards, member);
                }
            }
        }
    }

    private void collectData<TOperations, TQuerySession>(IProjectionSource<TOperations, TQuerySession> source)
        where TOperations : TQuerySession, IStorageOperations
    {
        if (!source.Options.TeardownDataOnRebuild) return;

        foreach (var cleanUp in source.Options.CleanUps)
        {
            switch (cleanUp)
            {
                case DeleteDocuments documents:
                    addTo(_documentTypes, documents.DocumentType);
                    break;

                case DeleteTableData tableData:
                    addTo(_tables, tableData.TableIdentifier);
                    break;
            }
        }

        var composite = source as CompositeProjection<TOperations, TQuerySession>;

        if (composite == null && source.Options.DeletePublishedTypesOnTeardown)
        {
            foreach (var documentType in source.PublishedTypes())
            {
                addTo(_documentTypes, documentType);
            }
        }

        if (source is IPublishesTables publisher)
        {
            foreach (var table in publisher.PublishedTableNames())
            {
                addTo(_tables, table);
            }
        }

        if (source is IAggregateProjection { NaturalKeyDefinition: { } naturalKey })
        {
            addTo(_naturalKeyAggregateTypes, naturalKey.AggregateType);
        }

        if (composite != null)
        {
            foreach (var member in composite.AllProjections())
            {
                collectData(member);
            }
        }
    }

    private static void addTo<T>(List<T> list, T item)
    {
        if (!list.Contains(item)) list.Add(item);
    }
}
