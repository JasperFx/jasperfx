namespace JasperFx.Events.EventModeling;

/// <summary>
/// What starts a slice (jasperfx#687 decision 8). Finer-grained than the
/// "human / external" split of textbook Event Modeling because the Critter
/// Stack can tell its own trigger kinds apart statically.
/// </summary>
/// <remarks>
/// <para>
/// Normally derived by the producing source. A declaration (an Event Model board, the fluent
/// <c>EventModelBuilder</c>) can also claim one, and usually claims <see cref="Human"/>. A slice
/// whose trigger kind nobody has claimed carries <see langword="null"/>.
/// </para>
/// <para>
/// <b>Who versus how.</b> <see cref="Human"/> answers <em>who</em> triggers a slice; every other member
/// answers <em>how</em> the code receives it. A declared <see cref="Human"/> met by a derived
/// <see cref="Http"/>, <see cref="Grpc"/> or <see cref="MessageHandler"/> is therefore not a
/// disagreement: <see cref="EventModelSliceDescriptor.Merge"/> keeps the derived kind on
/// <see cref="EventModelSliceDescriptor.TriggerKind"/> and the <see cref="Human"/> on
/// <see cref="EventModelSliceDescriptor.TriggeredBy"/>. See <see cref="TriggerKindCompatibility"/>.
/// </para>
/// </remarks>
public enum TriggerKind
{
    /// <summary>An HTTP endpoint (e.g. a Wolverine.Http route). Route and verb live on the slice's trigger origin.</summary>
    Http,

    /// <summary>A gRPC service method. Service and method live on the slice's trigger origin.</summary>
    Grpc,

    /// <summary>An inbound message handled by a Wolverine message handler; the command type is the trigger.</summary>
    MessageHandler,

    /// <summary>
    /// A scheduled or recurring job. Reserved ahead of the Critter Stack job scheduler landing;
    /// sources may stamp it today for cron-style / scheduled publishers.
    /// </summary>
    JobScheduler,

    /// <summary>A person acting through a UI / wireframe.</summary>
    Human,

    /// <summary>An external system pushing into the model (the inbound half of a translation slice).</summary>
    External,
}

/// <summary>
/// Which <see cref="TriggerKind"/> claims describe one trigger from two angles rather than disagree
/// about it: <see cref="TriggerKind.Human"/> says <em>who</em> triggers a slice, and the kinds through
/// which a person or a UI can reach code say <em>how</em> it is received.
/// </summary>
public static class TriggerKindCompatibility
{
    /// <summary>
    /// Whether a person, or the UI they act through, can reach code through <paramref name="kind"/>:
    /// <see cref="TriggerKind.Http"/>, <see cref="TriggerKind.Grpc"/> and
    /// <see cref="TriggerKind.MessageHandler"/>. <see cref="TriggerKind.JobScheduler"/> and
    /// <see cref="TriggerKind.External"/> are automation, not a person, and <see cref="TriggerKind.Human"/>
    /// is the <em>who</em> rather than a <em>how</em>.
    /// </summary>
    public static bool CanBeHumanTriggered(this TriggerKind kind)
        => kind is TriggerKind.Http or TriggerKind.Grpc or TriggerKind.MessageHandler;

    /// <summary>
    /// Whether two claims about one slice's trigger kind are compatible: the same kind, or
    /// <see cref="TriggerKind.Human"/> on one side and a kind that <see cref="CanBeHumanTriggered"/> on
    /// the other. Symmetric. Anything else is a <see cref="HotspotOrigin.SourceDisagreement"/>.
    /// </summary>
    public static bool IsCompatibleWith(this TriggerKind kind, TriggerKind other)
        => kind == other || SplitsWhoFromHow(kind, other);

    /// <summary>
    /// The two claims are a <em>who</em> (<see cref="TriggerKind.Human"/>) and a compatible <em>how</em>,
    /// so a merge keeps both rather than choosing one.
    /// </summary>
    internal static bool SplitsWhoFromHow(TriggerKind kind, TriggerKind other)
        => (kind == TriggerKind.Human && other.CanBeHumanTriggered())
           || (other == TriggerKind.Human && kind.CanBeHumanTriggered());
}
