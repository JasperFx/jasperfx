using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Events.Projections.Composite;

public class CompositeProjection<TOperations, TQuerySession> : ProjectionBase, IJasperFxProjection<TOperations>, IProjectionSource<TOperations, TQuerySession>, ISubscriptionFactory<TOperations, TQuerySession>, IHasIdentityAccess where TOperations : TQuerySession, IStorageOperations
{
    /// <summary>
    /// marten#6003: a composite projection does not park in this version. A parked upstream document would feed stale
    /// data to the stages after it, so a member that fails pauses the whole composite (or skips the event, if the error
    /// action is skip).
    /// </summary>
    public IdentityAccess IdentityAccess { get; } = IdentityAccess.NotSupported(
        "A composite projection does not park: a member that fails pauses the composite (or skips the event when the error action is Skip).");

    public CompositeProjection(string name)
    {
        Name = name;
        Version = 0;
        
        Stages.Add(new ProjectionStage<TOperations, TQuerySession>(1));
    }

    public IReadOnlyList<IProjectionSource<TOperations, TQuerySession>> AllProjections() =>
        Stages.SelectMany(x => x.Projections).ToList();

    /// <summary>
    /// jasperfx#684: let the members that did not change keep their documents and their progress when this composite
    /// replays from the start after a version bump. Off by default, which replays and rebuilds every member, as before.
    ///
    /// <para>Each member already runs under its own progression row, named for the member's own
    /// <see cref="ProjectionBase.Version" />. With this on, bump the version of each member you changed <i>and</i> the
    /// version of the composite. The composite then replays from the start. A member whose row for its current version
    /// is ahead of the composite's position skips every event at or below that row, and the members you changed
    /// (whose new version has no row yet) replay from the start in the same pass. A rebuild works the same way:
    /// <see cref="MembersToTearDownAsync" /> leaves the kept members standing.</para>
    ///
    /// <para>It only applies to a composite with a single stage. A member in a later stage reads the in-flight
    /// output of the stages before it, which a kept member would not produce, so a multi-stage composite replays every
    /// member as before (and logs a warning saying so). Turning it on also says that no member enriches itself from
    /// the stored documents of another member of this composite: a kept member's documents are already at their latest
    /// state while the changed members replay, so such an enrichment would read the future.</para>
    /// </summary>
    public bool KeepUnchangedMembers { get; set; }

    /// <summary>
    /// <see cref="KeepUnchangedMembers" /> is on and this composite has the single stage it needs.
    /// </summary>
    internal bool KeepsUnchangedMembers => KeepUnchangedMembers && Stages.Count(x => x.Projections.Count > 0) <= 1;

    /// <summary>
    /// jasperfx#684: the members a rebuild of this composite has to tear down, documents and progression rows both. That
    /// is every member, unless <see cref="KeepUnchangedMembers" /> applies, in which case a member whose progression row
    /// for its current version is ahead of the composite's own row is left out: it changed neither version nor code since
    /// it last ran, and the replay skips it up to that row. A member that is itself a composite is always included.
    ///
    /// <para>Stores call this from their teardown <b>before</b> deleting any progression row, and must leave a kept
    /// member's documents and progression row exactly as they are. Removing either one loses the position the replay
    /// skips it to.</para>
    /// </summary>
    /// <param name="database">The database being rebuilt</param>
    /// <param name="parent">
    /// The composite's shard name, carrying any tenant binding. Defaults to the store-global name this composite registers.
    /// </param>
    /// <param name="token"></param>
    public async Task<IReadOnlyList<IProjectionSource<TOperations, TQuerySession>>> MembersToTearDownAsync(
        IEventDatabase database, ShardName? parent = null, CancellationToken token = default)
    {
        var members = AllProjections();
        if (!KeepsUnchangedMembers || members.Count == 0) return members;

        parent ??= ShardName.Compose(Name, version: Version);
        var compositePosition = await database.ProjectionProgressFor(parent, token).ConfigureAwait(false);

        var stage = Stages.First(x => x.Projections.Count > 0);
        var toTearDown = new List<IProjectionSource<TOperations, TQuerySession>>();
        foreach (var member in members)
        {
            if (member is CompositeProjection<TOperations, TQuerySession>)
            {
                toTearDown.Add(member);
                continue;
            }

            var position = await database.ProjectionProgressFor(stage.MemberShardName(member, parent), token)
                .ConfigureAwait(false);
            if (position <= compositePosition) toTearDown.Add(member);
        }

        return toTearDown;
    }

    /// <summary>
    /// The shard name each member projection runs under — and so the name of the progression row it writes —
    /// when this composite runs as <paramref name="parent"/> (jasperfx#1080). A member that is itself a
    /// composite contributes its own members as well. The composite's own name is not included.
    /// </summary>
    /// <param name="parent">
    /// The composite's own shard name, carrying any tenant binding. Defaults to the store-global name this
    /// composite registers.
    /// </param>
    public IReadOnlyList<ShardName> ConstituentShardNames(ShardName? parent = null)
    {
        parent ??= ShardName.Compose(Name, version: Version);
        var names = new List<ShardName>();
        foreach (var stage in Stages)
        {
            foreach (var projection in stage.Projections)
            {
                var name = stage.MemberShardName(projection, parent);
                names.Add(name);
                if (projection is CompositeProjection<TOperations, TQuerySession> nested)
                {
                    names.AddRange(nested.ConstituentShardNames(name));
                }
            }
        }

        return names;
    }

    protected internal List<ProjectionStage<TOperations, TQuerySession>> Stages { get; } = new();

    protected internal ProjectionStage<TOperations, TQuerySession> StageFor(int stageNumber)
    {
        if (stageNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stageNumber),
                "The stages of a CompositeProjection are 1-based");
        }
        
        var stage = Stages.FirstOrDefault(x => x.Order == stageNumber);
        if (stage == null)
        {
            stage = new ProjectionStage<TOperations, TQuerySession>(stageNumber);
            Stages.Add(stage);
        }

        return stage;
    }

    bool IProjectionSource<TOperations, TQuerySession>.TryBuildReplayExecutor(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        if (!IsEligibleForReplay())
        {
            return false;
        }

        var shardName = ShardName.Compose(Name, version: Version);
        var execution = this.As<ISubscriptionFactory<TOperations, TQuerySession>>()
            .BuildExecution(store, database, Logger ?? NullLogger.Instance, shardName);

        return execution.TryBuildReplayExecutor(out executor);
    }

    /// <summary>
    /// A composite supports a single-pass rebuild only when it has at least one member and every member
    /// fans cleanly into one ordered pass (no custom-grouped/sliced members). See jasperfx#407 Phase A.
    /// </summary>
    internal bool IsEligibleForReplay()
    {
        var members = AllChildren().ToList();
        return members.Count > 0 && members.All(x => x.CanParticipateInCompositeReplay);
    }

    IInlineProjection<TOperations> IProjectionSource<TOperations, TQuerySession>.BuildForInline()
    {
        throw new NotSupportedException("Composite Projections must run asynchronously");
    }

    public override IEnumerable<Type> PublishedTypes()
    {
        // The composite's own declarations belong to no member, so nothing else would report them (fisher#63).
        // They come last because the stores read a projection's first published type as its state type.
        return Stages.SelectMany(stage => stage.Projections.SelectMany(x => x.PublishedTypes()))
            .Concat(base.PublishedTypes())
            .Distinct()
            .ToArray();
    }

    SubscriptionType ISubscriptionSource.Type => SubscriptionType.CompositeProjection;

    ShardName[] ISubscriptionSource.ShardNames()
    {
        return [ShardName.Compose(Name, version: Version)];
    }

    Type ISubscriptionSource.ImplementationType => GetType();

    SubscriptionDescriptor ISubscriptionSource.Describe(IEventStore store)
    {
        var descriptor = new SubscriptionDescriptor(this, store);
        var stages = descriptor.AddChildSet(nameof(Stages));
        foreach (var stage in Stages)
        {
            stages.Rows.Add(stage.ToDescription(store));
        }
        
        return descriptor;
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILoggerFactory loggerFactory,
        ShardName shardName)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        reportIgnoredMemberSettings(logger);
        var executionStages = Stages.Select(x => x.BuildExecution(store, database, loggerFactory, shardName)).ToArray();
        return new CompositeExecution<TOperations, TQuerySession>(shardName, Options, store, database, this,
            logger, executionStages, IsEligibleForReplay())
        {
            KeepsUnchangedMembers = KeepsUnchangedMembers
        };
    }

    ISubscriptionExecution ISubscriptionFactory<TOperations, TQuerySession>.BuildExecution(IEventStore<TOperations, TQuerySession> store, IEventDatabase database, ILogger logger, ShardName shardName)
    {
        reportIgnoredMemberSettings(logger);
        var executionStages = Stages.Select(x => x.BuildExecution(store, database, logger, shardName)).ToArray();
        return new CompositeExecution<TOperations, TQuerySession>(shardName, Options, store, database, this,
            logger, executionStages, IsEligibleForReplay())
        {
            KeepsUnchangedMembers = KeepsUnchangedMembers
        };
    }

    IReadOnlyList<AsyncShard<TOperations, TQuerySession>> ISubscriptionSource<TOperations, TQuerySession>.Shards()
    {
        var shardName = ShardName.Compose(Name, version: Version);
        return
        [
            new AsyncShard<TOperations, TQuerySession>(Options, ShardRole.Projection, shardName, this, this)
        ];
    }

    Task IJasperFxProjection<TOperations>.ApplyAsync(TOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        throw new NotSupportedException();
    }

    private int _ignoredMemberSettingsReported;

    // Once per composite, not per shard: a composite runs one shard per tenant and database.
    private void reportIgnoredMemberSettings(ILogger logger)
    {
        if (Interlocked.Exchange(ref _ignoredMemberSettingsReported, 1) == 1) return;

        if (KeepUnchangedMembers && !KeepsUnchangedMembers)
        {
            logger.LogWarning(
                "Composite projection '{Composite}' has KeepUnchangedMembers turned on, but it has more than one stage, so every member replays when it does. A member in a later stage reads the output of the stages before it as they replay, which a member that kept its progress would not produce",
                Name);
        }

        foreach (var (member, settings) in MembersWithIgnoredShardSettings())
        {
            logger.LogWarning(
                "Projection '{Member}' is a member of composite projection '{Composite}', which runs it in the composite's shard, so {Settings} on the member's Options has no effect. Set it on the composite's Options instead",
                member.Name, Name, string.Join(", ", settings));
        }
    }

    /// <summary>
    /// The members whose own options change a setting that only the shard reads. This composite's shard is
    /// built from its own <see cref="ProjectionBase.Options" />, so those member settings are never used. A
    /// nested composite reports its own members when it builds its execution.
    /// </summary>
    internal IEnumerable<(IProjectionSource<TOperations, TQuerySession> Member, IReadOnlyList<string> Settings)>
        MembersWithIgnoredShardSettings()
    {
        foreach (var member in AllChildren())
        {
            var settings = member.Options.ChangedShardSettings();
            if (settings.Count > 0) yield return (member, settings);
        }
    }

    internal IEnumerable<IProjectionSource<TOperations, TQuerySession>> AllChildren()
    {
        return Stages.SelectMany(x => x.Projections);
    }
}