import type { EventModelElementKind } from './types'

/**
 * The canonical Event Modeling / Event Storming sticky-note colours, mirroring
 * `JasperFx.Events.EventModeling.EventModelPalette.ColorFor`.
 *
 * Mirrored rather than fetched so the component can render a descriptor that arrived as a
 * plain JSON file with no server behind it. `palette.spec.ts` pins every value, because a
 * drifted colour is the kind of thing that looks fine in isolation and only shows up as
 * "CritterWatch and the console disagree" — the exact failure this package exists to prevent.
 */
export const EVENT_MODEL_PALETTE: Record<EventModelElementKind, string> = {
  Trigger: '#FFFFFF',
  Command: '#5B9BD5',
  Handler: '#5B9BD5',
  Aggregate: '#FFF2A8',
  Event: '#F5A623',
  Message: '#5B9BD5',
  Projection: '#7ED321',
  ReadModel: '#7ED321',
  ExternalSystem: '#F8BBD0',
  Hotspot: '#E91E63'
}

/** Hex colour for an element kind. Unknown kinds fall back to the upstream neutral. */
export function colorFor(kind: EventModelElementKind): string {
  return EVENT_MODEL_PALETTE[kind] ?? '#CCCCCC'
}

/**
 * Kinds the upstream palette documents as drawn with a treatment rather than a solid fill:
 * a handler and a projection are outlined, a (non-event) message is dashed.
 */
export const OUTLINED_KINDS: readonly EventModelElementKind[] = ['Handler', 'Projection'] as const
export const DASHED_KINDS: readonly EventModelElementKind[] = ['Message'] as const

/** Text colour that stays legible on a given sticky. White and pale yellow need dark ink. */
export function inkFor(kind: EventModelElementKind): string {
  return kind === 'Trigger' || kind === 'Aggregate' ? '#1F2933' : '#0B1F33'
}
