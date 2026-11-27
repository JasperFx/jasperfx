import type {
  BoundaryReadKind,
  EventModelDescriptor,
  EventModelElement,
  EventModelSliceDescriptor,
  TypeDescriptor
} from './types'

/**
 * The consistency boundary of one command slice, as the dcb.events per-command table lays it out
 * (jasperfx#1297): what the command reads, the event types each read folds, the tag each read is by,
 * and the query that results.
 *
 * Pure and exported for the same reason `streamRowPlan` is: this is a claim about the MODEL, so a
 * host's drill-down and a test should ask the same function the canvas asks. The slice's own
 * `boundaryQuery` is computed upstream and carries the slice's half — the events it appends under
 * each tag; the events a read model FOLDS live on the model, in `aggregates[].appliedEvents` and the
 * producing View slice's consumed events, which is what this resolves.
 */
export interface ResolvedBoundaryRead {
  kind: BoundaryReadKind
  type: TypeDescriptor
  tag: string
  /** The command member supplying the tag value, or null when bound by convention. */
  member: string | null
  /** The event types the read model folds, as the model knows them. Empty when it cannot say. */
  folded: TypeDescriptor[]
}

export interface ResolvedBoundaryTerm {
  tag: string
  member: string | null
  /** Every event type the term selects: the slice's own tagged events, then what its reads fold. */
  eventTypes: TypeDescriptor[]
}

export interface ConsistencyBoundary {
  reads: ResolvedBoundaryRead[]
  query: ResolvedBoundaryTerm[]
  /** Projections the slice declares to be outside the boundary. */
  untagged: TypeDescriptor[]
  /** The ways the command can refuse. */
  refusals: string[]
}

/** Wire identity of a type: full name when sent, else the short name — the same rule `layout.ts` keys on. */
function typeKey(type: TypeDescriptor | null | undefined): string | null {
  return type?.fullName ?? type?.name ?? null
}

/**
 * Whether two descriptors name one type, by the merge's own rule (jasperfx#798): full names when both
 * carry an assembly, short names when either is a declaration.
 */
function sameType(a: TypeDescriptor | null | undefined, b: TypeDescriptor | null | undefined): boolean {
  if (!a || !b) return false
  if (!a.assemblyName || !b.assemblyName) return a.name === b.name
  return (a.fullName ?? a.name) === (b.fullName ?? b.name)
}

/** A slice sits inside a Dynamic Consistency Boundary when it reads anything by tag (jasperfx#1297). */
export function isDcbSlice(slice: EventModelSliceDescriptor | null | undefined): boolean {
  return (slice?.reads ?? []).length > 0
}

/**
 * The tags an event card carries, from its slice's `eventTags` (jasperfx#1297). Matched by type identity,
 * falling back to the label for a producer that omits `type` on the element.
 */
export function tagsOf(slice: EventModelSliceDescriptor, element: EventModelElement): string[] {
  if (element.kind !== 'Event') return []
  for (const entry of slice.eventTags ?? []) {
    if (element.type ? sameType(entry.event, element.type) : entry.event.name === element.label) {
      return entry.tags ?? []
    }
  }
  return []
}

function addType(list: TypeDescriptor[], type: TypeDescriptor) {
  if (!list.some((x) => sameType(x, type))) list.push(type)
}

/** The events the thing a read reads is folded from, as the model knows them. */
function foldedEventsOf(
  descriptor: EventModelDescriptor | null | undefined,
  read: { type: TypeDescriptor }
): TypeDescriptor[] {
  const found: TypeDescriptor[] = []

  for (const aggregate of descriptor?.aggregates ?? []) {
    if (!sameType(aggregate.type, read.type)) continue
    for (const event of aggregate.appliedEvents ?? []) addType(found, event)
  }

  for (const slice of descriptor?.slices ?? []) {
    const produces =
      (slice.elements ?? []).some(
        (e) => (e.kind === 'ReadModel' || e.kind === 'Projection') && e.type && sameType(e.type, read.type)
      )
    if (!produces) continue
    for (const element of slice.elements ?? []) {
      // The View slice repeats the events it consumes as its own Event cards, which is what links are
      // drawn to — so its event cards are exactly what it folds.
      if (element.kind === 'Event' && element.type) addType(found, element.type)
    }
  }

  return found
}

/**
 * The consistency boundary of `slice`, resolved against `descriptor` (jasperfx#1297). Null when the
 * slice reads nothing by tag.
 */
export function consistencyBoundaryFor(
  descriptor: EventModelDescriptor | null | undefined,
  slice: EventModelSliceDescriptor
): ConsistencyBoundary | null {
  if (!isDcbSlice(slice)) return null

  const reads: ResolvedBoundaryRead[] = (slice.reads ?? []).map((read) => ({
    kind: read.kind,
    type: read.type,
    tag: read.tag,
    member: read.member ?? null,
    folded: foldedEventsOf(descriptor, read)
  }))

  // One term per tag, in declaration order. The upstream `boundaryQuery` carries the slice's own tagged
  // events; recomputed here from `eventTags` so a producer that omitted it still gets a table.
  const query: ResolvedBoundaryTerm[] = []
  for (const read of reads) {
    if (query.some((t) => t.tag === read.tag)) continue

    const eventTypes: TypeDescriptor[] = []
    for (const entry of slice.eventTags ?? []) {
      if ((entry.tags ?? []).includes(read.tag)) addType(eventTypes, entry.event)
    }
    for (const term of slice.boundaryQuery?.terms ?? []) {
      if (term.tag === read.tag) for (const type of term.eventTypes ?? []) addType(eventTypes, type)
    }
    for (const other of reads) {
      if (other.tag === read.tag) for (const type of other.folded) addType(eventTypes, type)
    }

    query.push({ tag: read.tag, member: read.member, eventTypes })
  }

  return {
    reads,
    query,
    untagged: slice.untaggedProjections ?? [],
    refusals: slice.rejectionReasons ?? []
  }
}

/** The dcb.events rendering of one query term: `{ types: [A, B], tags: [courseId:<CourseId>] }`. */
export function queryTermText(term: ResolvedBoundaryTerm): string {
  const types = term.eventTypes.map((t) => t.name).join(', ')
  const value = term.member ? `command.${term.member}` : '…'
  return `{ types: [${types}], tags: [${term.tag}:${value}] }`
}

/** Exposed for tests: the identity rule the boundary joins on. */
export const _boundaryInternals = { typeKey, sameType }
