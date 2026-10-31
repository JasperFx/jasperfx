namespace JasperFx.Events.Protected;

/// <summary>
/// One rule from <see cref="EventRegistry.AddMaskingRuleForProtectedInformation{T}(Action{T})" />: whether
/// it applies to an event, applying it when it does.
/// </summary>
internal interface IMasker
{
    bool TryMask(IEvent @event);
}

/// <summary>
/// How a <see cref="FuncMasker{T}" /> writes a replacement back when it only knows the event by a base
/// type or interface. Only the closed envelope can assign <c>Data</c>, and it cannot be named from there.
/// </summary>
internal interface IReplaceableEventData
{
    void ReplaceData(object data);
}

internal class ActionMasker<T> : IMasker where T : notnull
{
    private readonly Action<T> _masking;

    public ActionMasker(Action<T> masking)
    {
        _masking = masking;
    }

    public bool TryMask(IEvent @event)
    {
        // IEvent<T> rather than Event<T>: it is covariant, which is what lets one rule against a base type
        // or interface cover every event that implements it.
        if (@event is IEvent<T> e)
        {
            _masking(e.Data);
            return true;
        }

        return false;
    }
}

internal class FuncMasker<T> : IMasker where T : notnull
{
    private readonly Func<T, T> _masking;

    public FuncMasker(Func<T, T> masking)
    {
        _masking = masking;
    }

    public bool TryMask(IEvent @event)
    {
        // #1043: the stores tested Event<T> alone, so a rule against a base type or interface matched
        // nothing and reported nothing. Protected information then survived the pass even though the rule
        // was documented to cover "event types that can be cast to T", the same reach as ActionMasker.
        if (@event is IEvent<T> e and IReplaceableEventData replaceable)
        {
            replaceable.ReplaceData(_masking(e.Data));
            return true;
        }

        return false;
    }
}
