/**
 * TypeScript mirror of the `JasperFx.Events.EventModeling` wire descriptor (JasperFx.Events
 * 2.56.0, jasperfx#687 / #689 / #703 / #704).
 *
 * These are hand-written rather than generated, unlike the Bobcat console's monitor-event
 * mirrors: the descriptor is JasperFx's contract, not Bobcat's, so there is no local C# source
 * for a generator to read. `types.spec.ts` pins the enum members against the published shape so
 * a rename upstream fails here rather than rendering a blank lane.
 *
 * The elements/edges pair is deliberately the whole rendering contract — jasperfx#687 decision 9
 * calls it "the rendering contract every viewer can draw from without a second transform", which
 * is exactly why this package renders a descriptor and not any producer's private model.
 */

/** Kind of a rendered element. Maps 1:1 to a canonical Event Storming sticky-note colour. */
export type EventModelElementKind =
  | 'Trigger'
  | 'Command'
  | 'Handler'
  | 'Aggregate'
  | 'Event'
  | 'Message'
  | 'Projection'
  | 'ReadModel'
  | 'ExternalSystem'
  | 'Hotspot'
  /**
   * A named screen a human trigger acts through — the wireframe card by name only, no content
   * (jasperfx#1298). White like a trigger, drawn framed. Absent from any producer below the
   * JasperFx.Events release that added it.
   */
  | 'Screen'

/** The four lanes of a standard Event Modeling canvas, top to bottom. */
export type EventModelLane = 'Wireframe' | 'Command' | 'EventStream' | 'ReadModel'

/**
 * The four canonical Event Modeling slice patterns, plus `Query` (jasperfx#1003).
 *
 * `Query` extends classic Event Modeling: a read with no state change — trigger → read of a read
 * model, aggregate or document → response. Classic Event Modeling folds the read into the View
 * slice; the Critter Stack draws the endpoint that reads as its own slice, linked to the View that
 * produces what it reads by a `ReadModelRead` link. Absent from any producer below the JasperFx.Events
 * release that added it.
 */
export type SlicePattern = 'Command' | 'View' | 'Automation' | 'Translation' | 'Query'

/**
 * What starts a slice. Finer-grained than textbook Event Modeling's human/external split.
 *
 * `Human` answers WHO triggers a slice; every other member answers HOW the code receives it. A
 * declared `Human` merged with a derived `Http`, `Grpc` or `MessageHandler` is not a disagreement:
 * the slice keeps the derived kind on `triggerKind` and the `Human` on `triggeredBy`.
 */
export type TriggerKind =
  | 'Http'
  | 'Grpc'
  | 'MessageHandler'
  | 'JobScheduler'
  | 'Human'
  | 'External'

/**
 * Where a hotspot came from.
 *
 * `SourceDisagreement` is jasperfx#704: two sources describing the same slice made different
 * claims about the same role and the merge had to drop one. It is emitted by the merge, never by
 * a source, and only when a claim is actually lost — two sources on one rung whose lists simply
 * union have not disagreed about anything.
 *
 * `ModelCollapse` (jasperfx#837) is several of a service's Event Models folded into one.
 *
 * `SliceCollision` (jasperfx#954) is one source describing two different slices under one name —
 * two different handlers. The merge kept the first and refused to fold the second into it; the two
 * handlers ride on `winningClaim` / `losingClaim`, as a disagreement's claims do.
 *
 * `PatternViolation` (jasperfx#1003) is a slice carrying a role its pattern rules out — a `Query`
 * that appends events, starts a stream or sends a command. Computed upstream from the merged roles
 * and carried on the slice's `patternViolations`, not its `hotspots`; `role` names the offending role.
 *
 * `UndeclaredRejection` (jasperfx#1297) is a specification refusing a command for a reason the slice's
 * `rejectionReasons` do not declare. `IncompleteInformation` (jasperfx#1299) is a field of a view or an
 * emitted event that comes from nowhere the model can see; graded as a question like `Prose`, computed
 * upstream on the model's `incompleteInformation` with `sliceName` and `field` saying which.
 */
export type HotspotOrigin =
  | 'PendingSpecification'
  | 'Prose'
  | 'SourceDisagreement'
  | 'ModelCollapse'
  | 'SliceCollision'
  | 'UnresolvedSpecification'
  | 'PatternViolation'
  | 'UndeclaredRejection'
  | 'IncompleteInformation'

/**
 * How much authority a source's claim carries — the ladder that decides precedence
 * when several sources describe the same slice (jasperfx#703). Higher rung wins, per claimed
 * role rather than wholesale. `Specified` (jasperfx#995) ranks between `Declared` and `Derived`.
 *
 * ⚠️ Not the same axis as CritterWatch's `LifecycleProvenance`, despite the overlapping member
 * name. That one is a RECONCILIATION of one edge across static and runtime discovery, where
 * `Confirmed` means "both sources agree" — not "seen in production". This is a ladder of
 * AUTHORITY. `Declared` and `Derived` both map onto its `Inferred`, `Observed` onto its
 * `Observed`, and its `Confirmed` has no rung here: agreement between rungs is expressed by the
 * ABSENCE of a `SourceDisagreement` hotspot rather than by a fourth value.
 */
export type EventModelProvenance = 'Declared' | 'Specified' | 'Derived' | 'Observed'

/**
 * The slice members provenance is tracked against, so precedence is decided per claimed role.
 * Carried here only because a `SourceDisagreement` hotspot names one.
 */
export type EventModelRole =
  | 'TriggerLabel'
  | 'TriggerType'
  | 'TriggerKind'
  | 'TriggerOrigin'
  | 'Pattern'
  | 'CommandType'
  | 'HandlerType'
  | 'AggregateTypes'
  | 'EmittedEvents'
  | 'PublishedMessages'
  | 'ProjectionTypes'
  | 'ReadModelTypes'
  | 'ExternalSystems'
  | 'Hotspots'
  | 'Specifications'
  | 'Domain'
  | 'ConsumedEvents'
  | 'ReadsFrom'
  | 'Chapter'
  | 'Origin'
  | 'StartsStream'
  | 'AggregateDeclaration'
  | 'DeciderModel'
  | 'ViewProjection'
  | 'AggregateIdentity'
  | 'TriggeredBy'
  // jasperfx#1297
  | 'EventTags'
  | 'Reads'
  | 'UntaggedProjections'
  | 'RejectionReasons'
  | 'TagType'
  // jasperfx#1298
  | 'Actor'
  | 'ExternalEvents'
  | 'Schedule'
  | 'Screen'
  | 'Description'
  // jasperfx#1299
  | 'Fields'

/** One source's claim about one role, as it stood before a merge resolved the disagreement. */
export interface EventModelClaim {
  provenance: EventModelProvenance
  /** Display rendering of what was claimed — short type names for the typed roles. */
  value: string
  /**
   * The contributing source, when it named itself with an origin (jasperfx#859), such as
   * `critterwatch://observed/TripService`. Absent for an unattributed source and on the `Origin` role,
   * whose value already is the source.
   */
  source?: string | null
}

/** What a domain policy covers (jasperfx#960); a string under the wire's string-enum converter. */
export type DomainAssignmentScope = 'Assembly' | 'Namespace' | 'Type'

/** A declared policy putting handlers and endpoints in a domain — a module (jasperfx#960). */
export interface DomainAssignmentDescriptor {
  domain: string
  scope: DomainAssignmentScope
  target: string
}

/** Which way messages flow between the model and an external system. */
export type ExternalSystemDirection = 'Inbound' | 'Outbound'

/** A CLR type identity. Only the members this package renders or keys on. */
export interface TypeDescriptor {
  name: string
  /** Optional on the wire — synthesize from `name` rather than assuming it. */
  fullName?: string
  assemblyName?: string
}

/**
 * Structured detail of a slice's trigger: HTTP verb + route (+ route name), gRPC service + method, the
 * projection raising a side effect, or a display label. Only the fields for the trigger's kind are set.
 *
 * The wire has always carried this as an object; earlier releases of this package typed it as a
 * string, which is why `triggerOrigin` still accepts one.
 */
export interface PublisherOrigin {
  httpRoute?: string | null
  httpMethod?: string | null
  /**
   * The HTTP endpoint's route name, when it has one (e.g. `GetOrder`), as ASP.NET Core link generation
   * and Wolverine's `HttpChain.RouteName` call it. JasperFx 2.85.0+ (jasperfx#1007).
   */
  httpRouteName?: string | null
  grpcService?: string | null
  grpcMethod?: string | null
  projectionType?: TypeDescriptor | null
  /** A pre-rendered display string such as `POST /orders`, or a cron expression. */
  label?: string | null
}

/** One rendered element of a slice. */
export interface EventModelElement {
  id: string
  kind: EventModelElementKind
  lane: EventModelLane
  label: string
  type?: TypeDescriptor | null
  /**
   * Which rung of the ladder claimed the role this element renders (jasperfx#703), stamped by the
   * producer from the owning slice so a viewer never re-derives it.
   *
   * Absent on a descriptor from JasperFx.Events < 2.56 and on the element kinds that have no role
   * of their own. Note that an *unattributed* model reads `Declared` rather than absent,
   * deliberately: that is the rung the merge actually treats it as.
   */
  provenance?: EventModelProvenance | null
  /**
   * Plain text or markdown describing the element, stamped by the producer from the slice's
   * `elementDescriptions` (jasperfx#1298). Shown on hover. Absent when nobody described it.
   */
  description?: string | null
}

/** A directed relationship between two elements of a slice, by element id. */
export interface EventModelEdge {
  // Edge endpoints are fromId/toId (element ids) — deliberately NOT source/target.
  fromId: string
  toId: string
}

/** A specification bound to a slice — Bobcat stamps `{Feature}/{Scenario}` into `identity`. */
export interface SpecificationDescriptor {
  identity: string
  feature?: string | null
  scenario?: string | null
  resolvedTypes?: TypeDescriptor[]
}

/** An open question, a conflict, or (primarily) a specification that is still pending. */
export interface HotspotDescriptor {
  origin: HotspotOrigin
  text: string
  specificationIdentity?: string | null
  /** The role two sources disagreed about. Set only when `origin` is `SourceDisagreement`. */
  role?: EventModelRole | null
  /** The claim the merge kept. Set only when `origin` is `SourceDisagreement`. */
  winningClaim?: EventModelClaim | null
  /**
   * The claim the merge dropped. Set only when `origin` is `SourceDisagreement`.
   *
   * A pair rather than a list because merges are pairwise: three sources disagreeing about one
   * role produce two hotspots, each naming the two claims that actually met.
   */
  losingClaim?: EventModelClaim | null
  /**
   * The slice a model-level computed hotspot is about. Set only when `origin` is
   * `IncompleteInformation` (jasperfx#1299), which lives on the model's `incompleteInformation`
   * rather than on a slice.
   */
  sliceName?: string | null
  /** The field an `IncompleteInformation` hotspot is about, as `{Type}.{Field}` (jasperfx#1299). */
  field?: string | null
}

/** An external system on one end of an integration edge. */
export interface ExternalSystemDescriptor {
  name: string
  direction: ExternalSystemDirection
  endpointUri?: string | null
}

/** The stream lane of one event a slice emits (jasperfx#1203). */
export interface EmittedEventStream {
  event: TypeDescriptor
  aggregate: TypeDescriptor
}

/** The consistency tags one emitted event carries (jasperfx#1297). */
export interface EmittedEventTags {
  event: TypeDescriptor
  /** Tag names, as the model's `tags` catalogue names them. */
  tags: string[]
}

/** What kind of thing a command slice reads by tag (jasperfx#1297). */
export type BoundaryReadKind = 'Model' | 'Projection'

/**
 * One declared read of a command slice: a model or a projection, selected by one tag whose value a
 * command member supplies (jasperfx#1297). Reads are declared; the boundary is derived from them.
 */
export interface BoundaryReadDescriptor {
  kind: BoundaryReadKind
  type: TypeDescriptor
  tag: string
  /** The command member supplying the tag value, or absent when bound by convention. */
  member?: string | null
}

/** One term of the derived boundary query: the events carrying `tag` (jasperfx#1297). */
export interface BoundaryQueryTerm {
  tag: string
  member?: string | null
  /**
   * The emitted events of the slice that carry the tag. The events a read model FOLDS are a model-level
   * answer; `consistencyBoundaryFor` in `boundary.ts` adds them from `aggregates` and the producing View slice.
   */
  eventTypes?: TypeDescriptor[]
}

/** The consistency boundary a command slice's reads imply, computed upstream (jasperfx#1297). */
export interface BoundaryQueryDescriptor {
  terms?: BoundaryQueryTerm[]
  isEmpty?: boolean
}

/** One consistency tag of the model's catalogue (jasperfx#1297). */
export interface EventTagDescriptor {
  name: string
  /** The strong-typed tag type, absent before one exists. */
  type?: TypeDescriptor | null
  /** The primitive inside the tag type, when known. */
  innerType?: TypeDescriptor | null
  provenance?: EventModelProvenance | null
}

/** Who triggers a slice: an actor by name, with the roles the slice requires (jasperfx#1298). */
export interface ActorDescriptor {
  name: string
  requiredRoles?: string[]
}

/** A consumed event that comes from an external system (jasperfx#1298). */
export interface ExternalEventDescriptor {
  event: TypeDescriptor
  externalSystem: string
}

/** How an automation's schedule is expressed (jasperfx#1298). */
export type ScheduleKind = 'Interval' | 'Cron' | 'Named'

/** The schedule firing an automation, beside `JobScheduler` (jasperfx#1298). One of the three is set. */
export interface ScheduleDescriptor {
  kind: ScheduleKind
  /** A .NET TimeSpan as STJ writes it: `00:05:00`, `7.00:00:00`. */
  every?: string | null
  cron?: string | null
  name?: string | null
}

/** Plain text or markdown describing one element a slice references (jasperfx#1298). */
export interface ElementDescription {
  type: TypeDescriptor
  text: string
}

/** How many values a field holds (jasperfx#1299). */
export type FieldCardinality = 'One' | 'Many'

/** Where a declared field's value comes from — the board's `mapping` (jasperfx#1299). */
export interface FieldMappingDescriptor {
  source: TypeDescriptor
  field: string
}

/** One field of a modelled type (jasperfx#1299). */
export interface FieldDescriptor {
  name: string
  /** The field's type — the element type when `cardinality` is `Many`. */
  type: TypeDescriptor
  cardinality?: FieldCardinality
  optional?: boolean
  identity?: boolean
  tag?: string | null
  generated?: boolean
  pii?: boolean
  mapping?: FieldMappingDescriptor | null
  description?: string | null
}

/** The fields of one modelled type, on the model's `shapes` (jasperfx#1299). */
export interface TypeShapeDescriptor {
  type: TypeDescriptor
  fields?: FieldDescriptor[]
  provenance?: EventModelProvenance | null
}

/** Wire descriptor for a single slice of an Event Model. */
export interface EventModelSliceDescriptor {
  name: string
  domain?: string | null
  /**
   * The chapter — the span of the timeline this slice belongs to (jasperfx#824, bobcat#298). The
   * grouping every Event Modeling tool zooms into; eventmodelers.ai draws it as a wide arrow over
   * several slices. Independent of `domain`: a bounded context has many chapters. Absent from any
   * producer below JasperFx.Events 2.69, and from every slice a producer never chaptered.
   */
  chapter?: string | null

  /**
   * The aggregate whose stream this slice STARTS, as against appends to (jasperfx#957). The aggregate is
   * also in `aggregateTypes`, so a viewer that ignores this still draws it. Absent from any producer
   * below the JasperFx.Events release that added it.
   */
  startsStream?: TypeDescriptor | null
  /**
   * Which aggregate's stream each emitted event goes to, for the events a declaration put in a stream
   * lane (jasperfx#1203). Absent or empty on code-derived slices and from any producer below the
   * JasperFx.Events release that added it.
   */
  emittedEventStreams?: EmittedEventStream[] | null
  /**
   * WHY the slice has the aggregate it has, when a declaration said (jasperfx#994): applied from the
   * definition's `ForAggregate` default, declared on the slice, deliberately none, or a DCB decider
   * model. Absent when nobody said, which is always the case for code-derived slices.
   */
  aggregateDeclaration?: AggregateDeclaration | null
  /**
   * The Dynamic Consistency Boundary decider model the slice decides through, rather than single-stream
   * aggregates (jasperfx#994). Recorded only; how its events are selected is not modelled yet.
   */
  deciderModel?: TypeDescriptor | null
  pattern?: SlicePattern | null
  /**
   * Aggregate-shaped types this slice writes through, in declaration order.
   *
   * The same types the producer projects into the slice's `Aggregate` elements, carried separately
   * because a *row* is a statement about the model rather than about a card: stream rows (#299) ask
   * which stream a slice is on, which is a question the elements answer only by accident of how
   * they were built. A producer below JasperFx.Events 2.60 omits it, so the layout falls back to
   * the `Aggregate` elements.
   */
  aggregateTypes?: TypeDescriptor[]
  /** On a merged slice, HOW the code receives the trigger. */
  triggerKind?: TriggerKind | null
  /**
   * WHO triggers the slice when that differs from how the code receives it: `Human` beside a derived
   * `Http`, `Grpc` or `MessageHandler`. Null otherwise; a slice that has met no code carries `Human`
   * on `triggerKind` instead. Absent below the JasperFx release that added it.
   */
  triggeredBy?: TriggerKind | null
  triggerLabel?: string | null
  /** An object on the wire; a plain string is accepted for hosts that pre-render it. */
  triggerOrigin?: PublisherOrigin | string | null
  /**
   * The user's handler or endpoint class (never Wolverine's generated `MessageHandler` / `HttpHandler`
   * subclass), so a host can navigate to the code.
   */
  handlerType?: TypeDescriptor | null
  /** The name of the user's handler or endpoint method on `handlerType`. */
  handlerMethod?: string | null
  /**
   * Read models the slice reads BEFORE deciding, or what a `Query` slice reads and returns
   * (jasperfx#824, jasperfx#1007). Each is a `ReadModel` element and the `To` end of a
   * `ReadModelRead` link.
   */
  readsFrom?: TypeDescriptor[]
  elements?: EventModelElement[]
  edges?: EventModelEdge[]
  specifications?: SpecificationDescriptor[]
  hotspots?: HotspotDescriptor[]
  /**
   * Roles the slice carries that its pattern rules out, as `PatternViolation` hotspots
   * (jasperfx#1003). Computed upstream on every read, like `elements`, and each one is also a
   * `Hotspot` element. Absent from any producer below the JasperFx.Events release that added it.
   */
  patternViolations?: HotspotDescriptor[]
  externalSystems?: ExternalSystemDescriptor[]

  // jasperfx#1297 — Dynamic Consistency Boundaries. All absent from any producer below the release that added them.
  /** The consistency tags each emitted event carries. */
  eventTags?: EmittedEventTags[] | null
  /** What the slice reads by tag before deciding. Non-empty means the slice sits inside a DCB. */
  reads?: BoundaryReadDescriptor[] | null
  /** The boundary the reads imply, computed upstream on every read. */
  boundaryQuery?: BoundaryQueryDescriptor | null
  /** Projections the boundary deliberately leaves out. */
  untaggedProjections?: TypeDescriptor[] | null
  /** The ways a command slice can refuse — "Course is full". */
  rejectionReasons?: string[] | null

  // jasperfx#1298 — structural elements.
  /** Who triggers the slice, with the roles it requires of them. */
  actor?: ActorDescriptor | null
  /** Which consumed events come from an external system, and which one. */
  externalEvents?: ExternalEventDescriptor[] | null
  /** The cadence of a scheduled automation. */
  schedule?: ScheduleDescriptor | null
  /** The name of the screen a human trigger acts through; also a `Screen` element in the wireframe lane. */
  screen?: string | null
  /** Plain text or markdown describing the slice. */
  description?: string | null
  /** Descriptions of the elements the slice references, by type. Each is also on its element's `description`. */
  elementDescriptions?: ElementDescription[] | null
}

/**
 * Which aggregate-marker attribute a type carries. String members on the wire, not ordinals.
 */
export type AggregateKind = 'WriteAggregate' | 'ReadAggregate' | 'ConsistentAggregate' | 'BoundaryModel'

/** Why a slice has its aggregate (jasperfx#994). */
export type AggregateDeclaration = 'Default' | 'Explicit' | 'None' | 'DeciderModel'

/**
 * One aggregate-shaped type of the model, with the events it applies.
 *
 * ⚠️ This corrects a mirror that was wrong rather than merely incomplete: `aggregates` was typed
 * here as `EventModelElement[]`, which is what a slice's Aggregate *cards* are, not what the model
 * document carries. Nothing in this package had ever read the member, so the error was invisible
 * until stream rows (#299) needed `appliedEvents` to decide which row an event belongs on.
 */
export interface AggregateDescriptor {
  type: TypeDescriptor
  kind?: AggregateKind
  /**
   * Event types the aggregate applies, in declaration order. Empty when the producer could not
   * resolve the apply set statically — which is a real case, so no layout decision may *require*
   * it (#299 falls back to the slice's first aggregate).
   */
  appliedEvents?: TypeDescriptor[]
  /**
   * The aggregate's identity type — its stream id, e.g. `OrderId`, `Guid` or `string` (jasperfx#1004).
   * Derived from the aggregate's single-stream projection, or declared with `IdentifiedBy<T>()`. A
   * disagreement between the two is a model-level `SourceDisagreement` with role `AggregateIdentity`.
   * Absent from any producer below the JasperFx.Events release that added it.
   */
  identityType?: TypeDescriptor | null
  /** The rung of the source that described this aggregate (jasperfx#1004). */
  provenance?: EventModelProvenance | null
}

/**
 * Kind of a cross-slice link (jasperfx#823). One member per join rule, so a viewer can draw a
 * consumed event differently from a triggering one without re-deriving why they differ.
 *
 * `EventConsumed` and `ReadModelRead` need the roles jasperfx#824 added (`consumedEvents`,
 * `readsFrom`). `ReadModelRead` is also the link from a View slice to a `Query` slice that reads the
 * read model it produces (jasperfx#1003).
 */
export type EventModelLinkKind =
  | 'EventTriggers'
  | 'MessageTriggers'
  | 'EventConsumed'
  | 'ReadModelRead'

/**
 * A cause→effect relationship between two SLICES — the one relationship the descriptor's
 * slice-local `elements`/`edges` cannot express (jasperfx#823).
 *
 * Computed upstream on read from the roles each slice already stamps, never accepted as input, so
 * no viewer derives its own opinion about what connects to what.
 *
 * ⚠️ Optional, and absent from every descriptor a producer on JasperFx.Events < 2.69 can emit —
 * which includes the version this repo pins today. Everything in this package that reads `links`
 * therefore has to work without them; focus (#296) degrades to the selected slice alone.
 *
 * Drawing them is bobcat#295's job. This type is here because focus needs to know a slice's
 * neighbours, which is a question about the model rather than about the picture.
 */
export interface EventModelLink {
  fromSlice: string
  fromElementId: string
  toSlice: string
  toElementId: string
  kind: EventModelLinkKind
  /** The type identity the join matched on. */
  via?: TypeDescriptor | null
}

/** Wire descriptor for an entire Event Model. */
export interface EventModelDescriptor {
  name: string
  slices?: EventModelSliceDescriptor[]
  /** The model's aggregate-shaped types, in first-appearance order. Slices point at these by type. */
  aggregates?: AggregateDescriptor[]
  /**
   * Declared policies putting handlers and endpoints in domains — modules, in a modular monolith
   * (jasperfx#960). Consumed by the sources that derive slices; a viewer needs nothing from them.
   */
  domainAssignments?: DomainAssignmentDescriptor[]
  /** Cross-slice links, computed upstream. Absent on any producer below JasperFx.Events 2.69. */
  links?: EventModelLink[]
  /** The model's consistency-tag catalogue (jasperfx#1297). Slices refer to these by name. */
  tags?: EventTagDescriptor[]
  /** The fields of the model's types, one shape per type (jasperfx#1299). */
  shapes?: TypeShapeDescriptor[]
  /**
   * Fields whose value comes from nowhere the model can see, as `IncompleteInformation` hotspots
   * computed upstream (jasperfx#1299). Each names its `sliceName` and `field`; a viewer joins on those.
   */
  incompleteInformation?: HotspotDescriptor[]
}

/** Lanes in canonical top-to-bottom order. Rendering order is part of the contract. */
export const LANE_ORDER: readonly EventModelLane[] = [
  'Wireframe',
  'Command',
  'EventStream',
  'ReadModel'
] as const

/** Ladder rungs, lowest authority first. Order is part of the contract. */
export const PROVENANCE_ORDER: readonly EventModelProvenance[] = [
  'Declared',
  'Specified',
  'Derived',
  'Observed'
] as const

/** What each rung means, for a legend or a tooltip. */
export const PROVENANCE_LABEL: Record<EventModelProvenance, string> = {
  Declared: 'Declared — somebody wrote it down (a spec or the overlay)',
  Specified: 'Specified — read off the specifications, joined by command type (jasperfx#995)',
  Derived: 'Derived — read out of the code (Wolverine chains, the source generator)',
  Observed: 'Observed — seen happening in a running system'
}

/** Human-readable lane captions for the canvas gutter. */
export const LANE_LABEL: Record<EventModelLane, string> = {
  Wireframe: 'Wireframe / Trigger',
  Command: 'Command',
  EventStream: 'Event Stream',
  ReadModel: 'Read Model'
}
