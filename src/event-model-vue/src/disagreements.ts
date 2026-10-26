import type {
  EventModelClaim,
  EventModelElement,
  EventModelElementKind,
  EventModelRole,
  EventModelSliceDescriptor,
  HotspotDescriptor
} from './types'

/**
 * How a `SourceDisagreement` hotspot is drawn (jasperfx#1084).
 *
 * - `badge` (the default): no sticky of its own. A small muted "≠" sits on the card that renders the
 *   disputed role, or on the slice header when no card does, and both claims are on its tooltip.
 * - `sticky`: the pre-1084 rendering, a hotspot card beside the slice's other cards.
 *
 * Only disagreements have a choice. The other hotspot kinds (`PendingSpecification`, `PatternViolation`,
 * `ModelCollapse`, `SliceCollision`, `UnresolvedSpecification`) are findings and always stay stickies.
 */
export type DisagreementPresentation = 'badge' | 'sticky'

/**
 * The card kinds that render each role. A role missing from this map has no card of its own
 * (`Origin`, `TriggerKind`, `Pattern`, `Domain`, …), so its badge goes on the slice header.
 */
const KINDS_BY_ROLE: Partial<Record<EventModelRole, readonly EventModelElementKind[]>> = {
  TriggerLabel: ['Trigger'],
  TriggerType: ['Trigger'],
  TriggerOrigin: ['Trigger'],
  CommandType: ['Command'],
  HandlerType: ['Handler'],
  AggregateTypes: ['Aggregate'],
  StartsStream: ['Aggregate'],
  EmittedEvents: ['Event'],
  ConsumedEvents: ['Event'],
  PublishedMessages: ['Message'],
  ProjectionTypes: ['Projection'],
  ViewProjection: ['Projection'],
  ReadModelTypes: ['ReadModel'],
  ReadsFrom: ['ReadModel'],
  ExternalSystems: ['ExternalSystem']
}

/** Where a slice's disagreements land: on cards, by element id, and on the header. */
export interface DisagreementPlacement {
  byElementId: Map<string, HotspotDescriptor[]>
  header: HotspotDescriptor[]
}

/** The slice's `SourceDisagreement` hotspots, in descriptor order. */
export function disagreementsOf(slice: EventModelSliceDescriptor): HotspotDescriptor[] {
  return (slice.hotspots ?? []).filter((h) => h.origin === 'SourceDisagreement')
}

/**
 * Whether an element is the sticky a `SourceDisagreement` was projected into. The producer makes the
 * hotspot's text the element's label (jasperfx#704), so the label is the only join there is.
 */
export function isDisagreementElement(slice: EventModelSliceDescriptor, element: EventModelElement): boolean {
  if (element.kind !== 'Hotspot') return false
  return disagreementsOf(slice).some((h) => h.text === element.label)
}

/** The element ids of a slice's disagreement stickies, which the badge presentation does not lay out. */
export function disagreementElementIds(slice: EventModelSliceDescriptor): Set<string> {
  const texts = new Set(disagreementsOf(slice).map((h) => h.text))
  return new Set(
    (slice.elements ?? []).filter((e) => e.kind === 'Hotspot' && texts.has(e.label)).map((e) => e.id)
  )
}

/**
 * The names a claim mentions. A list role's claim is its short type names joined with ", " (the
 * C# merge's rendering), so split it back.
 */
function namesIn(claim: EventModelClaim | null | undefined): string[] {
  return (claim?.value ?? '')
    .split(',')
    .map((x) => x.trim())
    .filter((x) => x.length > 0)
}

function mentions(element: EventModelElement, names: ReadonlySet<string>): boolean {
  return names.has(element.label) || (!!element.type?.name && names.has(element.type.name))
}

/**
 * Decide which card each of a slice's disagreements marks.
 *
 * The role picks the card kind. When the role has several cards of that kind (the events of an
 * `EmittedEvents` disagreement), the badge goes on the cards either claim names, so the ones the
 * sources agree about stay clean; if neither claim names a card here, every card of the kind is
 * marked rather than none. A role with no card, or whose cards are not on the slice, marks the header.
 */
export function placeDisagreements(slice: EventModelSliceDescriptor): DisagreementPlacement {
  const byElementId = new Map<string, HotspotDescriptor[]>()
  const header: HotspotDescriptor[] = []
  const elements = slice.elements ?? []

  for (const hotspot of disagreementsOf(slice)) {
    const kinds = hotspot.role ? KINDS_BY_ROLE[hotspot.role] : undefined
    const candidates = kinds ? elements.filter((e) => kinds.includes(e.kind)) : []

    if (candidates.length === 0) {
      header.push(hotspot)
      continue
    }

    const names = new Set([...namesIn(hotspot.winningClaim), ...namesIn(hotspot.losingClaim)])
    const named = candidates.filter((e) => mentions(e, names))

    for (const element of named.length > 0 ? named : candidates) {
      const list = byElementId.get(element.id)
      if (list) list.push(hotspot)
      else byElementId.set(element.id, [hotspot])
    }
  }

  return { byElementId, header }
}

function describeClaim(claim: EventModelClaim): string {
  return claim.source ? `${claim.provenance} (${claim.source})` : claim.provenance
}

/**
 * One line per disagreement for a badge's tooltip or a host's selection detail: the role, then both
 * claims with their rung and (when the source named itself) the source, kept first. A disagreement
 * whose claims did not survive the wire falls back to its text.
 */
export function disagreementLines(hotspots: readonly HotspotDescriptor[]): string[] {
  return hotspots.map((h) => {
    if (!h.winningClaim || !h.losingClaim) return h.text
    const role = h.role ? `${h.role}: ` : ''
    return (
      `${role}kept ${describeClaim(h.winningClaim)} ${h.winningClaim.value}; ` +
      `dropped ${describeClaim(h.losingClaim)} ${h.losingClaim.value}`
    )
  })
}
