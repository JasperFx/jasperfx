<script setup lang="ts">
/**
 * Renders a JasperFx.Events `EventModelDescriptor` as an Event Modeling canvas.
 *
 * Deliberately renders the *descriptor* rather than any producer's private model. CritterWatch's
 * original `EventModelingView.vue` took its runtime `Lifecycle` and transformed it in the
 * component; that made the component unshareable, because the Bobcat console has no Lifecycle
 * and CritterWatch has no Bobcat generator. With jasperfx#687 the descriptor carries `elements`
 * and `edges` as "the rendering contract every viewer can draw from without a second transform",
 * so each producer adapts to the descriptor once and this component stays common.
 *
 * Layout is the pure grid from `layout.ts` — synchronous, no elk, no measurement pass.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  CANVAS_PADDING,
  CARD_PADDING_X,
  GUTTER_GAP,
  GUTTER_WIDTH,
  canvasSize,
  LABEL_FONT_SIZE,
  LABEL_LINE_HEIGHT,
  MAX_LABEL_LINES,
  layoutEventModel,
  type LayoutOptions
} from './layout'
import EventModelMinimap from './EventModelMinimap.vue'
import {
  breadcrumbFor,
  fitToRect,
  focusedSliceNames,
  lodFor,
  minimapScale,
  rectForSlices,
  scrollForMinimapPoint,
  selectionFromKey,
  selectionToKey,
  sliceOfSelection,
  worthMapping,
  type FocusTarget,
  type Selection,
  type ViewportState
} from './focus'
import { segmentLabel } from './text'
import { TRIGGER_ICON, TRIGGER_KIND_LABEL, parseRoute } from './icons'
import { colorFor, inkFor, DASHED_KINDS, OUTLINED_KINDS } from './palette'
import { chaptersOf, domainsOf, hiddenSliceNames, isEmptyFilter, type SliceFilter } from './filters'
import {
  LANE_LABEL,
  PROVENANCE_LABEL,
  type EventModelDescriptor,
  type EventModelElement,
  type EventModelSliceDescriptor,
  type HotspotDescriptor
} from './types'

const props = withDefaults(
  defineProps<{
    descriptor: EventModelDescriptor | null
    /** Slice names to collapse to a placeholder column. */
    /** Keyed by slice NAME (`EventModelSliceDescriptor.name`), not by any synthetic id. */
    collapsedSlices?: ReadonlySet<string>
    /** Outcome per spec identity, from run evidence (issue #107). Colours the slice header. */
    sliceOutcomes?: Record<string, 'passed' | 'failed' | 'notRun'>
    layout?: LayoutOptions
    /**
     * Show the filter bar (issue #194). On by default, and in the shared component rather than in
     * each host's page, so both consoles filter the same model the same way — the argument that
     * has won every other time on this canvas.
     */
    filterable?: boolean
    /**
     * Show the minimap (issue #296). On by default; a host embedding the canvas in a thumbnail
     * has nowhere to put it.
     */
    minimap?: boolean
    /**
     * Where to land on mount — zoom, scroll offsets, focus and selection (issue #296).
     *
     * The package REPORTS the viewport and ACCEPTS one; it never decides where to keep it. The
     * Bobcat console mirrors it into the route query so "CreditWallet, focused" can be pasted into
     * a PR; a host that wants none of that ignores `viewport-change` and passes nothing here.
     */
    initialViewport?: Partial<ViewportState> | null
  }>(),
  {
    descriptor: null,
    collapsedSlices: undefined,
    sliceOutcomes: undefined,
    layout: undefined,
    filterable: true,
    minimap: true,
    initialViewport: null
  }
)

const emit = defineEmits<{
  'element-click': [element: EventModelElement]
  /** The slice header was clicked — the drill-down hook (issue #108). */
  'slice-click': [slice: EventModelSliceDescriptor]
  /** The reader narrowed the canvas. Hosts that want to persist the view can listen. */
  'filter-change': [filter: SliceFilter]
  /** The reader moved, zoomed, focused or selected. The host decides whether to persist it (#296). */
  'viewport-change': [viewport: ViewportState]
}>()

// ------------------------------------------------------------------ filter & collapse (#194)
//
// Zoom is the wrong lever past a certain size and no tuning fixes it: CritterWatch's merged model
// is 106 slices and ~10,000px wide at the 25% zoom floor, and the floor is deliberate — below it
// the labels stop being labels. So the answer is to render fewer slices, not smaller ones.
//
// The state lives here, but the DECISION is a pure function (`hiddenSliceNames`) handed to
// `layoutEventModel` as an option. Layout never learns what a filter is, which is what keeps it a
// pure function of (descriptor, options) and keeps the two viewers' agreement checkable.

const filter = ref<SliceFilter>({})

const availableDomains = computed(() => domainsOf(props.descriptor))
const availableChapters = computed(() => chaptersOf(props.descriptor))

const hidden = computed(() => hiddenSliceNames(props.descriptor, filter.value))

const totalSlices = computed(() => (props.descriptor?.slices ?? []).length)
const shownSlices = computed(() => totalSlices.value - hidden.value.size)
const filtering = computed(() => !isEmptyFilter(filter.value))

function updateFilter(patch: Partial<SliceFilter>) {
  filter.value = { ...filter.value, ...patch }
  emit('filter-change', filter.value)
}

function toggleDomain(domain: string) {
  const next = new Set(filter.value.domains ?? [])
  if (next.has(domain)) next.delete(domain)
  else next.add(domain)
  updateFilter({ domains: next })
}

function toggleChapter(chapter: string) {
  const next = new Set(filter.value.chapters ?? [])
  if (next.has(chapter)) next.delete(chapter)
  else next.add(chapter)
  updateFilter({ chapters: next })
}

function clearFilter() {
  filter.value = {}
  emit('filter-change', filter.value)
}

// Collapse is per slice and reader-driven, unioned with whatever the host passed. A chevron in
// the header rather than a click ON the header: the header already opens the drill-down, and
// stealing that click would trade one navigation problem for another.
const collapsedByReader = ref<Set<string>>(new Set())

const effectiveCollapsed = computed(() => {
  const merged = new Set(collapsedByReader.value)
  for (const name of props.collapsedSlices ?? []) merged.add(name)
  return merged
})

function toggleCollapsed(name: string) {
  const next = new Set(collapsedByReader.value)
  if (next.has(name)) next.delete(name)
  else next.add(name)
  collapsedByReader.value = next
}

const graph = computed(() =>
  layoutEventModel(props.descriptor, {
    ...props.layout,
    collapsedSlices: effectiveCollapsed.value,
    hiddenSlices: hidden.value
  })
)

/**
 * #295 — how much of the cross-slice link layer to draw: everything, only what touches the
 * selection, or nothing.
 *
 * Three states rather than a checkbox because the honest answer depends on what the reader is
 * doing. `all` is the board; `selected` is following one thread; `none` is reading the slices
 * themselves, which is what the canvas was before links existed and is still a legitimate view of
 * it.
 */
export type LinkMode = 'all' | 'selected' | 'none'

const linkMode = ref<LinkMode>('all')

/** The links to draw: every one, or only those touching the current selection. */
const visibleLinks = computed(() => {
  if (linkMode.value === 'none') return []
  if (linkMode.value !== 'selected') return graph.value.links

  const slice = sliceOfSelection(selection.value)
  if (!slice) return []
  return graph.value.links.filter((link) => link.fromSlice === slice || link.toSlice === slice)
})

/**
 * Links the selection lights up. Computed from selection rather than from hover: selection changes
 * are rare, so this can ride reactive state, where a hover over one of 400 cards could not.
 */
const litLinks = computed(() => {
  const slice = sliceOfSelection(selection.value)
  if (!slice) return new Set<string>()

  const lit = new Set<string>()
  for (const link of graph.value.links) {
    if (link.fromSlice === slice || link.toSlice === slice) {
      lit.add(`${link.fromElementId}=>${link.toElementId}`)
    }
  }
  return lit
})

/**
 * The origin chevron an element carries when it is the far end of a link (#295).
 *
 * Event Modeling's own convention is to REPEAT the sticky where it is consumed rather than draw a
 * connector back to where it was produced — and the descriptor already repeats. So the chevron is
 * the local half of the picture: this card says where its input came from, in a slice name, which
 * stays readable at a zoom where a 3,000px arrow does not.
 */
function originOf(nodeId: string): { slice: string; elementId: string } | null {
  for (const link of graph.value.links) {
    if (link.toElementId === nodeId) return { slice: link.fromSlice, elementId: link.fromElementId }
  }
  return null
}

/** all → selected → none → all. One button rather than three, in a toolbar already dense. */
function cycleLinkMode() {
  linkMode.value = linkMode.value === 'all' ? 'selected' : linkMode.value === 'selected' ? 'none' : 'all'
}

/** Pan the origin of a link into view and flash it — what makes cause→effect navigable at 106 slices. */
function jumpToOrigin(nodeId: string) {
  const origin = originOf(nodeId)
  const node = origin ? graph.value.nodes.find((n) => n.id === origin.elementId) : null
  if (!node) return

  selectElement(node)
  scrollNodeIntoView(node)
}

function scrollNodeIntoView(node: { x: number; y: number; width: number; height: number }) {
  const element = viewport.value
  if (!element) return

  const centreX = (node.x + node.width / 2) * zoom.value + CANVAS_PADDING + GUTTER_WIDTH + GUTTER_GAP
  const centreY = (node.y + node.height / 2) * zoom.value + CANVAS_PADDING

  // Assigned, not `scrollTo({ behavior: 'smooth' })`. Smooth scrolling is a no-op wherever the
  // reader has asked for reduced motion — found by driving this in a browser, where the jump
  // selected the origin and then did not move at all — and every other scroll in this component
  // (zoom, pan, focus) already assigns directly for the same reason.
  element.scrollLeft = Math.max(0, centreX - element.clientWidth / 2)
  element.scrollTop = Math.max(0, centreY - element.clientHeight / 2)
}

/**
 * The stream rows to caption and tint (#299): every row of every lane the layout actually split.
 *
 * Flattened across lanes rather than nested under one, because only the `EventStream` lane splits
 * today and a viewer that hard-codes that would have to be edited the day a second lane does.
 */
const streamRows = computed(() =>
  graph.value.lanes.flatMap((lane) => (lane.rows.length > 1 ? lane.rows : []))
)

/**
 * Empty means there is no SLICE to draw, not no card.
 *
 * Node count was the same thing until slices could be collapsed by the reader (issue #194):
 * collapse every slice and the canvas has no nodes, but it has columns, headers and a reason for
 * each — replacing all of that with "No slices to render" tells the reader their model vanished.
 * Filtering everything out genuinely does leave nothing, and still reports empty.
 */
const isEmpty = computed(() => graph.value.slices.length === 0)

// ------------------------------------------------------------------ zoom & pan (bobcat#182)
//
// Stoat is 36 slices and CritterWatch's fleet-wide merged model rendered 121; a canvas that can
// only be read at 100% through a horizontal scrollbar is a canvas nobody reads. Zoom is a CSS
// transform on a wrapper, deliberately NOT a scale factor threaded into `layoutEventModel`:
// layout is a pure function of the descriptor, and letting the viewport change it would put the
// two viewers' agreement at the mercy of how wide someone's window happens to be.

/** Stops, not a continuous ramp — a reader wants to be able to return to a zoom they had. */
const ZOOM_STEPS = [0.25, 0.4, 0.55, 0.7, 0.85, 1, 1.25, 1.5, 2] as const
const MIN_ZOOM = ZOOM_STEPS[0]
const MAX_ZOOM = ZOOM_STEPS[ZOOM_STEPS.length - 1]

const zoom = ref<number>(1)
const viewport = ref<HTMLElement | null>(null)
const panning = ref(false)

const canvas = computed(() => canvasSize(graph.value))
/** The wrapper's own box: a transform does not change layout size, so the scroller needs this. */
const scaledSize = computed(() => ({
  width: Math.round(canvas.value.width * zoom.value),
  height: Math.round(canvas.value.height * zoom.value)
}))

/**
 * Change the zoom and keep the point in the middle of the viewport where it was.
 *
 * `transform-origin` is the top-left, so a bare zoom change anchors there — on a canvas 41,000px
 * wide (CritterWatch's merged model) zooming in while reading slice 80 throws the reader back to
 * slice 1. What someone means by "closer" is closer to *what they are looking at*, so the scroll
 * offset moves with the scale.
 */
function applyZoom(next: number) {
  const element = viewport.value
  const previous = zoom.value
  if (!element) {
    zoom.value = next
    return
  }

  // Where the middle of the viewport sits in unscaled canvas coordinates.
  const centreX = (element.scrollLeft + element.clientWidth / 2) / previous
  const centreY = (element.scrollTop + element.clientHeight / 2) / previous

  zoom.value = next
  // After the wrapper has been re-sized, or the scroller clamps the offset to the old width.
  void nextTick(() => {
    element.scrollLeft = Math.max(0, centreX * next - element.clientWidth / 2)
    element.scrollTop = Math.max(0, centreY * next - element.clientHeight / 2)
    trackScroll()
  })
}

function zoomIn() {
  applyZoom(ZOOM_STEPS.find((step) => step > zoom.value + 0.001) ?? MAX_ZOOM)
}

function zoomOut() {
  applyZoom([...ZOOM_STEPS].reverse().find((step) => step < zoom.value - 0.001) ?? MIN_ZOOM)
}

function resetZoom() {
  applyZoom(1)
}

/**
 * Fit the canvas to the viewport's width — the one zoom that is not a step, because "all of it on
 * screen" is a measurement rather than a preference. Never zooms *in* to fill: a small model blown
 * up to 200% looks like a mistake rather than like a fit.
 */
function fitToWidth() {
  const available = viewport.value?.clientWidth ?? 0
  const width = canvas.value.width
  if (available <= 0 || width <= 0) return
  applyZoom(Math.max(MIN_ZOOM, Math.min(1, available / width)))
}

let panFrom = { x: 0, y: 0, left: 0, top: 0 }

function startPan(event: MouseEvent) {
  // Left button only, and never a drag that begins on something clickable — a slice header and a
  // card are the two things on this canvas a reader actually presses.
  if (event.button !== 0) return
  if ((event.target as HTMLElement | null)?.closest('button')) return
  const element = viewport.value
  if (!element) return

  panning.value = true
  panFrom = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop }
  window.addEventListener('mousemove', onPan)
  window.addEventListener('mouseup', endPan)
}

function onPan(event: MouseEvent) {
  const element = viewport.value
  if (!element || !panning.value) return
  element.scrollLeft = panFrom.left - (event.clientX - panFrom.x)
  element.scrollTop = panFrom.top - (event.clientY - panFrom.y)
  trackScroll()
}

function endPan() {
  panning.value = false
  window.removeEventListener('mousemove', onPan)
  window.removeEventListener('mouseup', endPan)
}

// A drag that outlived the component would keep scrolling a detached element for ever.
onBeforeUnmount(endPan)

/**
 * Ctrl/⌘ + wheel is the pinch gesture a trackpad sends; a plain wheel stays scrolling.
 *
 * Continuous and anchored at the CURSOR rather than stepped and anchored at the middle (#296).
 * A pinch is an analogue gesture and snapping it to nine stops feels broken, and the point a
 * reader means by "closer" during a pinch is the one under their fingers — the middle of the
 * viewport is where the old stepped zoom put them instead, which on a 41,000px canvas is a
 * different slice. The stops stay as the button ladder, which is what they were good at.
 */
function onWheel(event: WheelEvent) {
  if (!event.ctrlKey && !event.metaKey) return
  event.preventDefault()

  const element = viewport.value
  const previous = zoom.value
  // Exponential in the delta, so a fast scroll and a slow one covering the same distance land in
  // the same place, and the step is proportional at every scale.
  const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, previous * Math.exp(-event.deltaY / 400)))
  if (!element || next === previous) {
    zoom.value = next
    return
  }

  const bounds = element.getBoundingClientRect()
  // Where the cursor is, in unscaled canvas coordinates.
  const anchorX = (element.scrollLeft + event.clientX - bounds.left) / previous
  const anchorY = (element.scrollTop + event.clientY - bounds.top) / previous

  zoom.value = next
  void nextTick(() => {
    element.scrollLeft = Math.max(0, anchorX * next - (event.clientX - bounds.left))
    element.scrollTop = Math.max(0, anchorY * next - (event.clientY - bounds.top))
    trackScroll()
  })
}

// ------------------------------------------------------------- focus, LOD, minimap, place (#296)
//
// Zoom answers "bigger"; none of this does. #182 gave nine stops and #194 gave a filter bar, and a
// 106-slice model is still ~10,000px wide at the 25% floor. What was missing was navigation: look
// at a region, see less when far out, know where you are, and be able to send someone the view.
//
// All four ride the transform wrapper #182 already built, and none of them reaches into
// `layoutEventModel` — the decisions live in `focus.ts` as pure functions over the laid-out graph,
// so the two viewers' agreement survives.

/** What the reader picked. Separate from focus: a click must not be able to move the viewport. */
const selection = ref<Selection | null>(null)

/** What the reader is looking at, if anything. */
const focus = ref<FocusTarget | null>(null)

/**
 * Where the reader was before they focused, so the way out is the way back.
 *
 * Captured once on entering a focus rather than on every step within one: a reader who walks
 * model → domain → slice and presses Esc means "put me back where I started", not "undo one rung".
 */
const beforeFocus = ref<{ zoom: number; x: number; y: number } | null>(null)

const focusedSlices = computed(() => focusedSliceNames(props.descriptor, focus.value))
const crumbs = computed(() => breadcrumbFor(props.descriptor, focus.value))

/** The attribute the stylesheet switches on — no re-layout, identical in both consoles. */
const lod = computed(() => lodFor(zoom.value))

/** The scroller's live box, mirrored into reactive state for the minimap's window. */
const scrollState = ref({ x: 0, y: 0, width: 0, height: 0 })

function trackScroll() {
  const element = viewport.value
  if (!element) return
  scrollState.value = {
    x: element.scrollLeft,
    y: element.scrollTop,
    width: element.clientWidth,
    height: element.clientHeight
  }
  reportViewport()
}

function reportViewport() {
  emit('viewport-change', {
    zoom: zoom.value,
    x: scrollState.value.x,
    y: scrollState.value.y,
    focus: focus.value,
    selection: selectionToKey(selection.value)
  })
}

function applyViewport(next: { zoom: number; x: number; y: number }) {
  const element = viewport.value
  zoom.value = next.zoom
  if (!element) return
  void nextTick(() => {
    element.scrollLeft = next.x
    element.scrollTop = next.y
    trackScroll()
  })
}

/**
 * Fit a focus target's neighbourhood into the viewport and dim everything else.
 *
 * `scale = clamp(min(vw/w, vh/h))`, then scroll to centre — the arithmetic is in `fitToRect` so it
 * is testable without a DOM. A target that resolves to nothing on the canvas (a slice the filter
 * bar hid) still becomes the focus but leaves the viewport alone: moving someone to an empty
 * rectangle is worse than not moving them.
 */
function focusOn(target: FocusTarget) {
  const element = viewport.value
  if (!beforeFocus.value && element) {
    beforeFocus.value = { zoom: zoom.value, x: element.scrollLeft, y: element.scrollTop }
  }
  focus.value = target

  const rect = rectForSlices(graph.value, focusedSliceNames(props.descriptor, target))
  if (!rect || !element || element.clientWidth <= 0) {
    reportViewport()
    return
  }

  // The scroller only constrains vertically when it actually scrolls vertically. Left unasked,
  // `vh/h` is the zoom the reader already had and the fit does nothing — see `fitToRect`.
  const verticallyBound = element.scrollHeight > element.clientHeight + 1

  applyViewport(
    fitToRect(
      rect,
      { width: element.clientWidth, height: verticallyBound ? element.clientHeight : 0 },
      { min: MIN_ZOOM, max: MAX_ZOOM }
    )
  )
}

/** Focus what is selected. A card focuses the slice that owns it — a card is not a neighbourhood. */
function focusSelection() {
  const name = sliceOfSelection(selection.value)
  if (name) focusOn({ kind: 'slice', name })
}

function clearFocus() {
  const previous = beforeFocus.value
  focus.value = null
  beforeFocus.value = null
  if (previous) applyViewport(previous)
  else reportViewport()
}

/** A crumb is a step out: the model clears the focus, a domain focuses its band. */
function goToCrumb(target: FocusTarget | null) {
  if (target) focusOn(target)
  else clearFocus()
}

function selectElement(node: { id: string; element: EventModelElement; sliceName: string }) {
  selection.value = { kind: 'element', id: node.id, sliceName: node.sliceName }
  reportViewport()
  emit('element-click', node.element)
}

function selectSlice(slice: EventModelSliceDescriptor) {
  selection.value = { kind: 'slice', name: slice.name }
  reportViewport()
  emit('slice-click', slice)
}

/** Dim everything outside the focus. Empty when nothing is focused — never dim the whole model. */
function dimmed(sliceName: string): boolean {
  return focusedSlices.value.size > 0 && !focusedSlices.value.has(sliceName)
}

/**
 * Esc steps out — the focus first, then the selection.
 *
 * On `window` rather than on the viewport: the canvas is a scroller nobody clicks into, so keying
 * it would mean Esc worked only after a click that was already ambiguous. Ignored while a text
 * field has the keyboard, so Esc in the filter search clears the search rather than the focus.
 */
function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  const active = (event.target as HTMLElement | null)?.tagName
  if (active === 'INPUT' || active === 'TEXTAREA') return
  if (focus.value) {
    event.preventDefault()
    clearFocus()
  } else if (selection.value) {
    selection.value = null
    reportViewport()
  }
}

const minimapZoom = computed(() => minimapScale(graph.value))

/**
 * A map of a canvas that fits on two screens answers a question nobody asked, and draws a 30px
 * smudge doing it. Measured on the graph rather than on the viewport, so it does not appear and
 * vanish as someone resizes their window.
 */
const showMinimap = computed(() => props.minimap && worthMapping(graph.value))

function onMinimapGoto(point: { x: number; y: number }) {
  const element = viewport.value
  if (!element) return
  const next = scrollForMinimapPoint(
    point,
    { width: element.clientWidth, height: element.clientHeight },
    zoom.value,
    minimapZoom.value
  )
  element.scrollLeft = next.x
  element.scrollTop = next.y
  trackScroll()
}

/**
 * Land on the host's viewport once the canvas exists.
 *
 * A focus is applied by *re-fitting* rather than by restoring the stored offsets, so a link opened
 * on a narrower window still frames the neighbourhood instead of landing on coordinates measured
 * somewhere else. A plain zoom/scroll link restores exactly, because there nothing was fitted.
 */
function restore(state: Partial<ViewportState> | null | undefined) {
  if (!state) return
  selection.value = selectionFromKey(props.descriptor, state.selection)

  if (state.focus) {
    focusOn(state.focus)
    return
  }
  if (state.zoom === undefined && state.x === undefined && state.y === undefined) return
  applyViewport({
    zoom: Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, state.zoom ?? zoom.value)),
    x: state.x ?? 0,
    y: state.y ?? 0
  })
}

onMounted(() => {
  globalThis.window.addEventListener('keydown', onKeydown)
  trackScroll()
  restore(props.initialViewport)
})

onBeforeUnmount(() => globalThis.window.removeEventListener('keydown', onKeydown))

// A descriptor swap is a different model: a focus on a slice it does not contain would dim the
// whole canvas with no way to see why.
watch(
  () => props.descriptor,
  () => {
    focus.value = null
    beforeFocus.value = null
    selection.value = null
  }
)

function styleFor(element: EventModelElement) {
  const fill = colorFor(element.kind)
  const outlined = OUTLINED_KINDS.includes(element.kind)
  const dashed = DASHED_KINDS.includes(element.kind)
  return {
    background: outlined ? 'transparent' : fill,
    color: outlined ? fill : inkFor(element.kind),
    border: `${outlined || dashed ? 2 : 1}px ${dashed ? 'dashed' : 'solid'} ${fill}`
  }
}

/**
 * jasperfx#704 — the hotspot behind a Hotspot card, looked up by label.
 *
 * The producer projects each hotspot into an element with `ForLabel(name, Hotspot, hotspot.Text)`,
 * so the label IS the hotspot text and that is the only join available. Worth it: without it a
 * source disagreement renders as a generic magenta sticky indistinguishable from a pending spec,
 * and "the code says this slice emits X; production says X and Y" is the most valuable thing a
 * four-source model produces.
 */
function hotspotFor(node: { element: EventModelElement; sliceName: string }): HotspotDescriptor | null {
  if (node.element.kind !== 'Hotspot') return null
  const slice = graph.value.slices.find((s) => s.name === node.sliceName)
  return (slice?.descriptor.hotspots ?? []).find((h) => h.text === node.element.label) ?? null
}

/**
 * Tooltip text. The type's full name stays the headline where there is one; the ladder rung and a
 * disagreement's two claims are appended, because both are exactly the kind of thing you want on
 * hover rather than crowding the card.
 */
function titleFor(node: { element: EventModelElement; sliceName: string }): string {
  const parts: string[] = [node.element.type?.fullName ?? node.element.label]

  const provenance = node.element.provenance
  if (provenance) parts.push(PROVENANCE_LABEL[provenance] ?? provenance)

  const hotspot = hotspotFor(node)
  if (hotspot?.origin === 'SourceDisagreement' && hotspot.winningClaim && hotspot.losingClaim) {
    parts.push(
      `Kept: ${hotspot.winningClaim.provenance} claims ${hotspot.winningClaim.value}`,
      `Dropped: ${hotspot.losingClaim.provenance} claims ${hotspot.losingClaim.value}`
    )
  }

  return parts.join('\n')
}

/**
 * bobcat#181 — an edge as an SVG polyline. The points are `layout.ts`'s routing decision, not this
 * component's: two viewers drawing the same descriptor differently is what this package exists to
 * prevent, and a route is as much a rendering claim as a coordinate.
 */
function pointsFor(edge: { points: ReadonlyArray<{ x: number; y: number }> }): string {
  return edge.points.map((p) => `${p.x},${p.y}`).join(' ')
}

/**
 * bobcat#180 — the label, split at the points a reader would break it themselves.
 *
 * Rendered with a `<wbr>` between segments because CSS offers a browser break opportunities at
 * spaces and hyphens and nowhere else: `DepositMoneyIntoAccount` and `POST /accounts/{id}/deposit`
 * are each one unbreakable word to the layout engine, which is exactly why the old fixed-width
 * card clipped them.
 */
function segmentsFor(element: EventModelElement): string[] {
  return segmentLabel(element.label ?? '')
}

/**
 * bobcat#178 — a source disagreement is a FINDING, and the card has to say so.
 *
 * The producer makes the hotspot's text the element label, so the card used to lead with the role
 * name and a clipped sentence: *"EmittedEvents: Derived claims ClaimResult; Declared claims
 * NodeClaimed, Cl…"*. The reviewer who designed the feature read it as a malformed events list,
 * which is the whole finding — nothing on the card said two sources disagreed, and the one thing
 * a four-source model produces that nothing else can deserves better than tooltip-only.
 *
 * So a hotspot card renders structure instead of prose: what kind of finding it is, then (for a
 * disagreement) the role and the two claims with their rungs, kept first. The claims are typed on
 * the descriptor — `role`, `winningClaim`, `losingClaim` — so none of this is parsed back out of
 * the sentence.
 */
const HOTSPOT_ORIGIN_LABEL: Record<string, string> = {
  SourceDisagreement: 'Sources disagree',
  PendingSpecification: 'Pending spec',
  Prose: 'Note'
}

function originLabelFor(hotspot: HotspotDescriptor): string {
  return HOTSPOT_ORIGIN_LABEL[hotspot.origin] ?? hotspot.origin
}

/** The two claims of a disagreement, kept first — null unless both are on the descriptor. */
function claimsFor(hotspot: HotspotDescriptor) {
  if (hotspot.origin !== 'SourceDisagreement') return null
  const kept = hotspot.winningClaim
  const dropped = hotspot.losingClaim
  // A disagreement whose pair did not survive the wire degrades to its text, never to half a
  // finding with one side missing.
  return kept && dropped ? { kept, dropped } : null
}

/** A trigger card whose label is an HTTP route renders its verb as a badge (#184). */
function routeFor(element: EventModelElement) {
  return element.kind === 'Trigger' ? parseRoute(element.label ?? '') : null
}

/**
 * A slice *named* for its route gets the same treatment (#184 follow-on).
 *
 * Wolverine names a query slice for its verb and route — `GET /api/clients/{id}/accounts` — so the
 * header spends its scarcest width on a word the glyph beside it has already said. Badging the verb
 * costs the header three characters instead of eight and makes the path start where the eye
 * expects it. The name itself is untouched; this is only how it is drawn.
 */
function sliceRouteFor(slice: EventModelSliceDescriptor) {
  return parseRoute(slice.name)
}

/**
 * bobcat#184 — the slice's trigger kind, as a glyph and a tooltip.
 *
 * `triggerKind` is on every derived slice and was rendered nowhere. The tooltip carries
 * `triggerOrigin` with it, which is where the route lives now that wolverine#4181 has stopped the
 * HTTP source claiming `TriggerLabel` — so the human label stays on the card and the machine
 * detail is one hover away instead of eating a column of width.
 */
function triggerIconFor(slice: EventModelSliceDescriptor): string | null {
  const kind = slice.triggerKind
  return kind ? (TRIGGER_ICON[kind] ?? null) : null
}

function triggerTitleFor(slice: EventModelSliceDescriptor): string {
  const kind = slice.triggerKind
  const parts = [kind ? (TRIGGER_KIND_LABEL[kind] ?? kind) : 'Trigger']
  if (slice.triggerOrigin) parts.push(slice.triggerOrigin)
  return parts.join(' · ')
}

/**
 * bobcat#183 — how many specifications are bound to this slice, as the header's badge.
 *
 * The count is the point, not decoration: a slice with no spec is the drift case the canvas
 * already colours orange, and until now the only way to learn a slice HAD specs was to click it.
 * Where the host supplied run evidence the badge carries the verdict too (`outcomeFor`), so a
 * failing slice says so at the same glance.
 */
function specCountFor(slice: EventModelSliceDescriptor): number {
  return (slice.specifications ?? []).length
}

function specLabelFor(slice: EventModelSliceDescriptor): string {
  const count = specCountFor(slice)
  if (count === 0) return 'no spec'
  return count === 1 ? '1 spec' : `${count} specs`
}

/** The bound identities, on hover — the drill-down is a click away, the list should not be. */
function specTitleFor(slice: EventModelSliceDescriptor): string {
  const specs = slice.specifications ?? []
  if (specs.length === 0) return 'No specification is bound to this slice'
  return specs.map((spec) => spec.identity).join('\n')
}

/** The slice's outcome, if run evidence named any of its specifications. */
function outcomeFor(sliceName: string): string | null {
  const outcomes = props.sliceOutcomes
  if (!outcomes) return null
  const slice = graph.value.slices.find((s) => s.name === sliceName)
  const identities = (slice?.descriptor.specifications ?? []).map((s) => s.identity)
  if (identities.some((id) => outcomes[id] === 'failed')) return 'failed'
  if (identities.some((id) => outcomes[id] === 'notRun')) return 'notRun'
  if (identities.length > 0 && identities.every((id) => outcomes[id] === 'passed')) return 'passed'
  return null
}
</script>

<template>
  <div class="em-canvas" data-testid="event-model">
    <p v-if="isEmpty" class="em-empty" data-testid="event-model-empty">
      No slices to render.
    </p>
    <template v-else>
      <!-- bobcat#182 — 36 slices do not fit at 100%, and 121 (CritterWatch's merged fleet model)
           are not close. Stops rather than a continuous ramp, plus a measured fit-to-width. -->
      <div class="em-toolbar">
        <!-- issue #296 — the breadcrumb IS the way out: each crumb is a step up the focus ladder
             (model → domain → slice), and Esc does the whole journey at once. -->
        <nav v-if="crumbs.length > 0" class="em-crumbs" data-testid="event-model-crumbs">
          <template v-for="(crumb, index) in crumbs" :key="`${index}-${crumb.label}`">
            <span v-if="index > 0" class="em-crumb-sep" aria-hidden="true">›</span>
            <button
              type="button"
              class="em-crumb"
              :data-current="index === crumbs.length - 1 ? 'true' : undefined"
              :disabled="index === crumbs.length - 1"
              @click="goToCrumb(crumb.target)"
            >
              {{ crumb.label }}
            </button>
          </template>
          <button
            type="button"
            class="em-zoom em-focus-clear"
            title="Clear the focus (Esc)"
            data-testid="focus-clear"
            @click="clearFocus"
          >
            ✕
          </button>
        </nav>

        <!-- Select a slice or a card, then focus its neighbourhood. Two controls rather than one
             because a click that moved the viewport would be a click that lost your place. -->
        <button
          type="button"
          class="em-zoom em-focus"
          data-testid="focus-selection"
          :disabled="selection === null"
          :title="
            selection === null
              ? 'Select a slice or a card first'
              : `Focus ${sliceOfSelection(selection)} and its neighbours`
          "
          @click="focusSelection"
        >
          Focus
        </button>
        <!-- #295 — links: all / selected / none. Three states because the honest answer depends on
             what the reader is doing, and "none" is the canvas as it was before links existed,
             which is still a legitimate way to read the slices themselves. -->
        <button
          type="button"
          class="em-zoom em-links-mode"
          :data-mode="linkMode"
          :title="`Links: ${linkMode} — click to cycle`"
          data-testid="link-mode"
          @click="cycleLinkMode"
        >
          ⇢ {{ linkMode }}
        </button>
        <button
          type="button"
          class="em-zoom em-zoom-out"
          title="Zoom out"
          :disabled="zoom <= MIN_ZOOM"
          @click="zoomOut"
        >
          −
        </button>
        <button type="button" class="em-zoom em-zoom-level" title="Reset to 100%" @click="resetZoom">
          {{ Math.round(zoom * 100) }}%
        </button>
        <button
          type="button"
          class="em-zoom em-zoom-in"
          title="Zoom in"
          :disabled="zoom >= MAX_ZOOM"
          @click="zoomIn"
        >
          +
        </button>
        <button type="button" class="em-zoom em-zoom-fit" title="Fit to width" @click="fitToWidth">
          Fit
        </button>
      </div>

      <!-- issue #194 — zoom cannot make 106 slices navigable, so narrow the model instead.
           Every control here resolves to `hiddenSliceNames`, a pure function; the layout is
           handed a set of names and never learns a filter exists. -->
      <div v-if="filterable" class="em-filterbar" data-testid="event-model-filter">
        <input
          class="em-filter-search"
          type="search"
          placeholder="Find a slice…"
          data-testid="filter-search"
          :value="filter.search ?? ''"
          @input="updateFilter({ search: ($event.target as HTMLInputElement).value })"
        />

        <div v-if="availableDomains.length > 0" class="em-filter-domains">
          <button
            v-for="domain in availableDomains"
            :key="domain"
            type="button"
            class="em-filter-chip"
            :data-domain="domain"
            :data-on="filter.domains?.has(domain) ? 'true' : undefined"
            :aria-pressed="filter.domains?.has(domain) ? 'true' : 'false'"
            @click="toggleDomain(domain)"
          >
            {{ domain }}
          </button>
        </div>

        <!-- #298 — chapter chips beside the domain chips, in the model's own order: a chapter is
             a span of the timeline, so alphabetising them would shuffle the story. -->
        <div v-if="availableChapters.length > 0" class="em-filter-domains em-filter-chapters">
          <button
            v-for="chapter in availableChapters"
            :key="chapter"
            type="button"
            class="em-filter-chip em-filter-chip-chapter"
            :data-chapter="chapter"
            :data-on="filter.chapters?.has(chapter) ? 'true' : undefined"
            :aria-pressed="filter.chapters?.has(chapter) ? 'true' : 'false'"
            @click="toggleChapter(chapter)"
          >
            {{ chapter }}
          </button>
        </div>

        <!-- The drift view. `unbound` is the one that earns its place: on the measured model 125
             of 125 slices had no spec, and "which ones do" is the whole question. -->
        <button
          type="button"
          class="em-filter-chip"
          data-testid="filter-unbound"
          :data-on="filter.specs === 'unbound' ? 'true' : undefined"
          :aria-pressed="filter.specs === 'unbound' ? 'true' : 'false'"
          title="Only slices with no bound specification"
          @click="updateFilter({ specs: filter.specs === 'unbound' ? 'any' : 'unbound' })"
        >
          no spec
        </button>
        <button
          type="button"
          class="em-filter-chip"
          data-testid="filter-bound"
          :data-on="filter.specs === 'bound' ? 'true' : undefined"
          :aria-pressed="filter.specs === 'bound' ? 'true' : 'false'"
          title="Only slices with at least one bound specification"
          @click="updateFilter({ specs: filter.specs === 'bound' ? 'any' : 'bound' })"
        >
          has spec
        </button>
        <button
          type="button"
          class="em-filter-chip"
          data-testid="filter-hotspots"
          :data-on="filter.hotspotsOnly ? 'true' : undefined"
          :aria-pressed="filter.hotspotsOnly ? 'true' : 'false'"
          title="Only slices carrying a hotspot"
          @click="updateFilter({ hotspotsOnly: !filter.hotspotsOnly })"
        >
          hotspots
        </button>

        <!-- Always shown, not only while filtering: a canvas that silently renders a subset is
             how a reader concludes a slice does not exist. -->
        <span class="em-filter-count" data-testid="filter-count">
          {{ shownSlices }} of {{ totalSlices }}
        </span>
        <button
          v-if="filtering"
          type="button"
          class="em-filter-clear"
          data-testid="filter-clear"
          @click="clearFilter"
        >
          clear
        </button>
      </div>

      <div
        ref="viewport"
        class="em-viewport"
        :data-panning="panning ? 'true' : undefined"
        :data-lod="lod"
        :data-focused="focus ? 'true' : undefined"
        @mousedown="startPan"
        @wheel="onWheel"
        @scroll="trackScroll"
      >
        <div
          class="em-zoomed"
          :style="{
            width: `${scaledSize.width}px`,
            height: `${scaledSize.height}px`,
            transform: `scale(${zoom})`
          }"
        >
          <div class="em-scroll" :style="{ gap: `${GUTTER_GAP}px`, padding: `${CANVAS_PADDING}px` }">
          <div class="em-lane-gutter" :style="{ flexBasis: `${GUTTER_WIDTH}px` }">
            <div
              v-for="lane in graph.lanes"
              :key="lane.lane"
              class="em-lane-label"
              :data-split="lane.rows.length > 1 ? 'true' : undefined"
              :style="{ top: `${lane.y}px`, height: `${lane.height}px` }"
            >
              {{ LANE_LABEL[lane.lane] }}
            </div>
            <!-- #299 — one caption per stream row, in the gutter under the lane's own caption.
                 Only ever rendered for a lane the layout actually split, so a model with one
                 aggregate has no second column of text to explain. -->
            <div
              v-for="row in streamRows"
              :key="`row-label-${row.key ?? 'unassigned'}`"
              class="em-stream-row-label"
              :data-unassigned="row.key === null ? 'true' : undefined"
              :title="row.label ?? 'On no aggregate stream — published messages, and events of a slice that writes no aggregate'"
              :style="{ top: `${row.y}px`, height: `${row.height}px` }"
            >
              {{ row.label ?? '—' }}
            </div>
          </div>

          <div class="em-plot" :style="{ width: `${graph.width}px`, height: `${graph.height}px` }">
            <div
              v-for="lane in graph.lanes"
              :key="`band-${lane.lane}`"
              class="em-lane-band"
              :style="{ top: `${lane.y}px`, height: `${lane.height}px`, width: `${graph.width}px` }"
            />

            <!-- #299 — the rows' own banding. It alternates rather than drawing a rule per row:
                 the lane band already owns the rules, and a second set of hairlines inside it reads
                 as four lanes rather than as one lane of streams. This is also what still separates
                 the rows at `overview`, where the captions are gone. -->
            <div
              v-for="(row, index) in streamRows"
              :key="`row-band-${row.key ?? 'unassigned'}`"
              class="em-stream-band"
              :data-alt="index % 2 === 1 ? 'true' : undefined"
              :style="{ top: `${row.y}px`, height: `${row.height}px`, width: `${graph.width}px` }"
            />

            <!-- #298 — chapter bands: the wide arrow eventmodelers.ai draws over a chapter's slices,
                 one per contiguous run in declaration order (the canvas never reorders). A band is
                 a button because it is the focus rung above slice: click it and the chapter fits the
                 viewport, the same way a slice's ⌖ does. -->
            <button
              v-for="(band, index) in graph.chapters"
              :key="`chapter-${index}-${band.name}`"
              type="button"
              class="em-chapter-band"
              :data-chapter="band.name"
              :data-focused="focus?.kind === 'chapter' && focus.name === band.name ? 'true' : undefined"
              :data-dimmed="focusedSlices.size > 0 && !band.slices.some((s) => focusedSlices.has(s)) ? 'true' : undefined"
              :title="`Focus the ${band.name} chapter (${band.slices.length} slice${band.slices.length === 1 ? '' : 's'})`"
              :style="{ left: `${band.x}px`, top: `${band.y}px`, width: `${band.width}px`, height: `${band.height}px` }"
              @click.stop="focusOn({ kind: 'chapter', name: band.name })"
            >
              <span class="em-chapter-name">{{ band.name }}</span>
            </button>

            <div
              v-for="slice in graph.slices"
              :key="`slice-${slice.name}`"
              class="em-slice"
              :data-slice="slice.name"
              :data-outcome="outcomeFor(slice.name) ?? undefined"
              :data-dimmed="dimmed(slice.name) ? 'true' : undefined"
              :data-selected="selection?.kind === 'slice' && selection.name === slice.name ? 'true' : undefined"
              :style="{
                left: `${slice.x}px`,
                top: `${graph.chapterBandHeight}px`,
                width: `${slice.width}px`,
                height: `${graph.height - graph.chapterBandHeight}px`
              }"
            >
              <div class="em-slice-header" :style="{ maxWidth: `${slice.width - 8}px` }">
                <!-- issue #194 — collapse THIS slice. A chevron rather than a click on the header,
                     because the header already opens the drill-down and stealing that click would
                     trade one navigation problem for another. -->
                <button
                  type="button"
                  class="em-slice-collapse"
                  :data-collapsed="slice.collapsed ? 'true' : undefined"
                  :aria-expanded="slice.collapsed ? 'false' : 'true'"
                  :title="slice.collapsed ? `Expand ${slice.name}` : `Collapse ${slice.name}`"
                  @click.stop="toggleCollapsed(slice.name)"
                >
                  {{ slice.collapsed ? '›' : '‹' }}
                </button>
                <!-- #296 — focus THIS slice, without going via the toolbar.
                     Not a convenience: the slice name opens the host's drill-down, and in the
                     Bobcat console that is a MODAL drawer whose overlay then covers the toolbar.
                     "Select, then press Focus" is unreachable the moment the host reacts to the
                     selection, which was found by driving the real 106-slice canvas. Its own
                     control also makes the feature discoverable, which "select something first"
                     never was. -->
                <button
                  type="button"
                  class="em-slice-focus"
                  :data-focused="focus?.kind === 'slice' && focus.name === slice.name ? 'true' : undefined"
                  :title="`Focus ${slice.name} and its neighbours`"
                  @click.stop="focusOn({ kind: 'slice', name: slice.name })"
                >
                  ⌖
                </button>
                <!-- bobcat#184 — what kind of thing triggers this slice, legible without reading. -->
                <svg
                  v-if="triggerIconFor(slice.descriptor)"
                  class="em-trigger-icon"
                  :data-kind="slice.descriptor.triggerKind"
                  viewBox="0 0 16 16"
                  role="img"
                  :aria-label="triggerTitleFor(slice.descriptor)"
                >
                  <title>{{ triggerTitleFor(slice.descriptor) }}</title>
                  <path :d="triggerIconFor(slice.descriptor) ?? undefined" />
                </svg>
                <button
                  class="em-slice-name"
                  type="button"
                  :title="slice.name"
                  @click="selectSlice(slice.descriptor)"
                >
                  <template v-if="sliceRouteFor(slice.descriptor)"
                    ><span class="em-route-method">{{
                      sliceRouteFor(slice.descriptor)!.method
                    }}</span
                    >{{ sliceRouteFor(slice.descriptor)!.path }}</template
                  >
                  <template v-else>{{ slice.name }}</template>
                </button>
                <!-- bobcat#183 — the bound-specification count, verdict-tinted where the host gave
                     run evidence. `no spec` is deliberately spelled out rather than shown as 0: it is
                     the drift case, and it should read as a finding. -->
                <!-- A button, not a label: it names the specifications, so a reader clicks it
                     expecting to see them. It opens the drawer the slice name opens. -->
                <button
                  class="em-slice-specs"
                  type="button"
                  :data-outcome="outcomeFor(slice.name) ?? (specCountFor(slice.descriptor) === 0 ? 'none' : undefined)"
                  :data-count="specCountFor(slice.descriptor)"
                  :title="specTitleFor(slice.descriptor)"
                  @click="selectSlice(slice.descriptor)"
                >
                  {{ specLabelFor(slice.descriptor) }}
                </button>
                <!-- #296 — at `overview` the cards have no text at all, so the column's own name
                     and pattern are the only thing left saying what it is. Rendered always and
                     revealed by CSS, because a `v-if` on the level of detail would re-render the
                     graph on every notch of a pinch. -->
                <span v-if="slice.descriptor.pattern" class="em-slice-pattern">{{
                  slice.descriptor.pattern
                }}</span>
              </div>
            </div>

            <!-- bobcat#181 — the edges the descriptor already computes, which the canvas used to drop
                 on the floor. Behind the cards in DOM order and pointer-inert, so a line never eats a
                 card's click; `currentColor` so it inherits the host's ink in either theme. -->
            <svg
              class="em-edges"
              :width="graph.width"
              :height="graph.height"
              :viewBox="`0 0 ${graph.width} ${graph.height}`"
              aria-hidden="true"
            >
              <defs>
                <marker
                  id="em-arrow"
                  markerWidth="6"
                  markerHeight="6"
                  refX="5"
                  refY="3"
                  orient="auto"
                  markerUnits="strokeWidth"
                >
                  <path d="M0,0 L6,3 L0,6 z" fill="currentColor" />
                </marker>
              </defs>
              <polyline
                v-for="edge in graph.edges"
                :key="`${edge.fromId}->${edge.toId}`"
                class="em-edge"
                :data-from="edge.fromId"
                :data-to="edge.toId"
                :points="pointsFor(edge)"
                marker-end="url(#em-arrow)"
              />

              <!-- #295 — the cross-slice links, in their own <g> so the toolbar can hide the lot
                   without touching the intra-slice edges, which are a different kind of claim.
                   Thinner and fainter than an edge at rest: at 106 slices these are the lines that
                   would otherwise dominate a picture whose subject is the slices. -->
              <g v-if="linkMode !== 'none'" class="em-links" :data-mode="linkMode">
                <polyline
                  v-for="link in visibleLinks"
                  :key="`${link.fromElementId}=>${link.toElementId}`"
                  class="em-link"
                  :data-lit="litLinks.has(`${link.fromElementId}=>${link.toElementId}`) ? 'true' : undefined"
                  :data-kind="link.kind"
                  :data-from="link.fromElementId"
                  :data-to="link.toElementId"
                  :data-from-slice="link.fromSlice"
                  :data-to-slice="link.toSlice"
                  :points="pointsFor(link)"
                  marker-end="url(#em-arrow)"
                />
              </g>
            </svg>

            <button
              v-for="node in graph.nodes"
              :key="node.id"
              class="em-card"
              type="button"
              :data-kind="node.element.kind"
              :data-lane="node.element.lane"
              :data-provenance="node.element.provenance ?? undefined"
              :data-hotspot-origin="hotspotFor(node)?.origin ?? undefined"
              :data-dimmed="dimmed(node.sliceName) ? 'true' : undefined"
              :data-selected="selection?.kind === 'element' && selection.id === node.id ? 'true' : undefined"
              :title="titleFor(node)"
              :style="{
                left: `${node.x}px`,
                top: `${node.y}px`,
                width: `${node.width}px`,
                height: `${node.height}px`,
                padding: `6px ${CARD_PADDING_X}px`,
                fontSize: `${LABEL_FONT_SIZE}px`,
                lineHeight: `${LABEL_LINE_HEIGHT}`,
                '--em-label-lines': MAX_LABEL_LINES,
                ...styleFor(node.element)
              }"
              @click="selectElement(node)"
            >
              <!-- #295 — the origin chevron. A span rather than a nested <button>, which is
                   invalid inside one: the card's own click selects, and this one's stopPropagation
                   makes it jump instead. -->
              <span
                v-if="originOf(node.id)"
                class="em-origin"
                role="button"
                tabindex="0"
                :title="`From ${originOf(node.id)!.slice} — click to jump there`"
                @click.stop="jumpToOrigin(node.id)"
                @keydown.enter.stop.prevent="jumpToOrigin(node.id)"
                >◂ {{ originOf(node.id)!.slice }}</span
              >
              <span v-if="hotspotFor(node)" class="em-hotspot">
                <span class="em-hotspot-origin">{{ originLabelFor(hotspotFor(node)!) }}</span>
                <template v-if="claimsFor(hotspotFor(node)!)">
                  <span v-if="hotspotFor(node)!.role" class="em-hotspot-role">{{
                    hotspotFor(node)!.role
                  }}</span>
                  <span class="em-hotspot-claim" data-claim="kept">
                    <span class="em-hotspot-rung">{{ claimsFor(hotspotFor(node)!)!.kept.provenance }}</span>
                    {{ claimsFor(hotspotFor(node)!)!.kept.value }}
                  </span>
                  <span class="em-hotspot-claim" data-claim="dropped">
                    <span class="em-hotspot-rung">{{
                      claimsFor(hotspotFor(node)!)!.dropped.provenance
                    }}</span>
                    {{ claimsFor(hotspotFor(node)!)!.dropped.value }}
                  </span>
                </template>
                <span v-else class="em-hotspot-text">{{ hotspotFor(node)!.text }}</span>
              </span>
              <span v-else-if="routeFor(node.element)" class="em-card-label"
                ><span class="em-route-method">{{ routeFor(node.element)!.method }}</span
                ><template
                  v-for="(segment, index) in segmentLabel(routeFor(node.element)!.path)"
                  :key="index"
                  >{{ segment }}<wbr /></template
              ></span>
              <span v-else class="em-card-label"
                ><template v-for="(segment, index) in segmentsFor(node.element)" :key="index"
                  >{{ segment }}<wbr /></template
              ></span>
            </button>
            </div>
          </div>
        </div>
      </div>

      <!-- issue #296 — the same graph again, as rects, so a reader can see where they are on a
           canvas several screens wide. Outside the scroller deliberately: it is chrome over the
           canvas, not part of the thing being scrolled. -->
      <div v-if="showMinimap" class="em-minimap-holder">
        <EventModelMinimap
          :graph="graph"
          :zoom="zoom"
          :scroll="scrollState"
          :focused="focusedSlices"
          @goto="onMinimapGoto"
        />
      </div>
    </template>
  </div>
</template>

<style scoped>
.em-canvas {
  position: relative;
  width: 100%;
}
/* The scroller, and the pan surface. Grab-to-drag because at 55% a 121-slice model is still
   several screens wide, and a horizontal scrollbar is a poor way to travel that. */
.em-viewport {
  width: 100%;
  overflow: auto;
  cursor: grab;
}
.em-viewport[data-panning='true'] {
  cursor: grabbing;
  user-select: none;
}
/* Scaled from its top-left, with its own box set to the scaled size — a transform does not change
   layout size, so without the explicit width/height the scroller would still think the canvas was
   its 100% self. */
.em-zoomed {
  transform-origin: 0 0;
}
.em-toolbar {
  display: flex;
  justify-content: flex-end;
  gap: 4px;
  padding: 4px 12px 0;
}

/* issue #194 — the filter bar. Same ink-and-opacity idiom as the zoom controls, so it reads as
   one toolbar in either host's theme rather than as a widget bolted on. */
.em-filterbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
  padding: 4px 12px 0;
  font-size: 11px;
  line-height: 16px;
}
.em-filter-search {
  min-width: 120px;
  padding: 1px 6px;
  border: 1px solid currentColor;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  line-height: 16px;
  opacity: 0.55;
}
.em-filter-search:focus {
  opacity: 1;
  outline: none;
}
.em-filter-domains {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.em-filter-chip {
  padding: 1px 8px;
  border: 1px solid currentColor;
  border-radius: 999px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  line-height: 16px;
  opacity: 0.55;
  cursor: pointer;
}
.em-filter-chip:hover {
  opacity: 1;
}
/* An active chip inverts rather than merely brightening: at 106 slices a reader needs to see
   what is ON at a glance, and "slightly less faded" is not a state anyone can read. */
.em-filter-chip[data-on='true'] {
  opacity: 1;
  background: currentColor;
}
.em-filter-chip[data-on='true'] {
  color: inherit;
}
.em-filter-chip[data-on='true']::after {
  content: '';
}
.em-filter-count {
  margin-left: auto;
  opacity: 0.55;
  font-variant-numeric: tabular-nums;
}
.em-filter-clear {
  padding: 1px 6px;
  border: 1px solid currentColor;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  line-height: 16px;
  opacity: 0.55;
  cursor: pointer;
}
.em-filter-clear:hover {
  opacity: 1;
}

.em-slice-focus {
  flex: 0 0 auto;
  padding: 0 3px;
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 12px;
  line-height: 1;
  opacity: 0.35;
  cursor: pointer;
  pointer-events: auto;
}
.em-slice-focus:hover,
.em-slice-focus[data-focused='true'] {
  opacity: 1;
}
/* At `overview` the header is the slice's whole identity and the controls are three unreadable
   pixels each, so they go with the rest of the chrome. */
.em-viewport[data-lod='overview'] .em-slice-focus {
  display: none;
}

.em-slice-collapse {
  flex: 0 0 auto;
  padding: 0 3px;
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  line-height: 1;
  opacity: 0.4;
  cursor: pointer;
}
.em-slice-collapse:hover {
  opacity: 1;
}
.em-zoom {
  min-width: 26px;
  padding: 1px 6px;
  border: 1px solid currentColor;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  line-height: 16px;
  opacity: 0.55;
  cursor: pointer;
}
.em-zoom:hover:not(:disabled) {
  opacity: 1;
}
.em-zoom:disabled {
  opacity: 0.25;
  cursor: default;
}
.em-zoom-level {
  min-width: 44px;
}
/* Gap and padding are bound from layout.ts's canvas constants, which is what canvasSize measures
   the zoom wrapper with — the same anti-drift rule as the card's type scale. */
.em-scroll {
  display: flex;
}
.em-lane-gutter {
  position: relative;
  flex-grow: 0;
  flex-shrink: 0;
}
.em-lane-label {
  position: absolute;
  display: flex;
  align-items: center;
  font-size: 12px;
  font-weight: 600;
  opacity: 0.7;
}
/* #299 — a stream's caption, under its lane's. Smaller and indented: it names a row inside a
   band, and a caption at the lane's own weight would read as a fifth lane. */
.em-stream-row-label {
  position: absolute;
  display: flex;
  align-items: center;
  padding-left: 12px;
  font-size: 11px;
  font-weight: 500;
  opacity: 0.6;
}
.em-stream-row-label[data-unassigned] {
  opacity: 0.35;
}
.em-lane-label[data-split] {
  align-items: flex-start;
}
.em-plot {
  position: relative;
  flex: 0 0 auto;
}
.em-lane-band {
  position: absolute;
  left: 0;
  border-top: 1px solid currentColor;
  opacity: 0.12;
}
.em-stream-band {
  position: absolute;
  left: 0;
  pointer-events: none;
}
.em-stream-band[data-alt] {
  background: currentColor;
  opacity: 0.035;
}
/* #298 — the wide arrow over a chapter's slices. Clipped to an arrowhead on the right because the
   timeline runs left to right and the band is a span of it, not a box around it. `currentColor` at
   low alpha so it reads in both consoles' palettes without a colour of its own. */
.em-chapter-band {
  position: absolute;
  display: flex;
  align-items: center;
  box-sizing: border-box;
  padding: 0 10px 2px 10px;
  border: none;
  background: currentColor;
  color: inherit;
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  line-height: 1;
  text-align: left;
  cursor: pointer;
  clip-path: polygon(0 0, calc(100% - 10px) 0, 100% 50%, calc(100% - 10px) 100%, 0 100%);
  opacity: 0.1;
  pointer-events: auto;
}
.em-chapter-band:hover,
.em-chapter-band[data-focused='true'] {
  opacity: 0.22;
}
.em-chapter-band[data-dimmed='true'] {
  opacity: 0.05;
}
.em-chapter-name {
  /* The band's fill is `currentColor` at low alpha, so the name has to be painted at full
     strength on its own: a child element with its own opacity, not the parent's. */
  position: relative;
  color: inherit;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
/* A band is a table-of-contents entry, so it is the one label that grows as everything else
   shrinks — at `overview` the chapter names are what the reader navigates by. */
.em-viewport[data-lod='overview'] .em-chapter-band {
  font-size: 24px;
}
.em-slice {
  position: absolute;
  top: 0;
  border-left: 1px dashed currentColor;
  opacity: 0.45;
  pointer-events: none;
}
.em-slice[data-outcome='failed'] {
  border-left-color: #e5484d;
  opacity: 1;
}
.em-slice[data-outcome='passed'] {
  border-left-color: #46a758;
  opacity: 1;
}
/* notRun IS the drift colour — a bound spec that has not run is a claim without evidence, and it
   should read that way on the canvas itself in every viewer, not only in a host's own chrome. */
.em-slice[data-outcome='notRun'] {
  border-left-color: #e8930c;
  opacity: 1;
}
/* The overlay itself stays pointer-inert so cards keep their clicks; the name is the one
   interactive part — the slice's drill-down handle. */
.em-slice-header {
  position: absolute;
  top: -2px;
  left: 4px;
  display: flex;
  align-items: center;
  gap: 6px;
}
.em-trigger-icon {
  flex: 0 0 auto;
  width: 12px;
  height: 12px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.3;
  stroke-linecap: round;
  stroke-linejoin: round;
  opacity: 0.6;
  pointer-events: auto;
}
.em-slice-name {
  /* flex-shrink with min-width: 0 is what lets the NAME give way to the badge rather than pushing
     it out of the column — the count is short and fixed, the name is not. */
  flex: 0 1 auto;
  min-width: 0;
  padding: 0;
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  white-space: nowrap;
  /* A long slice name used to run across its neighbour's column. It now ends in an ellipsis
     inside its own column, with the full name on the title (#180). */
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: pointer;
  pointer-events: auto;
}
.em-slice-specs {
  flex: 0 0 auto;
  padding: 0 5px;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
  border: 1px solid currentColor;
  border-radius: 8px;
  font-size: 10px;
  line-height: 15px;
  opacity: 0.75;
  white-space: nowrap;
  pointer-events: auto;
}
.em-slice-specs[data-outcome='passed'] {
  color: #46a758;
  opacity: 1;
}
.em-slice-specs[data-outcome='failed'] {
  color: #e5484d;
  opacity: 1;
}
/* notRun and none share the drift colour on purpose: a claim with no evidence and a claim with
   nothing to produce evidence are the same problem at different stages. */
.em-slice-specs[data-outcome='notRun'],
.em-slice-specs[data-outcome='none'] {
  color: #e8930c;
  opacity: 1;
}
/* jasperfx#703 — the ladder, as a SECOND channel. Fill colour is spoken for: it means the element
   KIND, and that agreement between viewers is the whole reason this package exists. So provenance
   rides a corner marker instead, and only the top rung gets one.

   Declared and Derived are deliberately left looking exactly as they did. An unattributed model
   reads Declared rather than absent, so fading it would fade most of a typical canvas — and the
   new information here is "production has SEEN this", not "this was only written down". */
.em-card[data-provenance='Observed']::after {
  content: '';
  position: absolute;
  top: 0;
  right: 0;
  border-width: 0 10px 10px 0;
  border-style: solid;
  border-color: transparent currentColor transparent transparent;
  opacity: 0.85;
}

/* jasperfx#704 — a source disagreement is a FINDING, not decoration, and it is worth more than the
   generic magenta sticky every other hotspot gets. A double outline in the hotspot colour reads as
   "two sources, one of them dropped" at a glance, and the two claims are on the tooltip. */
.em-card[data-hotspot-origin='SourceDisagreement'] {
  outline: 2px solid #e91e63;
  outline-offset: 2px;
  font-weight: 600;
}

/* The edge layer fills the plot and never intercepts a pointer — the cards are the interactive
   things, and a line crossing one must not steal its click. */
.em-edges {
  position: absolute;
  top: 0;
  left: 0;
  overflow: visible;
  pointer-events: none;
}
.em-edge {
  fill: none;
  stroke: currentColor;
  stroke-width: 1.5;
  stroke-linejoin: round;
  opacity: 0.45;
}

/* #295 — cross-slice links. Thinner and fainter than an edge at rest: an edge is a statement
   about ONE slice's internals and belongs with its cards, while these cross the whole board, and
   at 106 slices they would otherwise become the picture. */
.em-link {
  fill: none;
  stroke: currentColor;
  stroke-width: 1;
  stroke-linejoin: round;
  opacity: 0.28;
}
/* One glyph per kind, no labels — the far end's own label already says what travelled. */
.em-link[data-kind='EventConsumed'] {
  stroke-dasharray: 1 3;
}
.em-link[data-kind='ReadModelRead'] {
  stroke-dasharray: 6 3;
}
/* Lit by the selection: its links and their far ends come up, the rest stays where it was. */
.em-link[data-lit] {
  opacity: 0.9;
  stroke-width: 1.75;
}
.em-links[data-mode='selected'] .em-link {
  opacity: 0.9;
}

/* The origin chevron. Top-left of the card, small, and quiet until hovered — it is a navigation
   affordance on a card whose subject is its own label. */
.em-origin {
  position: absolute;
  top: 2px;
  left: 4px;
  max-width: calc(100% - 8px);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 9px;
  line-height: 1.1;
  opacity: 0.55;
  cursor: pointer;
}
.em-origin:hover,
.em-origin:focus-visible {
  opacity: 1;
  text-decoration: underline;
}
.em-viewport[data-lod='compact'] .em-origin,
.em-viewport[data-lod='overview'] .em-origin {
  display: none;
}

.em-card {
  position: absolute;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
  font: inherit;
  text-align: center;
  cursor: pointer;
  overflow: hidden;
  /* padding, font-size and line-height come from layout.ts as inline style: the column width was
     computed from that type scale, and a stylesheet holding a second opinion about it is a
     clipped label with no traceable symptom (#180). */
}
/* Wrap at the `<wbr>` opportunities, then clamp — a name too long even for the widened column
   ends in an ellipsis with its full text on the card's tooltip, rather than being cut mid-glyph
   by `overflow: hidden` as it was before #180. `anywhere` is the backstop for the one segment
   that is itself wider than the cap. */
/* bobcat#178 — a hotspot card is a finding, laid out as one: what kind, then (for a source
   disagreement) the role and the two claims with their rungs, kept above dropped. Left-aligned
   and stacked, because a centred sentence is what made it read as a malformed events list. */
.em-hotspot {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 1px;
  width: 100%;
  text-align: left;
  overflow: hidden;
}
.em-hotspot-origin {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.6px;
  text-transform: uppercase;
  opacity: 0.9;
}
.em-hotspot-role {
  font-size: 11px;
  font-weight: 600;
}
.em-hotspot-claim {
  /* stretch + min-width: 0 is what actually makes the ellipsis appear: a column flex item is
     shrink-to-fit by default, so a long claim sizes to its text and gets cut by the card's
     overflow with no ellipsis to say it had been. */
  align-self: stretch;
  min-width: 0;
  font-size: 10px;
  line-height: 1.3;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
/* The dropped claim is stated, not hidden — it is half the finding — but it reads as the loser:
   struck through and faded, where the kept one carries full weight. */
.em-hotspot-claim[data-claim='dropped'] {
  opacity: 0.7;
  text-decoration: line-through;
}
.em-hotspot-rung {
  font-weight: 700;
}
.em-hotspot-rung::after {
  content: ' ·';
}
.em-hotspot-text {
  font-size: 11px;
  line-height: 1.25;
  overflow-wrap: anywhere;
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  overflow: hidden;
}

/* The verb of a route, as a badge: three to seven characters of fixed vocabulary a reader
   recognises by shape, so it should not be competing with the path for the same line (#184). */
.em-route-method {
  display: inline-block;
  margin-right: 4px;
  padding: 0 4px;
  border-radius: 3px;
  border: 1px solid currentColor;
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.3px;
  /* Outlined in the card's own ink rather than filled: the fill means the element KIND, and a
     badge that painted over it would be spending the one channel this package guards. */
  opacity: 0.75;
}
.em-card-label {
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: var(--em-label-lines, 3);
  line-clamp: var(--em-label-lines, 3);
  overflow: hidden;
  overflow-wrap: anywhere;
}
.em-empty {
  padding: 24px;
  opacity: 0.6;
}

/* ------------------------------------------------------------------ #296: focus, LOD, minimap */

/* The breadcrumb sits at the LEFT of the toolbar and pushes the zoom controls right, because it
   is the answer to "where am I" and that question is read before any control is reached for. */
.em-crumbs {
  display: flex;
  align-items: center;
  gap: 3px;
  margin-right: auto;
  font-size: 11px;
  line-height: 16px;
}
.em-crumb {
  padding: 1px 4px;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  opacity: 0.6;
  cursor: pointer;
}
.em-crumb:hover:not(:disabled) {
  opacity: 1;
}
/* The last crumb is where you already are — stated, not offered. */
.em-crumb[data-current='true'] {
  opacity: 1;
  font-weight: 600;
  cursor: default;
}
.em-crumb-sep {
  opacity: 0.4;
}
.em-focus-clear {
  min-width: 22px;
}

/* Under the canvas, right-aligned, in normal flow — deliberately NOT floated over a corner of it.
   The viewport has no height of its own: it grows to whatever the scaled canvas needs, and at the
   25% floor a 106-slice model is only ~140px tall. A 72px overlay in that corner covers half the
   plot, which is what the first cut of this did. A strip below it can never hide a card. */
.em-minimap-holder {
  display: flex;
  justify-content: flex-end;
  padding: 4px 12px 0;
  opacity: 0.8;
}
.em-minimap-holder:hover {
  opacity: 1;
}

/* Dimming, not hiding. A focused neighbourhood only means something against the rest of the model
   — hide the others and the reader has lost the very context the focus was supposed to give. The
   dimmed slices keep their clicks, so stepping sideways is still one press away. */
.em-card[data-dimmed='true'] {
  opacity: 0.16;
}
.em-slice[data-dimmed='true'] {
  opacity: 0.12;
}
.em-viewport[data-focused='true'] .em-edge {
  opacity: 0.2;
}
.em-card[data-selected='true'] {
  outline: 2px solid currentColor;
  outline-offset: 2px;
}
.em-slice[data-selected='true'] {
  border-left-style: solid;
  opacity: 1;
}

/* The pattern, said once per column. Invisible until `overview`, where the cards have no text. */
.em-slice-pattern {
  display: none;
  flex: 0 0 auto;
  font-size: 11px;
  opacity: 0.6;
}

/* --- Level of detail -----------------------------------------------------------------------
   Everything below is CSS switching on one attribute, and that is the whole design (#296). A
   `v-if` per level would re-render 600 cards on every notch of a pinch, and — the part that
   actually matters — two consoles would each decide for themselves what "less" meant. An
   attribute set from the scale means they cannot disagree.

   `detail` (≥ 0.7) has no rules at all: it IS today's rendering, and saying so by omission is how
   it stays that way. */

/* compact (0.4–0.7): kind colour, short label, spec badge. The things that go are the ones a
   reader cannot resolve at this size anyway — a 12px glyph is four pixels of nothing, and a
   hotspot's sentence is a grey smear that still costs the card its whole box. */
.em-viewport[data-lod='compact'] .em-trigger-icon,
.em-viewport[data-lod='compact'] .em-route-method,
.em-viewport[data-lod='compact'] .em-hotspot-claim,
.em-viewport[data-lod='compact'] .em-hotspot-role,
.em-viewport[data-lod='compact'] .em-hotspot-text {
  display: none;
}
.em-viewport[data-lod='compact'] .em-card-label {
  -webkit-line-clamp: 1;
  line-clamp: 1;
}

/* overview (< 0.4): a card is a colour block. At 25% a 13px label renders at three pixels — it is
   not small text, it is texture — so the kind colour is the only thing still carrying meaning, and
   the slice's own name and pattern say what the column is. */
/* #299 — the stream captions go at `overview`, and only the tint separates the rows. They cannot
   be counter-scaled the way a column name is: the gutter is 132px wide, which is 33px at the 25%
   floor, and a caption drawn big enough to read there would spill across the plot. */
.em-viewport[data-lod='overview'] .em-stream-row-label {
  display: none;
}
.em-viewport[data-lod='overview'] .em-card-label,
.em-viewport[data-lod='overview'] .em-hotspot,
.em-viewport[data-lod='overview'] .em-trigger-icon,
.em-viewport[data-lod='overview'] .em-slice-collapse,
.em-viewport[data-lod='overview'] .em-slice-specs {
  display: none;
}
.em-viewport[data-lod='overview'] .em-slice-pattern {
  display: inline;
}
/* Counter-scaled, and laid over the top of its own column rather than above it.
   The header is INSIDE the transform, so at the 25% floor an 11px name draws at under three
   pixels — texture, not text. 40px survives the scale at ~10px on screen, which is the whole
   point of the level: the column's name is the only thing left saying what it is.
   Over the column and not above it because there is only `CANVAS_PADDING` (12px, three at this
   scale) of room above the plot, and a label hoisted into it is simply clipped — which is what
   the first cut of this did on the 106-slice model. */
.em-viewport[data-lod='overview'] .em-slice-header {
  top: 0;
  left: 2px;
  flex-direction: column;
  align-items: flex-start;
  gap: 0;
}
.em-viewport[data-lod='overview'] .em-slice-name {
  font-size: 40px;
  line-height: 42px;
  font-weight: 600;
  opacity: 0.9;
}
.em-viewport[data-lod='overview'] .em-slice-pattern {
  font-size: 28px;
  line-height: 30px;
  opacity: 0.55;
}
</style>
