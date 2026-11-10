using JasperFx.Events.Protected;

namespace JasperFx.Events;

public partial class EventRegistry
{
    private readonly List<IMasker> _maskers = [];

    /// <summary>
    ///     Register a policy for how to remove or mask protected information for an event type
    ///     <typeparamref name="T" /> or series of event types that can be cast to <typeparamref name="T" />
    /// </summary>
    /// <remarks>
    ///     Use this for an event with settable members. A <c>record</c> rewritten with a <c>with</c>
    ///     expression needs the <see cref="AddMaskingRuleForProtectedInformation{T}(Func{T,T})" /> overload,
    ///     or the mutation is discarded.
    /// </remarks>
    /// <param name="action">Action to mask the current object</param>
    public void AddMaskingRuleForProtectedInformation<T>(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Unconstrained because Marten's IEventStoreOptions declares these unconstrained, and an inherited
        // member can only implement it as is. Polecat's and Fisher's notnull callers narrow from here.
#pragma warning disable CS8714
        _maskers.Add(new ActionMasker<T>(action));
#pragma warning restore CS8714
    }

    /// <summary>
    ///     Register a policy for how to remove or mask protected information for an event type
    ///     <typeparamref name="T" /> or series of event types that can be cast to
    ///     <typeparamref name="T" />, replacing the event data with what <paramref name="func" /> returns
    /// </summary>
    /// <remarks>
    ///     A rule against a base type or interface has to return something the event can still hold, so
    ///     normally an instance of the same runtime type. Anything else fails the masking pass rather than
    ///     leaving the event unmasked.
    /// </remarks>
    /// <param name="func">Function to replace the event with a masked event</param>
    public void AddMaskingRuleForProtectedInformation<T>(Func<T, T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

#pragma warning disable CS8714
        _maskers.Add(new FuncMasker<T>(func));
#pragma warning restore CS8714
    }

    /// <summary>
    ///     Apply every masking rule that matches this event, and report whether any did. An event that
    ///     no rule matched is left alone, and is not worth rewriting.
    /// </summary>
    public bool TryMask(IEvent @event)
    {
        var matched = false;
        foreach (var masker in _maskers)
        {
            // |= and NOT ||=. With the short-circuiting form, the first rule to match an event
            // stopped every later rule from even being invoked, so only one of several applicable
            // rules ever ran -- and the operation still reported success. For a right-to-erasure
            // feature that means protected information silently survives a masking pass.
            matched |= masker.TryMask(@event);
        }

        return matched;
    }
}
