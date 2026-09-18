import type { TriggerKind } from './types'

/**
 * Trigger-kind glyphs (bobcat#184).
 *
 * A slice's `triggerKind` is on every derived slice and was rendered nowhere: "an HTTP endpoint"
 * and "a message handler" are different enough kinds of behaviour that a reader scanning a canvas
 * of 36 slices should not have to read prose to tell them apart.
 *
 * Inline path data on a 16×16 box rather than an icon dependency: this package ships to two
 * consoles with different icon sets (Element Plus here, its own there), and a shared component
 * that drags in a third would be the one thing neither host wants. Stroked in `currentColor`, so
 * the glyph inherits the host's ink like every other line on the canvas.
 */
export const TRIGGER_ICON: Record<TriggerKind, string> = {
  // A globe: the request came from outside over the network.
  Http: 'M8 1.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 0 0 0-13M1.5 8h13M8 1.5c1.8 1.7 2.7 4 2.7 6.5S9.8 12.8 8 14.5C6.2 12.8 5.3 10.5 5.3 8S6.2 3.2 8 1.5',
  // Two arrows passing: a call and its response, over a channel of its own.
  Grpc: 'M2 5.5h9M8.5 3 11 5.5 8.5 8M14 10.5H5M7.5 8 5 10.5 7.5 13',
  // An envelope: something was sent, and this slice happens to be who reads it.
  MessageHandler: 'M1.5 3.5h13v9h-13zM1.5 4l6.5 5 6.5-5',
  // A clock: nobody asked; the time did.
  JobScheduler: 'M8 1.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 0 0 0-13M8 4.5V8l2.5 2',
  // A person: a human act is the trigger, which is what an overlay's label usually describes.
  Human: 'M8 2.5a2.6 2.6 0 1 0 0 5.2a2.6 2.6 0 0 0 0-5.2M2.8 14c0-2.9 2.3-4.6 5.2-4.6s5.2 1.7 5.2 4.6',
  // A box with an outbound arrow: another system reached in.
  External: 'M9 2.5h4.5V7M13.5 2.5 8 8M12 9.5v4h-9.5V4H7'
}

/** Human wording for a trigger kind, for the icon's tooltip. */
export const TRIGGER_KIND_LABEL: Record<TriggerKind, string> = {
  Http: 'HTTP endpoint',
  Grpc: 'gRPC call',
  MessageHandler: 'Message handler',
  JobScheduler: 'Scheduled job',
  Human: 'Human action',
  External: 'External system'
}

/** HTTP methods a route label may lead with. */
const METHODS = new Set([
  'GET',
  'PUT',
  'POST',
  'HEAD',
  'PATCH',
  'TRACE',
  'DELETE',
  'CONNECT',
  'OPTIONS'
])

/**
 * Split `POST /api/accounts/{id}/withdrawals` into its method and path, or return null for a label
 * that is not a route.
 *
 * Routes are the worst offenders for card width (bobcat#184, and the half of bobcat#180 that
 * widening alone does not solve): the verb is three to seven characters of fixed vocabulary that
 * a reader recognises by shape, so it belongs in a badge rather than in the sentence. Matched
 * rather than read off `triggerOrigin` because the label is what the card actually renders — an
 * older producer, or one whose overlay never named the slice, puts the route there too.
 */
export function parseRoute(label: string): { method: string; path: string } | null {
  const match = /^([A-Za-z]+)\s+(\/\S*)$/.exec(label.trim())
  if (!match) return null
  const method = match[1].toUpperCase()
  return METHODS.has(method) ? { method, path: match[2] } : null
}
