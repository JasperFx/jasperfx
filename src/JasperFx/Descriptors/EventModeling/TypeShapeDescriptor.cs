using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// The fields of one modelled type — a command, an event, a read model, an aggregate (jasperfx#1299). Model
/// level, on <see cref="EventModelDescriptor.Shapes"/>, because one type appears in many slices.
/// </summary>
/// <remarks>
/// <para>
/// <b>The disagreement rule.</b> Once the type exists in code, a declared field with no derived counterpart is a
/// <see cref="HotspotOrigin.SourceDisagreement"/> on <see cref="EventModelRole.Fields"/>; a derived field the
/// declaration lacks is not, because code may carry more. The declared field's <see cref="FieldDescriptor.Mapping"/>,
/// <see cref="FieldDescriptor.Tag"/> and <see cref="FieldDescriptor.Description"/> travel onto the derived field,
/// as a stream lane travels onto a derived event.
/// </para>
/// </remarks>
/// <param name="Type">The modelled type.</param>
/// <param name="Fields">Its fields, in declaration order.</param>
public sealed record TypeShapeDescriptor(TypeDescriptor Type, IReadOnlyList<FieldDescriptor> Fields)
{
    /// <summary>The fields, in declaration order. Never null (jasperfx#807's treatment).</summary>
    public IReadOnlyList<FieldDescriptor> Fields { get; init; } = Fields ?? Array.Empty<FieldDescriptor>();

    /// <summary>
    /// Which rung of the provenance ladder described this shape. Null means unattributed, read as
    /// <see cref="EventModelProvenance.Declared"/>. Stamped by <see cref="EventModelDescriptor.WithProvenance"/>;
    /// the source generator stamps <see cref="EventModelProvenance.Derived"/> itself.
    /// </summary>
    public EventModelProvenance? Provenance { get; init; }

    /// <summary>The field named <paramref name="name"/>, matched case-insensitively, or null.</summary>
    public FieldDescriptor? Find(string name)
        => Fields.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fold another source's shape of the <em>same</em> type into this one: the higher rung's fields, with the
    /// lower rung's declared-only detail carried onto matching fields, and every lower-rung field the higher
    /// rung lacks reported through <paramref name="disagreement"/>. Same-rung shapes union by field name,
    /// first wins.
    /// </summary>
    public TypeShapeDescriptor Merge(TypeShapeDescriptor other, Action<HotspotDescriptor>? disagreement = null)
    {
        if (!EventModelSliceDescriptor.SameType(Type, other.Type))
        {
            throw new ArgumentException(
                $"Cannot merge the shape of {other.Type.Name} into the shape of {Type.Name}; shapes merge by type.",
                nameof(other));
        }

        var mine = Provenance ?? EventModelProvenance.Declared;
        var theirs = other.Provenance ?? EventModelProvenance.Declared;

        // A real type replaces a declaration in place, as the aggregates do (jasperfx#958).
        var type = string.IsNullOrEmpty(Type.AssemblyName) && !string.IsNullOrEmpty(other.Type.AssemblyName)
            ? other.Type
            : Type;

        if (mine == theirs)
        {
            var fields = Fields.ToList();
            foreach (var field in other.Fields)
            {
                var index = fields.FindIndex(x => string.Equals(x.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) fields.Add(field);
                else fields[index] = carryOver(fields[index], field);
            }

            return this with { Type = type, Fields = fields, Provenance = Provenance ?? other.Provenance };
        }

        var winner = theirs.Outranks(mine) ? other : this;
        var loser = ReferenceEquals(winner, this) ? other : this;
        var winnerRung = ReferenceEquals(winner, this) ? mine : theirs;
        var loserRung = ReferenceEquals(winner, this) ? theirs : mine;

        var merged = winner.Fields.ToList();
        var missing = new List<string>();
        foreach (var field in loser.Fields)
        {
            var index = merged.FindIndex(x => string.Equals(x.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                missing.Add(field.Name);
                continue;
            }

            merged[index] = carryOver(merged[index], field);
        }

        if (missing.Count > 0 && disagreement is not null)
        {
            var kept = new EventModelClaim(winnerRung, string.Join(", ", winner.Fields.Select(x => x.Name)));
            var dropped = new EventModelClaim(loserRung, string.Join(", ", missing));
            var hotspot = HotspotDescriptor.SourceDisagreement(EventModelRole.Fields, kept, dropped);

            disagreement(hotspot with
            {
                Text = $"{EventModelRole.Fields} of {type.Name}: {kept.Claimant} claims {kept.Value}; " +
                       $"{dropped.Claimant} also declares {dropped.Value}, which {kept.Claimant} lacks",
            });
        }

        return new TypeShapeDescriptor(type, merged) { Provenance = winner.Provenance ?? loser.Provenance };
    }

    /// <summary>
    /// The declared-only detail a lower-rung field carries onto the one that won: a mapping, a tag, a
    /// description — nothing the code could have said.
    /// </summary>
    private static FieldDescriptor carryOver(FieldDescriptor kept, FieldDescriptor from)
        => kept with
        {
            Mapping = kept.Mapping ?? from.Mapping,
            Tag = kept.Tag ?? from.Tag,
            Description = richer(kept.Description, from.Description),
            Generated = kept.Generated || from.Generated,
            Pii = kept.Pii || from.Pii,
        };

    private static string? richer(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return second!.Length > first!.Length ? second : first;
    }

    // A record compares a collection member by reference (the jasperfx#853 lesson); a wire descriptor wants
    // value equality so a round trip equals what went in.

    /// <summary>Value equality, comparing <see cref="Fields"/> element-wise.</summary>
    public bool Equals(TypeShapeDescriptor? other)
        => other is not null && Type == other.Type && Provenance == other.Provenance && Fields.SequenceEqual(other.Fields);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.Add(Provenance);
        foreach (var field in Fields) hash.Add(field);
        return hash.ToHashCode();
    }
}
