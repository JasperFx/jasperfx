using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Grouping;

// marten#6003: the declared grouping rules whose identity can be found again from the identity alone, and the record
// of every rule a slicer was given, so the projection can say how it finds one identity's events.
[UnconditionalSuppressMessage("Trimming", "IL2087:DynamicallyAccessedMembers",
    Justification = "TId and TTag are a projection's identity and tag types, preserved where the projection is registered.")]
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "TId and TTag are a projection's identity and tag types, preserved where the projection is registered.")]
internal static class IdentityRules
{
    // IdentityFromStream<TEvent>(): the event's stream id or key, a strong-typed identifier wrapped
    public static Action<IReadOnlyList<IEvent>, IEventGrouping<TId>> FromStream<TEvent, TId>() where TId : notnull
    {
        var identity = IEvent.CreateAggregateIdentitySource<TId>();

        return (events, grouping) =>
        {
            foreach (var @event in events)
            {
                if (@event.Data is TEvent) grouping.AddEvent(identity(@event), @event);
            }
        };
    }

    // IdentityFromTag<TTag>(): every stored tag of type TTag on the event, as the identity
    public static Action<IReadOnlyList<IEvent>, IEventGrouping<TId>> FromTag<TTag, TId>() where TId : notnull
        where TTag : notnull
    {
        var convert = tagToIdentity<TTag, TId>();

        return (events, grouping) =>
        {
            foreach (var @event in events)
            {
                if (@event.Tags is not { Count: > 0 } tags) continue;

                TId? first = default;
                List<TId>? more = null;
                var any = false;
                foreach (var tag in tags)
                {
                    if (tag.TagType != typeof(TTag)) continue;

                    var id = convert((TTag)tag.Value);
                    if (!any)
                    {
                        first = id;
                        any = true;
                    }
                    else if (!id.Equals(first) && (more == null || !more.Contains(id)))
                    {
                        (more ??= new()).Add(id);
                    }
                }

                if (!any) continue;

                grouping.AddEvent(first!, @event);
                if (more == null) continue;

                foreach (var id in more) grouping.AddEvent(id, @event);
            }
        };
    }

    private static Func<TTag, TId> tagToIdentity<TTag, TId>() where TTag : notnull where TId : notnull
    {
        if (typeof(TTag) == typeof(TId)) return tag => (TId)(object)tag;

        if (StrongTypedIdResolver.TryResolve(typeof(TTag), out var info, StrongTypedIdStyle.AnyType) &&
            info.SimpleType == typeof(TId))
        {
            return tag => (TId)StrongTypedIdResolver.Unwrap(info, tag);
        }

        throw new InvalidProjectionException(
            $"IdentityFromTag<{typeof(TTag).FullNameInCode()}>() needs the projection's identity to be {typeof(TTag).FullNameInCode()} or the simple type it wraps, but it is {typeof(TId).FullNameInCode()}");
    }
}

/// <summary>
/// marten#6003: what a slicer knows about how it groups, enough to classify the projection.
/// </summary>
internal sealed class IdentityRuleLog
{
    private readonly List<(IdentityAccessClass Class, string Rule)> _rules = new();
    private readonly List<Type> _tagTypes = new();

    /// <summary>Is any rule <c>IdentityFromStream</c>?</summary>
    public bool HasStreamRules => _rules.Any(x => x.Class == IdentityAccessClass.Stream);

    /// <summary>The tag types of the <c>IdentityFromTag</c> rules.</summary>
    public IReadOnlyList<Type> TagTypes => _tagTypes;

    public void Stream(Type eventType) => _rules.Add((IdentityAccessClass.Stream,
        $"IdentityFromStream<{eventType.ShortNameInCode()}>()"));

    public void Tag(Type tagType)
    {
        _rules.Add((IdentityAccessClass.Tag, $"IdentityFromTag<{tagType.ShortNameInCode()}>()"));
        if (!_tagTypes.Contains(tagType)) _tagTypes.Add(tagType);
    }

    public void Delegate(string rule) => _rules.Add((IdentityAccessClass.ScanOnly, rule));

    public IdentityAccess Classify()
    {
        if (_rules.Count == 0)
        {
            return IdentityAccess.ScanOnly("The projection has no grouping rules this slicer can see.");
        }

        var scanOnly = _rules.Where(x => x.Class == IdentityAccessClass.ScanOnly).Select(x => x.Rule).ToArray();
        if (scanOnly.Length > 0)
        {
            return IdentityAccess.ScanOnly(
                $"{string.Join(", ", scanOnly)} can't be inverted to find one identity's events. Declare the grouping with IdentityFromStream<TEvent>() or IdentityFromTag<TTag>() instead, or turn on the slice index with UseSliceIndex().");
        }

        var rules = string.Join(", ", _rules.Select(x => x.Rule));
        return _rules.Any(x => x.Class == IdentityAccessClass.Tag)
            ? new IdentityAccess(IdentityAccessClass.Tag, $"Every grouping rule is declared by stream or by tag: {rules}.")
            : new IdentityAccess(IdentityAccessClass.Stream, $"Every grouping rule is declared by stream: {rules}.");
    }
}
