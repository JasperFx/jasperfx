namespace JasperFx.Events.Appending;

/// <summary>
///     What a plan decided for one stream action.
/// </summary>
public sealed class StreamAppendStep
{
    internal StreamAppendStep(StreamAction stream, StreamAppendRule rule, long currentVersion)
    {
        Stream = stream;
        Rule = rule;
        CurrentVersion = currentVersion;
    }

    /// <summary>
    ///     The action this step is about.
    /// </summary>
    public StreamAction Stream { get; }

    /// <summary>
    ///     The row of the decision table that applied.
    /// </summary>
    public StreamAppendRule Rule { get; }

    /// <summary>
    ///     What to do with the stream.
    /// </summary>
    public StreamAppendVerdict Verdict => Rule.Verdict;

    /// <summary>
    ///     The version this step starts from: what the store read, or where the action before it in the
    ///     same plan left the stream.
    /// </summary>
    public long CurrentVersion { get; }

    /// <summary>
    ///     The version of the stream once this step has been written.
    /// </summary>
    public long EndingVersion => IsWrite ? CurrentVersion + Stream.Events.Count : CurrentVersion;

    /// <summary>
    ///     Does this step write to the database?
    /// </summary>
    public bool IsWrite => Verdict is StreamAppendVerdict.InsertStream or StreamAppendVerdict.UpdateStream;

    /// <summary>
    ///     Is this step a refusal?
    /// </summary>
    public bool IsRefused => Verdict is not (StreamAppendVerdict.Skip or StreamAppendVerdict.Verified
        or StreamAppendVerdict.InsertStream or StreamAppendVerdict.UpdateStream);

    /// <summary>
    ///     What <see cref="StreamAction.ExpectedVersionOnServer" /> becomes.
    /// </summary>
    /// <remarks>
    ///     An update carries the version just read, which is what the stream row write's
    ///     <c>and version = @expected</c> compares against, and stays as the backstop behind a read that
    ///     turned out not to be held. An insert keeps whatever the caller said: Fisher cleared it, which
    ///     erases the guard for a transient retry that re-enters the plan and now finds the stream there.
    ///     Only the slice's guess on a raised event is dropped, because it was never a claim.
    /// </remarks>
    internal long? ExpectedVersionAfter(AppendOrigin origin)
    {
        if (Verdict == StreamAppendVerdict.UpdateStream) return CurrentVersion;

        return origin == AppendOrigin.RaisedByProjection ? null : Stream.ExpectedVersionOnServer;
    }
}

/// <summary>
///     The decisions for one unit of work, from <see cref="ClientSideAppendPlanner.Plan" />.
/// </summary>
public sealed class ClientSideAppendPlan
{
    private readonly ClientSideAppendContext _context;

    internal ClientSideAppendPlan(IReadOnlyList<StreamAppendStep> steps, ClientSideAppendContext context)
    {
        Steps = steps;
        _context = context;
    }

    /// <summary>
    ///     One step per action, in the order the actions came in.
    /// </summary>
    public IReadOnlyList<StreamAppendStep> Steps { get; }

    /// <summary>
    ///     The steps that write, in the order they have to be written: an action folded onto an earlier one
    ///     depends on that write having landed.
    /// </summary>
    public IEnumerable<StreamAppendStep> Writes => Steps.Where(x => x.IsWrite);

    /// <summary>
    ///     The first refused step, if any. First in input order, so which of several refusals a caller sees
    ///     does not depend on how a store batches its reads.
    /// </summary>
    public StreamAppendStep? Refusal => Steps.FirstOrDefault(x => x.IsRefused);

    /// <summary>
    ///     Throw the refusal, or stamp every event in the plan and record the stream versions, and only then.
    ///     One call, so that a store cannot write a plan whose refusal it forgot to look at.
    /// </summary>
    /// <remarks>
    ///     After this, every event carries its version, id, tenant and timestamp, and every written action
    ///     carries the <see cref="StreamAction.Version" /> and
    ///     <see cref="StreamAction.ExpectedVersionOnServer" /> the stream row write needs. No I/O.
    /// </remarks>
    public void Apply()
    {
        if (Refusal is { } refused) throw exceptionFor(refused);

        var sequence = 0;

        foreach (var step in Writes)
        {
            ClientSideAppendPlanner.Stamp(step.Stream, step.CurrentVersion, _context);
            step.Stream.ExpectedVersionOnServer = step.ExpectedVersionAfter(_context.Origin);

            if (_context.AppendMode != EventAppendMode.Rich) continue;

            foreach (var @event in step.Stream.Events)
            {
                @event.Sequence = _context.ReservedSequences![sequence++];
            }
        }
    }

    private Exception exceptionFor(StreamAppendStep step)
    {
        var stream = step.Stream;
        var id = ClientSideAppendPlanner.streamId(stream);
        var refusals = _context.Refusals;

        return step.Verdict switch
        {
            StreamAppendVerdict.RefuseCollision => refusals.Collision(id, stream.AggregateType),
            StreamAppendVerdict.RefuseArchived => refusals.Archived(id),
            StreamAppendVerdict.RefuseUnexpectedVersion => refusals.UnexpectedVersion(id, stream.AggregateType,
                stream.ExpectedVersionOnServer!.Value, step.CurrentVersion),
            StreamAppendVerdict.RefuseMixedTenants => new InvalidOperationException(
                $"Stream '{id}' of tenant '{stream.TenantId}' holds an event of another tenant. Refused rather than moved: one stream row cannot hold two tenants' events."),
            StreamAppendVerdict.RefuseUnreadRow => new InvalidOperationException(
                $"The store did not read stream '{id}', which a client-side append has to number from its current version."),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Verdict, "Not a refusal")
        };
    }
}
