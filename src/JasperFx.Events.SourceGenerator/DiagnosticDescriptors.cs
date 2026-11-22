using Microsoft.CodeAnalysis;

namespace JasperFx.Events.SourceGenerator;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor CannotInferIdentity = new(
        id: "JFXEVT002",
        title: "Cannot infer identity type",
        messageFormat: "Cannot infer identity type for '{0}'; add [AggregateIdentity] or an Id property",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The projection has conventional methods the generator cannot dispatch without a second
    /// declaration of the user's class. There is no runtime fallback for this — the projection throws
    /// <c>InvalidProjectionException</c> when the store is built (see
    /// <c>JasperFxAggregationProjectionBase.AssembleAndAssertValidity</c>) — so this is an error rather
    /// than the Info-level "falling back to runtime expression compilation" note it used to be. That
    /// message described the pre-2.0 FEC fallback, which the 9.0 projections rework deleted, and Info
    /// severity kept it out of CLI builds entirely.
    /// </summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "JFXEVT003",
        title: "Projection must be declared partial",
        messageFormat:
            "No dispatcher can be generated for projection '{0}' because {1} — {2}. Conventional Apply/Create/ShouldDelete methods are dispatched by the compile-time source generator and there is no runtime fallback.",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// An EventProjection's explicit ApplyAsync writes a document whose type the generator cannot
    /// name, so no published-type registration is emitted for it. Silent otherwise: the store will
    /// still provision that document's storage on demand, and only the ahead-of-time surfaces
    /// (schema creation, known document types, rebuild teardown) come up short.
    /// </summary>
    public static readonly DiagnosticDescriptor UnregistrableDocumentOperation = new(
        id: "JFXEVT005",
        title: "Document type cannot be registered from this operation",
        messageFormat:
            "'{0}' calls {1} on its session with document type '{2}', which cannot be registered as a published type; call it with a concrete document type so the projection registers it",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>
    /// An EventProjection with an explicit <c>ApplyAsync</c> writes documents the generator can name,
    /// but the registration is a <c>PublishedTypes()</c> override emitted into the projection class, so
    /// a non-partial projection cannot receive one. Warning rather than error: nothing fails at runtime,
    /// the store provisions that document's storage on demand, and only the ahead-of-time surfaces
    /// (schema creation, known document types, rebuild teardown) come up short. Before this the skip was
    /// entirely silent. See #654.
    /// </summary>
    public static readonly DiagnosticDescriptor UnregisteredPublishedTypes = new(
        id: "JFXEVT006",
        title: "Published document types cannot be registered",
        messageFormat:
            "'{0}' writes document type(s) {1} from its ApplyAsync override, but is not declared partial, so they are not registered as published types; declare the projection partial, or register them with RegisterPublishedType",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A type carries <c>[BoundaryAggregate]</c> but the generator found nothing to fold events with,
    /// so no evolver is emitted and <c>FetchForWritingByTags&lt;T&gt;</c> will throw
    /// <c>InvalidProjectionException: No source-generated dispatcher found</c> the first time the type
    /// is fetched.
    /// </summary>
    /// <remarks>
    /// Error rather than warning, and unlike a bare no-<c>Id</c> aggregate — where silence is correct,
    /// because a missing <c>Id</c> is far more likely a mistake than an intentional boundary aggregate
    /// (#324) — this attribute is an explicit opt-in. A type carrying it and generating nothing is
    /// unambiguously wrong, and the runtime failure it produces arrives at fetch time naming neither
    /// the type nor the reason. See #730.
    /// </remarks>
    public static readonly DiagnosticDescriptor BoundaryAggregateWithoutEvolver = new(
        id: "JFXEVT007",
        title: "Boundary aggregate has nothing to fold events with",
        messageFormat:
            "'{0}' is marked [BoundaryAggregate] but declares no Evolve method and no conventional Apply/Create methods, so no evolver can be generated and fetching it by tags will fail at runtime",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A self-aggregating type has conventional methods, but its evolver would be a <c>file</c>-scoped
    /// type at namespace level registered by an assembly-level <c>typeof(...)</c>, and neither can name an
    /// open generic type (CS0246) or one behind <c>private</c> / <c>protected</c> (CS0122). Before #1194
    /// the generator emitted that code anyway and broke the consumer's build inside a file they cannot
    /// edit. Now nothing is emitted from the declaration and this explains what does reach it.
    /// </summary>
    /// <remarks>
    /// Warning rather than error: the build is fine, and the type may never be fetched or projected on
    /// its own. Not reported at all when the same compilation already generates a dispatcher that covers
    /// the type — a closed form of a generic aggregate from a <c>Snapshot&lt;T&gt;</c>-style call site, or
    /// a partial projection over it — because then nothing is missing.
    /// </remarks>
    public static readonly DiagnosticDescriptor UnnameableSelfAggregate = new(
        id: "JFXEVT008",
        title: "Aggregate type cannot be named by a generated evolver",
        messageFormat:
            "No evolver is generated from '{0}' because {1}, so a generated file cannot name it; {2}",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A method on a projection or self-aggregating type has the name and parameter shape of a
    /// conventional handler (Apply, Create, ShouldDelete, or Project / Create / Transform on an
    /// EventProjection), or a constructor has the shape of an implicit Create, but it is not public.
    /// Generated dispatch calls public members only, so the projection compiles, runs and never applies
    /// that event. Before generated dispatch handlers were found by reflection, so this is exactly the
    /// shape an upgrade leaves behind. See #1196.
    /// </summary>
    /// <remarks>
    /// Warning rather than error, so a deliberately private helper that happens to be called Apply can
    /// be suppressed, renamed, or marked [JasperFxIgnore].
    /// </remarks>
    public static readonly DiagnosticDescriptor NonPublicHandler = new(
        id: "JFXEVT009",
        title: "Conventional projection handler is not public",
        messageFormat: "'{0}' looks like a projection handler but is not public, so it will not be called. {1}",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // ── Natural keys (JFXEVT010-015). The runtime raises the same per-aggregate problems from
    //    AssembleAndAssertValidity; these move them to build time, and add the cross-projection checks
    //    on SourceProjection that the runtime can only make once every projection is registered. ──

    /// <summary>
    /// More than one owned natural key on one aggregate. Discovery refuses it at configuration time, and
    /// this reports it at build time.
    /// </summary>
    public static readonly DiagnosticDescriptor MultipleOwnedNaturalKeys = new(
        id: "JFXEVT010",
        title: "An aggregate can own at most one natural key",
        messageFormat:
            "'{0}' marks both '{1}' and '{2}' with [NaturalKey], but an aggregate can own at most one natural key; map the others to the projection that owns them with [MappedNaturalKey<T>]",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Fetching by natural key is keyed on the .NET type of the key, so two keys of one type on the same
    /// aggregate would be indistinguishable.
    /// </summary>
    public static readonly DiagnosticDescriptor DuplicateNaturalKeyType = new(
        id: "JFXEVT011",
        title: "Natural keys on one aggregate need distinct types",
        messageFormat:
            "'{0}' has natural keys '{1}' and '{2}' of the same type '{3}'; every natural key on an aggregate needs a distinct type",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NaturalKeySourceIsSelf = new(
        id: "JFXEVT012",
        title: "A natural key cannot be mapped to its own aggregate",
        messageFormat:
            "Natural key '{0}.{1}' names its own aggregate as its source projection; remove the source projection to make it an owned key",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// The source is neither an aggregate projection nor an aggregate with an owned [NaturalKey], so
    /// there is no lookup to resolve the mapped key through.
    /// </summary>
    public static readonly DiagnosticDescriptor NaturalKeySourceHasNoKey = new(
        id: "JFXEVT013",
        title: "The source projection of a mapped natural key has no natural key",
        messageFormat:
            "Natural key '{0}.{1}' is mapped to '{2}', which {3}",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Mapped keys resolve through exactly one lookup table, the one the source owns, so a source whose
    /// key of that type is itself mapped cannot serve it.
    /// </summary>
    public static readonly DiagnosticDescriptor NaturalKeySourceIsMapped = new(
        id: "JFXEVT014",
        title: "Mapped natural keys cannot be chained",
        messageFormat:
            "Natural key '{0}.{1}' is mapped to '{2}', whose '{3}' key is itself mapped; map it to the projection that owns the key instead",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NaturalKeySourceTypeMismatch = new(
        id: "JFXEVT015",
        title: "A mapped natural key must have the type of its source's natural key",
        messageFormat:
            "Natural key '{0}.{1}' has type '{2}', but its source '{3}' owns natural key '{4}' of type '{5}'",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ── Wolverine's [WriteAggregate<TKey>] / [ReadAggregate<TKey>] / [WriteModel<TKey>] / [ReadModel<TKey>]
    //    (JFXEVT016-017). Matched by name, so this analyzer needs no reference to Wolverine. The named key
    //    type has to be one the aggregate declares, which is all attributes the checks above already read;
    //    Wolverine checks the same at codegen against the registered projections. ──

    /// <summary>
    ///     A handler parameter names a natural key type its aggregate does not have.
    /// </summary>
    public static readonly DiagnosticDescriptor ModelNaturalKeyNotFound = new(
        id: "JFXEVT016",
        title: "The natural key type named for a model must be one of the model's natural keys",
        messageFormat: "Parameter '{0}' names natural key type '{1}', but '{2}' {3}",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    ///     A handler parameter names <c>TKey?</c> as the natural key type. The key is <c>TKey</c>; a <c>TKey?</c>
    ///     member is still matched to it.
    /// </summary>
    public static readonly DiagnosticDescriptor ModelNaturalKeyIsNullable = new(
        id: "JFXEVT017",
        title: "Name a natural key by its non-nullable type",
        messageFormat:
            "Parameter '{0}' names natural key type '{1}'; name '{2}' instead, which a '{1}' member is still matched to",
        category: "JasperFx.Events",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
