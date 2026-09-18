import { CANVAS_PADDING, GUTTER_GAP, GUTTER_WIDTH, canvasSize, type EventModelGraph } from './layout'
import type { EventModelDescriptor } from './types'

/**
 * Navigating a model rather than magnifying it (issue #296).
 *
 * #182 gave the canvas nine zoom stops and #194 gave it a filter bar, and a 106-slice model is
 * still ~10,000px wide at the 25% floor. What was missing was never magnification: it was being
 * able to say *look at this part*, and to get back out again. Every decision here is a pure
 * function over an already-laid-out {@link EventModelGraph}, for the same reason `filters.ts` is
 * pure — `layoutEventModel` never learns that a viewport exists, so "the same descriptor renders
 * identically in both viewers" stays a checkable claim.
 */

/**
 * What the reader is looking at. A `slice` focus is the common case; a `chapter` focus is the band
 * above it (#298) and a `domain` focus the grouping a chapterless model still has; all step out to
 * the whole model.
 *
 * The focus ladder is model → chapter → slice → bound spec (the drawer), with domain standing in
 * for chapter on a slice that declares none — the two are orthogonal (a bounded context has many
 * chapters), so a crumb trail never shows both: it shows the grouping the canvas actually draws as
 * a band above the slice.
 */
export interface FocusTarget {
  kind: 'slice' | 'domain' | 'chapter'
  name: string
}

/** A rectangle in *unscaled canvas* coordinates — the space `canvasSize` measures. */
export interface Rect {
  x: number
  y: number
  width: number
  height: number
}

/** Where the viewport is, and what it is looking at. The payload of `viewport-change`. */
export interface ViewportState {
  zoom: number
  /** Scroll offset in SCALED pixels, i.e. what `scrollLeft`/`scrollTop` actually hold. */
  x: number
  y: number
  focus?: FocusTarget | null
  /** Element id of the selected card, when the reader selected one. */
  selection?: string | null
}

/**
 * Level of detail, from the scale (issue #296).
 *
 * Thresholds rather than a continuous ramp, and expressed as an attribute the *stylesheet*
 * switches on rather than as `v-if` in the template: a canvas of 600 cards must not re-render
 * because a reader nudged the wheel, and CSS that hides a glyph costs nothing per card. It also
 * means both consoles change appearance at exactly the same scale by construction, which a
 * per-host implementation could not promise.
 */
export type LevelOfDetail = 'detail' | 'compact' | 'overview'

/** ≥ 0.7 is today's rendering; below 0.4 a label is not a label any more. */
export const LOD_COMPACT_BELOW = 0.7
export const LOD_OVERVIEW_BELOW = 0.4

export function lodFor(zoom: number): LevelOfDetail {
  if (zoom >= LOD_COMPACT_BELOW) return 'detail'
  if (zoom >= LOD_OVERVIEW_BELOW) return 'compact'
  return 'overview'
}

/**
 * The slices one link away from `sliceName`, in either direction, plus the slice itself.
 *
 * ⚠️ **Degrades on purpose.** `links` is computed upstream (jasperfx#823) and is absent from every
 * descriptor a producer below JasperFx.Events 2.69 can emit — which includes the version this repo
 * pins. Without them the neighbourhood is the slice alone, which is still a useful focus and is the
 * path this package can actually exercise today. Nothing here derives a link client-side: two
 * viewers inventing their own joins is exactly what the upstream computation exists to prevent.
 */
export function neighbourhoodOf(
  descriptor: EventModelDescriptor | null | undefined,
  sliceName: string
): Set<string> {
  const names = new Set<string>([sliceName])
  for (const link of descriptor?.links ?? []) {
    if (link.fromSlice === sliceName) names.add(link.toSlice)
    else if (link.toSlice === sliceName) names.add(link.fromSlice)
  }
  return names
}

/** Every slice in a domain. A domain nobody declares is an empty focus, never the whole model. */
export function slicesInDomain(
  descriptor: EventModelDescriptor | null | undefined,
  domain: string
): Set<string> {
  const names = new Set<string>()
  for (const slice of descriptor?.slices ?? []) {
    if (slice.domain === domain) names.add(slice.name)
  }
  return names
}

/** Every slice in a chapter (#298). A chapter nobody declares is an empty focus, never the whole model. */
export function slicesInChapter(
  descriptor: EventModelDescriptor | null | undefined,
  chapter: string
): Set<string> {
  const names = new Set<string>()
  for (const slice of descriptor?.slices ?? []) {
    if (slice.chapter === chapter) names.add(slice.name)
  }
  return names
}

/**
 * The slice names a focus target resolves to — its neighbourhood for a slice, its band for a
 * chapter or a domain.
 */
export function focusedSliceNames(
  descriptor: EventModelDescriptor | null | undefined,
  target: FocusTarget | null | undefined
): Set<string> {
  if (!target) return new Set<string>()
  if (target.kind === 'chapter') return slicesInChapter(descriptor, target.name)
  return target.kind === 'domain'
    ? slicesInDomain(descriptor, target.name)
    : neighbourhoodOf(descriptor, target.name)
}

/**
 * What the reader has selected — the thing *Focus* acts on.
 *
 * Selection is separate from focus on purpose: the issue's sentence is "select a slice, a domain
 * band, or a card → Focus", and collapsing the two would mean every click on a card moved the
 * viewport. A click should not be able to lose someone's place.
 */
export type Selection =
  | { kind: 'element'; id: string; sliceName: string }
  | { kind: 'slice'; name: string }

/** Selection as the flat string the URL carries. */
export function selectionToKey(selection: Selection | null | undefined): string | null {
  if (!selection) return null
  return selection.kind === 'slice' ? `slice:${selection.name}` : `element:${selection.id}`
}

/**
 * Read a selection back, resolving an element key to the slice that owns it.
 *
 * Ownership is a lookup rather than a parse of the id. Element ids are `{slice}/{kind}/{type}` by
 * convention upstream, but a slice name containing a `/` — `GET /api/accounts` is one, and
 * Wolverine emits those — would make the parse wrong in exactly the case it mattered.
 */
export function selectionFromKey(
  descriptor: EventModelDescriptor | null | undefined,
  key: string | null | undefined
): Selection | null {
  if (!key) return null
  const separator = key.indexOf(':')
  if (separator <= 0) return null
  const kind = key.slice(0, separator)
  const name = key.slice(separator + 1)
  if (name.length === 0) return null

  if (kind === 'slice') {
    return (descriptor?.slices ?? []).some((s) => s.name === name) ? { kind: 'slice', name } : null
  }
  if (kind !== 'element') return null

  for (const slice of descriptor?.slices ?? []) {
    if ((slice.elements ?? []).some((e) => e.id === name)) {
      return { kind: 'element', id: name, sliceName: slice.name }
    }
  }
  return null
}

/** The slice a selection belongs to — what *Focus* turns into a neighbourhood. */
export function sliceOfSelection(selection: Selection | null | undefined): string | null {
  if (!selection) return null
  return selection.kind === 'slice' ? selection.name : selection.sliceName
}

/**
 * The canvas-space box around a set of slice columns.
 *
 * Full plot height rather than the occupied lanes: an Event Modeling slice IS its column, and
 * fitting only the two lanes that happen to carry cards would crop the lane the reader is about to
 * look for. Returns null when none of the names are on the canvas — a focus on a slice the filter
 * bar has hidden must leave the viewport alone rather than fit an empty rectangle.
 */
export function rectForSlices(graph: EventModelGraph, names: ReadonlySet<string>): Rect | null {
  let left = Number.POSITIVE_INFINITY
  let right = Number.NEGATIVE_INFINITY

  for (const slice of graph.slices) {
    if (!names.has(slice.name)) continue
    left = Math.min(left, slice.x)
    right = Math.max(right, slice.x + slice.width)
  }

  if (!Number.isFinite(left) || !Number.isFinite(right)) return null

  // Plot space → canvas space: the gutter and the padding sit to the left of every column.
  const plotOrigin = CANVAS_PADDING + GUTTER_WIDTH + GUTTER_GAP
  return {
    x: plotOrigin + left,
    y: CANVAS_PADDING,
    width: right - left,
    height: graph.height
  }
}

/**
 * Fit a rectangle into a viewport: `scale = clamp(min(vw/w, vh/h))`, then scroll to centre.
 *
 * The scroll offsets come back in *scaled* pixels because that is what a scroller holds, and are
 * clamped at zero — a neighbourhood narrower than the viewport centres inside it rather than
 * scrolling negative, which browsers silently ignore and tests do not.
 *
 * ⚠️ `viewport.height <= 0` means "the height is not constraining", and the fit is width-only.
 * That is not a guard against a missing measurement, it is a case that happens every time: the
 * canvas's scroller has no height of its own — it grows to whatever the scaled canvas needs — so
 * in the Bobcat console `clientHeight` is always ABOUT the canvas height and `vh/h` reduces to the
 * zoom the reader already had. Driving the real 106-slice model, focus "fitted" 46% to 48%. The
 * caller decides which case it is by asking whether the scroller actually scrolls vertically.
 */
export function fitToRect(
  rect: Rect,
  viewport: { width: number; height: number },
  bounds: { min: number; max: number }
): { zoom: number; x: number; y: number } {
  const padded = { width: Math.max(1, rect.width), height: Math.max(1, rect.height) }
  const horizontal = viewport.width / padded.width
  const raw =
    viewport.height > 0 ? Math.min(horizontal, viewport.height / padded.height) : horizontal
  // A neighbourhood of one small slice blown up to 200% looks like a mistake, the same argument
  // fit-to-width settled in #182 — so the ceiling is the honest 100%, not MAX_ZOOM.
  const zoom = Math.min(Math.max(raw, bounds.min), Math.min(bounds.max, 1))

  const centreX = (rect.x + rect.width / 2) * zoom
  const centreY = (rect.y + rect.height / 2) * zoom
  return {
    zoom,
    x: Math.max(0, centreX - viewport.width / 2),
    y: Math.max(0, centreY - viewport.height / 2)
  }
}

/**
 * The breadcrumb for a focus: `Banking › Onboarding › WithdrawFunds`.
 *
 * Each crumb is a step out — the model clears the focus, the middle rung focuses its band — so the
 * way back is the same control that says where you are. The middle rung is the slice's **chapter**
 * when it has one (#298: the grouping the canvas draws as a band above the slice), its domain
 * otherwise, and nothing when it has neither: an invented "(no chapter)" crumb would be a step
 * *into* a grouping the model never claimed.
 */
export interface Crumb {
  label: string
  target: FocusTarget | null
}

export function breadcrumbFor(
  descriptor: EventModelDescriptor | null | undefined,
  target: FocusTarget | null | undefined
): Crumb[] {
  if (!target) return []
  const crumbs: Crumb[] = [{ label: descriptor?.name ?? 'Model', target: null }]

  if (target.kind === 'domain' || target.kind === 'chapter') {
    crumbs.push({ label: target.name, target })
    return crumbs
  }

  const slice = (descriptor?.slices ?? []).find((s) => s.name === target.name)
  if (slice?.chapter) {
    crumbs.push({ label: slice.chapter, target: { kind: 'chapter', name: slice.chapter } })
  } else if (slice?.domain) {
    crumbs.push({ label: slice.domain, target: { kind: 'domain', name: slice.domain } })
  }
  crumbs.push({ label: target.name, target })
  return crumbs
}

/**
 * Viewport state as flat, URL-safe strings, and back.
 *
 * In the package rather than in each host's page for the reason every other cross-console decision
 * ended up here: a Bobcat link and a CritterWatch link to "CreditWallet, focused" should mean the
 * same thing. The host still owns *where* it puts them — Bobcat mirrors them into the route query
 * (#296), and a host that wants no URL state simply ignores the event.
 */
export function viewportToQuery(state: ViewportState): Record<string, string> {
  const query: Record<string, string> = {
    // Two decimals: a continuous wheel zoom otherwise writes 0.6173469387755102 into a URL a
    // human is expected to paste into a PR.
    z: state.zoom.toFixed(2),
    x: String(Math.round(state.x)),
    y: String(Math.round(state.y))
  }
  if (state.focus) query.focus = `${state.focus.kind}:${state.focus.name}`
  if (state.selection) query.sel = state.selection
  return query
}

/**
 * Read back what {@link viewportToQuery} wrote, from anything shaped like a query bag.
 *
 * Every field is independently optional and a junk value is dropped rather than defaulted: a URL
 * someone hand-edited should land on the model, not on a canvas scrolled to `NaN`.
 */
export function viewportFromQuery(
  query: Record<string, unknown> | null | undefined
): Partial<ViewportState> {
  const state: Partial<ViewportState> = {}
  if (!query) return state

  const num = (value: unknown): number | undefined => {
    const text = Array.isArray(value) ? value[0] : value
    if (typeof text !== 'string' && typeof text !== 'number') return undefined
    const parsed = Number(text)
    return Number.isFinite(parsed) ? parsed : undefined
  }
  const str = (value: unknown): string | undefined => {
    const text = Array.isArray(value) ? value[0] : value
    return typeof text === 'string' && text.length > 0 ? text : undefined
  }

  const zoom = num(query.z)
  if (zoom !== undefined && zoom > 0) state.zoom = zoom
  const x = num(query.x)
  if (x !== undefined) state.x = Math.max(0, x)
  const y = num(query.y)
  if (y !== undefined) state.y = Math.max(0, y)

  const focus = str(query.focus)
  if (focus) {
    const separator = focus.indexOf(':')
    const kind = separator > 0 ? focus.slice(0, separator) : ''
    const name = separator > 0 ? focus.slice(separator + 1) : ''
    // A slice name may contain a colon — `GET /api/x:y` is a legal Wolverine slice name — so the
    // split is on the FIRST one only, and an unknown kind is dropped rather than guessed at.
    if ((kind === 'slice' || kind === 'domain' || kind === 'chapter') && name.length > 0) {
      state.focus = { kind, name }
    }
  }

  const selection = str(query.sel)
  if (selection) state.selection = selection

  return state
}

/**
 * How big a minimap of this canvas is, and what it scales by (issue #296).
 *
 * ⚠️ **Deliberately NOT a uniform scale**, and that is the one surprising decision in this file.
 * The issue asks for "~1/40 scale", which is right for a canvas of ordinary proportions and wrong
 * for the canvas this feature exists for: 106 slices is ~53,000 × 550px, an aspect ratio near
 * 100:1. Scaled uniformly into any corner box it is a **hairline** — measured at 222 × 3.6px on
 * the real model, which is not a map of anything. So x and y get their own scales, each capped at
 * 1/40 so a small model still gets a small map rather than a magnified one.
 *
 * What that costs is card *shape*: at 106 slices a column is under a pixel wide and the map reads
 * as a barcode of lane bands. What it buys is the only two questions a minimap is asked — how far
 * along the model am I, and which lanes have anything in them — and both survive the distortion.
 * A faithful 100:1 rectangle answers neither.
 */
export const MINIMAP_SCALE = 1 / 40
export const MINIMAP_MAX_WIDTH = 260
export const MINIMAP_MAX_HEIGHT = 72

/**
 * Below this the canvas is a few screens and nobody gets lost in it, so there is nothing for a map
 * to answer — and a map of a two-slice model is a 30px smudge that reads as a rendering artefact.
 * ~2,500px is six slices; the model that produced this issue is twenty times that.
 */
export const MINIMAP_MIN_CANVAS_WIDTH = 2500

export function worthMapping(graph: EventModelGraph): boolean {
  return canvasSize(graph).width >= MINIMAP_MIN_CANVAS_WIDTH
}

export interface MinimapScale {
  x: number
  y: number
}

export function minimapScale(
  graph: EventModelGraph,
  max: { width: number; height: number } = {
    width: MINIMAP_MAX_WIDTH,
    height: MINIMAP_MAX_HEIGHT
  }
): MinimapScale {
  const canvas = canvasSize(graph)
  if (canvas.width <= 0 || canvas.height <= 0) return { x: MINIMAP_SCALE, y: MINIMAP_SCALE }
  return {
    // x is the axis the model actually runs along, so it takes the 1/40 ceiling and the box cap.
    x: Math.min(MINIMAP_SCALE, max.width / canvas.width),
    // y fills the box instead. An Event Model is four lanes tall whatever its width, so a
    // 1/40 height is ~13px — three pixels a lane, which is not a band, it is a smudge.
    y: Math.min(1, max.height / canvas.height)
  }
}

/**
 * The viewport's window on the minimap, in minimap pixels.
 *
 * Scroll offsets are scaled pixels and the minimap is a fraction of the *unscaled* canvas, so the
 * conversion divides by the zoom before it multiplies by the minimap scale. Clamped to the
 * minimap's own box: a canvas scrolled to its right edge on a viewport wider than it would
 * otherwise draw a window hanging off the side.
 */
export function minimapWindow(
  graph: EventModelGraph,
  scroll: { x: number; y: number; width: number; height: number },
  zoom: number,
  scale: MinimapScale
): Rect {
  const canvas = canvasSize(graph)
  const box = { width: canvas.width * scale.x, height: canvas.height * scale.y }
  const safeZoom = zoom > 0 ? zoom : 1

  const width = Math.min(box.width, (scroll.width / safeZoom) * scale.x)
  const height = Math.min(box.height, (scroll.height / safeZoom) * scale.y)
  return {
    x: Math.min(Math.max(0, (scroll.x / safeZoom) * scale.x), Math.max(0, box.width - width)),
    y: Math.min(Math.max(0, (scroll.y / safeZoom) * scale.y), Math.max(0, box.height - height)),
    width,
    height
  }
}

/** Where to scroll so a point clicked on the minimap ends up in the middle of the viewport. */
export function scrollForMinimapPoint(
  point: { x: number; y: number },
  viewport: { width: number; height: number },
  zoom: number,
  scale: MinimapScale
): { x: number; y: number } {
  const safeX = scale.x > 0 ? scale.x : MINIMAP_SCALE
  const safeY = scale.y > 0 ? scale.y : MINIMAP_SCALE
  return {
    x: Math.max(0, (point.x / safeX) * zoom - viewport.width / 2),
    y: Math.max(0, (point.y / safeY) * zoom - viewport.height / 2)
  }
}
