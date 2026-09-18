import { requiredContentWidth } from './text'
import {
  LANE_ORDER,
  type EventModelDescriptor,
  type EventModelEdge,
  type EventModelElement,
  type EventModelLane,
  type EventModelLink,
  type EventModelSliceDescriptor,
  type TypeDescriptor
} from './types'

/**
 * Deterministic grid layout for an Event Model descriptor.
 *
 * Pure, synchronous, and free of any graph library — no elk, no worker, no measurement pass,
 * so the canvas renders in one pass and a test can assert exact coordinates. That determinism
 * is the point: the acceptance criterion for this package is that the same descriptor renders
 * identically in the Bobcat console and in CritterWatch, and "identically" is only checkable
 * if position is a function of the descriptor alone.
 *
 * The shape is the standard Event Modeling canvas: slices are vertical columns in declaration
 * order, lanes are horizontal bands in canonical top-to-bottom order, and an element sits in
 * the cell where its slice column meets its lane band. Several elements in one cell run
 * left-to-right, which is what widens a slice with three emitted events.
 */

export interface LayoutOptions {
  /**
   * Minimum card width in px, and the width every column keeps unless its own labels need more
   * (issue bobcat#180). Set `maxCardWidth` to the same value to pin every card to one width.
   */
  cardWidth?: number
  /**
   * Ceiling a column may grow to for a long label. Past it the card clamps the label to
   * {@link MAX_LABEL_LINES} lines with an ellipsis and leaves the full text on the tooltip —
   * one pathological route must not push every other column off the screen.
   */
  maxCardWidth?: number
  /** Card height in px. */
  cardHeight?: number
  /** Horizontal gap between cards inside a lane cell. */
  gapX?: number
  /** Vertical gap between lane bands. */
  gapY?: number
  /** Gap between slice columns, on top of the inter-card gap. */
  sliceGap?: number
  /** Slice names to collapse to a single placeholder column. */
  collapsedSlices?: ReadonlySet<string>

  /**
   * Slice names to omit from the layout entirely — no cards, no placeholder, no width.
   *
   * The distinction from {@link collapsedSlices} is the point (issue #194). A collapsed slice
   * keeps a {@link COLLAPSED_WIDTH} placeholder so it stays findable, which is what a reader
   * wants for one slice they put away. A hidden slice costs nothing at all, which is what a
   * reader needs at 106 slices — the measured CritterWatch model is ~10,000px wide at the 25%
   * zoom floor, and 106 placeholders would still be 5,000px of it.
   *
   * A name in both sets is hidden: hiding is the stronger statement, and rendering a placeholder
   * for something the reader filtered out would put back the width they asked to remove.
   */
  hiddenSlices?: ReadonlySet<string>

  /**
   * Draw the `EventStream` lane as one row per aggregate stream (issue #299). Default **true**.
   *
   * Canonical Event Modeling puts a stream on a line: two slices that write `Account` put their
   * events on the same horizontal row, and that they share a stream is then visible with no arrow
   * at all — which is the point, because a shared aggregate is not a cause→effect link and drawing
   * it as one would fan every event of an aggregate out to every slice on it.
   *
   * Set `false` to keep the single flat lane. A consumer that does that gets the pre-#299 picture
   * exactly; so does any model the split has nothing to say about (see {@link streamRowPlan}).
   */
  streamRows?: boolean
}

export interface LaidOutNode {
  id: string
  element: EventModelElement
  /** Slice this element belongs to. Edges never cross slices, but the viewer groups by it. */
  sliceName: string
  x: number
  y: number
  width: number
  height: number
}

/**
 * One row inside a lane band — an aggregate's stream, or the row for stream elements that are on
 * no stream at all (issue #299).
 *
 * Every lane has at least one, so a viewer never branches on whether rows exist; a lane that was
 * not split has exactly one unlabelled row spanning the whole band.
 */
export interface LaidOutLaneRow {
  /** Type identity of the aggregate whose stream this row is. `null` for the unlabelled row. */
  key: string | null
  /** Caption for the gutter — the aggregate's short name. `null` for the unlabelled row. */
  label: string | null
  /** Absolute plot-space top of the row. */
  y: number
  height: number
}

export interface LaidOutLane {
  lane: EventModelLane
  y: number
  height: number
  /**
   * The rows this band is divided into, top to bottom. Length 1 for every lane but a split
   * `EventStream` — and for that one too, whenever the split has nothing to say.
   */
  rows: LaidOutLaneRow[]
}

export interface LaidOutSlice {
  name: string
  descriptor: EventModelSliceDescriptor
  x: number
  width: number
  /** Width every card in this column was given — sized to the column's own labels (#180). */
  cardWidth: number
  collapsed: boolean
}

/**
 * An edge with the polyline a viewer draws it as (issue #180's sibling, bobcat#181).
 *
 * Routed here rather than in the component for the same reason positions are: two viewers drawing
 * the same descriptor differently is the one thing this package exists to prevent, and a route is
 * as much a rendering claim as a coordinate. `points` is plot-space and always has at least two
 * entries; the last one is where the arrowhead goes.
 */
export interface LaidOutEdge extends EventModelEdge {
  points: ReadonlyArray<{ x: number; y: number }>
}

/**
 * A cross-slice link with the corridor route a viewer draws it as (issue #295).
 *
 * Routed here for the same reason an edge is: two viewers drawing one descriptor differently is
 * what this package exists to prevent, and a route is a rendering claim. `points` is plot-space,
 * always four entries (or two when the ends share an x), and the last is where the arrowhead goes.
 */
export interface LaidOutLink extends EventModelLink {
  points: ReadonlyArray<{ x: number; y: number }>
  /** Which horizontal lane-gap track this link runs along. Links sharing a source share a track. */
  track: number
  /** Plot-space y of that track — the horizontal run's own line. */
  trackY: number
}

/**
 * A chapter band — the wide arrow eventmodelers.ai draws over the slices of one chapter (issue
 * #298). One per CONTIGUOUS run of drawn slices sharing a chapter, in declaration order.
 *
 * The canvas never reorders slices: declaration order is the producer's statement about sequence,
 * so a chapter whose slices are interleaved with another's draws one band per run, each with the
 * same name. Slices with no chapter sit under no band. Hidden slices are not drawn, so they are not
 * in any band either — filter a chapter's middle slice away and its two neighbours become one run.
 */
export interface LaidOutChapterBand {
  name: string
  x: number
  width: number
  y: number
  height: number
  /** The drawn slices under this band, left to right. */
  slices: string[]
}

export interface EventModelGraph {
  nodes: LaidOutNode[]
  edges: LaidOutEdge[]
  /** Cross-slice links, routed through the lane gaps. Empty unless the descriptor carries them. */
  links: LaidOutLink[]
  lanes: LaidOutLane[]
  slices: LaidOutSlice[]
  /** Chapter bands above the slice columns (#298). Empty when no drawn slice declares a chapter. */
  chapters: LaidOutChapterBand[]
  /**
   * Vertical room the chapter bands take above the first lane: {@link CHAPTER_BAND_HEIGHT} when any
   * drawn slice has a chapter, else 0 — so an unchaptered model is coordinate-identical to before.
   */
  chapterBandHeight: number
  width: number
  height: number
}

const DEFAULTS = {
  cardWidth: 180,
  maxCardWidth: 320,
  cardHeight: 72,
  gapX: 24,
  gapY: 48,
  sliceGap: 56
} as const

/** Width of the placeholder a collapsed slice column occupies. */
export const COLLAPSED_WIDTH = 48

/**
 * The card's own type scale and box, owned here rather than in the stylesheet (#180).
 *
 * A width computed from one font size and rendered at another is a clipped label with no
 * symptom anyone can trace, so `EventModelView` writes these onto the card as inline style
 * instead of letting CSS hold a second opinion.
 */
export const LABEL_FONT_SIZE = 13
export const LABEL_LINE_HEIGHT = 1.25
export const CARD_PADDING_X = 8
/** Lines a column is *sized* for; see `requiredContentWidth`. */
export const LABEL_TARGET_LINES = 2
/** Lines a card will *render* before clamping with an ellipsis. 3 × 1.25 × 13px + padding < 72. */
export const MAX_LABEL_LINES = 3

/**
 * The canvas chrome around the plot, here for the same reason the type scale is (#182): the zoom
 * wrapper has to know the *unscaled* size of everything it scales, and a stylesheet holding a
 * second opinion about the gutter's width is a scrollbar that stops half a lane-label short.
 */
export const GUTTER_WIDTH = 132
export const GUTTER_GAP = 12
export const CANVAS_PADDING = 12

/**
 * Height of the chapter band strip above the lanes (issue #298). Tall enough for one line of the
 * chapter's name at `detail`; at `overview` the stylesheet lets the name run larger, because a band
 * is the one thing still legible when the cards are colour blocks.
 */
export const CHAPTER_BAND_HEIGHT = 28

/** Overall size of the drawn canvas, chrome included — what a zoom wrapper scales. */
export function canvasSize(graph: EventModelGraph): { width: number; height: number } {
  return {
    width: 2 * CANVAS_PADDING + GUTTER_WIDTH + GUTTER_GAP + graph.width,
    height: 2 * CANVAS_PADDING + graph.height
  }
}

/**
 * The width the cards in one column get: the widest label's requirement, held between the
 * caller's floor and ceiling. Pure — it estimates from the label text, never measures the DOM.
 *
 * A `Hotspot` label is excluded from the vote. The producer projects each hotspot into an element
 * whose label IS the hotspot text (jasperfx#704), so it is a whole sentence — *"EmittedEvents:
 * Derived claims ClaimResult; Declared claims NodeClaimed, ClaimRenewed"* — and letting prose set
 * the width of a column of type names widens every column on a real canvas to fit the finding
 * rather than the model. The sticky still wraps and clamps inside whatever width it is given, with
 * its full text on the tooltip, which is where a sentence belongs.
 */
function cardWidthFor(
  elements: readonly EventModelElement[],
  minCardWidth: number,
  maxCardWidth: number
): number {
  let needed = minCardWidth
  for (const element of elements) {
    if (element.kind === 'Hotspot') continue
    const content = requiredContentWidth(element.label ?? '', LABEL_FONT_SIZE, LABEL_TARGET_LINES)
    needed = Math.max(needed, Math.ceil(content) + 2 * CARD_PADDING_X)
  }
  return Math.min(needed, maxCardWidth)
}

/**
 * The polyline joining two cards.
 *
 * Two cases, because an Event Modeling canvas has exactly two kinds of relationship and they read
 * differently. Along a lane — command → handler → aggregate — the flow is left to right, so a
 * straight horizontal line between the facing edges is the whole story. Across lanes — command →
 * event, event → projection — the line has to cross a lane gap, and an orthogonal elbow through
 * the midpoint of that gap keeps it legible where a diagonal would cut through the band divider at
 * an arbitrary angle and read as a different kind of statement.
 *
 * Both cases are direction-aware: an edge pointing back up (or left) leaves the *other* side of
 * its source, so the arrowhead never lands on the face it started from.
 */
function routeEdge(from: LaidOutNode, to: LaidOutNode): { x: number; y: number }[] {
  const fromMidY = from.y + from.height / 2
  const toMidY = to.y + to.height / 2

  if (from.y === to.y) {
    return to.x >= from.x
      ? [
          { x: from.x + from.width, y: fromMidY },
          { x: to.x, y: toMidY }
        ]
      : [
          { x: from.x, y: fromMidY },
          { x: to.x + to.width, y: toMidY }
        ]
  }

  const downward = to.y > from.y
  const startY = downward ? from.y + from.height : from.y
  const endY = downward ? to.y : to.y + to.height
  const gapMidY = (startY + endY) / 2
  const fromMidX = from.x + from.width / 2
  const toMidX = to.x + to.width / 2

  const start = { x: fromMidX, y: startY }
  const end = { x: toMidX, y: endY }
  // A card sitting directly above or below its partner wants one straight drop, not an elbow with
  // two zero-length legs.
  if (fromMidX === toMidX) return [start, end]

  return [start, { x: fromMidX, y: gapMidY }, { x: toMidX, y: gapMidY }, end]
}

/** The lane the stream rows divide. Named because three separate decisions below key on it. */
const STREAM_LANE: EventModelLane = 'EventStream'

/** Wire identity of a type: the full name when the producer sent one, else the short name. */
function typeKey(type: TypeDescriptor | null | undefined): string | null {
  return type?.fullName ?? type?.name ?? null
}

/** The aggregates a slice writes through, in declaration order, as `{ key, label }`. */
function aggregatesOf(slice: EventModelSliceDescriptor): { key: string; label: string }[] {
  const found: { key: string; label: string }[] = []

  // `aggregateTypes` is the producer's own statement and is preferred; the Aggregate elements are
  // the same claim projected into cards, and are all a descriptor below JasperFx.Events 2.60 has.
  for (const type of slice.aggregateTypes ?? []) {
    const key = typeKey(type)
    if (key) found.push({ key, label: type.name ?? key })
  }
  if (found.length > 0) return found

  for (const element of slice.elements ?? []) {
    if (element.kind !== 'Aggregate') continue
    const key = typeKey(element.type) ?? element.label
    if (key) found.push({ key, label: element.type?.name ?? element.label })
  }
  return found
}

/**
 * The rows the `EventStream` lane is divided into, and which row each element of it belongs on.
 *
 * Pure and exported because it is a claim about the MODEL, not about the picture: a host that draws
 * its own legend, and a test that wants to say "these two slices are on one stream", should ask the
 * same function the layout asks rather than re-deriving the rule.
 *
 * Three rules, and the first is the one that keeps every existing canvas where it was:
 *
 * 1. **Fewer than two aggregates in view ⇒ one flat row.** A row is a comparison; with one stream
 *    there is nothing to compare and the split would only cost height. This is also why filtering
 *    a 106-slice model down to one aggregate collapses the lane back — rows are computed over the
 *    slices actually being drawn, not over the whole descriptor.
 * 2. **An event sits on its slice's aggregate.** With several, the aggregate whose `appliedEvents`
 *    name it wins; with no answer there (a producer that could not resolve the apply set statically
 *    emits none) it falls back to the slice's first aggregate, which is the claim the slice made
 *    first.
 * 3. **A `Message` is on no stream, and neither is an event whose slice names no aggregate.** They
 *    share the trailing unlabelled row. One row, one meaning — "in the stream lane, on no stream" —
 *    rather than two rows that would both be captioned by their absence.
 */
export interface StreamRowPlan {
  /** Rows top to bottom. Always at least one. */
  rows: { key: string | null; label: string | null }[]
  /** Row index by element id. Anything absent belongs to row 0, which is the flat-lane case. */
  rowByElementId: Map<string, number>
}

export function streamRowPlan(
  descriptor: EventModelDescriptor | null | undefined,
  options: { collapsedSlices?: ReadonlySet<string>; hiddenSlices?: ReadonlySet<string> } = {}
): StreamRowPlan {
  const flat: StreamRowPlan = { rows: [{ key: null, label: null }], rowByElementId: new Map() }

  const collapsed = options.collapsedSlices ?? new Set<string>()
  const hidden = options.hiddenSlices ?? new Set<string>()
  const drawn = (descriptor?.slices ?? []).filter((s) => !hidden.has(s.name) && !collapsed.has(s.name))

  const perSlice = drawn.map((slice) => ({ slice, aggregates: aggregatesOf(slice) }))
  const used = new Set<string>()
  for (const entry of perSlice) for (const aggregate of entry.aggregates) used.add(aggregate.key)
  if (used.size < 2) return flat

  // Model order first — `aggregates` is the model's own list, and first appearance there is the
  // order a reader of the document would expect — then anything only a slice mentioned.
  const labels = new Map<string, string>()
  const order: string[] = []
  const take = (key: string, label: string) => {
    if (!used.has(key) || labels.has(key)) return
    labels.set(key, label)
    order.push(key)
  }
  for (const aggregate of descriptor?.aggregates ?? []) {
    const key = typeKey(aggregate.type)
    if (key) take(key, aggregate.type?.name ?? key)
  }
  for (const entry of perSlice) for (const aggregate of entry.aggregates) take(aggregate.key, aggregate.label)

  const applied = new Map<string, Set<string>>()
  for (const aggregate of descriptor?.aggregates ?? []) {
    const key = typeKey(aggregate.type)
    if (!key) continue
    const names = applied.get(key) ?? new Set<string>()
    for (const event of aggregate.appliedEvents ?? []) {
      if (event?.fullName) names.add(event.fullName)
      if (event?.name) names.add(event.name)
    }
    applied.set(key, names)
  }

  // Assign by KEY first and resolve to indices afterwards: whether the unlabelled row exists at all
  // is only known once every element has been asked.
  const assigned = new Map<string, string | null>()
  let unlabelled = false
  for (const { slice, aggregates } of perSlice) {
    for (const element of slice.elements ?? []) {
      if (element.lane !== STREAM_LANE) continue

      let key: string | null = null
      if (element.kind !== 'Message' && aggregates.length > 0) {
        key = aggregates[0].key
        if (aggregates.length > 1) {
          // `label` is the last candidate on purpose: a producer that omits `type` still labels an
          // event card with its short type name, and largeModel-shaped descriptors do exactly that.
          const candidates = [element.type?.fullName, element.type?.name, element.label]
          const match = aggregates.find((aggregate) =>
            candidates.some((name) => !!name && applied.get(aggregate.key)?.has(name))
          )
          if (match) key = match.key
        }
      }

      if (key === null) unlabelled = true
      assigned.set(element.id, key)
    }
  }

  const rows: StreamRowPlan['rows'] = order.map((key) => ({ key, label: labels.get(key) ?? key }))
  if (unlabelled) rows.push({ key: null, label: null })

  const indexOf = new Map<string | null, number>(rows.map((row, index) => [row.key, index]))
  const rowByElementId = new Map<string, number>()
  for (const [id, key] of assigned) rowByElementId.set(id, indexOf.get(key) ?? 0)

  return { rows, rowByElementId }
}

/**
 * How far inside a lane gap the first corridor track sits, and how far apart tracks are.
 *
 * Both are small enough that several tracks fit in one `gapY`, and the router clamps rather than
 * overflowing into a card row: a corridor that leaves its gap would cross the thing the whole
 * design is built to avoid.
 */
export const TRACK_INSET = 10
export const TRACK_SPACING = 7

/**
 * Route the cross-slice links through the lane gaps (issue #295, design §2).
 *
 * <b>Why a corridor at all.</b> The grid gives empty horizontal bands between card rows for free.
 * A link that leaves its source vertically, runs horizontally inside one of those bands, and rises
 * or drops into its target never crosses a card on the way — which a straight line between two
 * cards 3,000px apart cannot promise.
 *
 * <b>Bundled by source, which is the single biggest clutter reduction.</b> Every link leaving one
 * element shares one track, so four consumers of an event are four branches off one line rather
 * than four lines. The trunk *is* the event stream leaving the fact, which is also how a reader
 * already thinks about it.
 *
 * <b>Tracks are first-fit over x-intervals, left to right.</b> Two bundles whose horizontal runs
 * overlap get different tracks and cannot overprint; two that do not overlap share one, so a wide
 * model does not accumulate a track per link.
 *
 * ⚠️ <b>The honest limit.</b> A link between lanes that are not adjacent runs its corridor in the
 * gap beside the SOURCE, so its far vertical leg passes the rows in between at the target's x. The
 * corridor never crosses a card; that one leg may. Fixing it properly means routing through every
 * intervening gap, which is a materially bigger router for a case that is rare on a real board.
 */
function routeLinks(
  links: readonly EventModelLink[],
  placed: Map<string, LaidOutNode>,
  gapY: number
): LaidOutLink[] {
  // Group by source element: one trunk, N branches. Insertion order is the descriptor's, which is
  // deterministic upstream, so the tracks a model gets are stable between runs.
  const bundles = new Map<string, EventModelLink[]>();
  for (const link of links) {
    // A link whose ends are not both drawn is dropped rather than pointed at the origin — the same
    // rule a dangling edge gets, and the case a hidden or collapsed slice produces constantly.
    if (!placed.has(link.fromElementId) || !placed.has(link.toElementId)) continue;

    const bundle = bundles.get(link.fromElementId);
    if (bundle) bundle.push(link);
    else bundles.set(link.fromElementId, [link]);
  }

  // Allocated x-intervals per gap, so first-fit can ask "does this bundle overlap anything already
  // on that track". Keyed by the gap's top y, which identifies it uniquely.
  const taken = new Map<number, { from: number; to: number }[][]>();
  const routed: LaidOutLink[] = [];

  for (const [fromId, bundle] of bundles) {
    const source = placed.get(fromId)!;
    const targets = bundle.map((link) => placed.get(link.toElementId)!);

    // The corridor sits on the side of the source the targets are on. Ties and same-row targets
    // use the gap below, which is the design's default.
    const sourceMid = source.y + source.height / 2;
    const below = targets.some((target) => target.y + target.height / 2 >= sourceMid);
    const gapTop = below ? source.y + source.height : source.y - gapY;

    const sourceX = source.x + source.width / 2;
    const xs = [sourceX, ...targets.map((target) => target.x + target.width / 2)];
    const span = { from: Math.min(...xs), to: Math.max(...xs) };

    const lanesInGap = taken.get(gapTop) ?? [];
    let track = lanesInGap.findIndex(
      (occupants) => !occupants.some((other) => other.from <= span.to && span.from <= other.to)
    );
    if (track < 0) {
      track = lanesInGap.length;
      lanesInGap.push([]);
    }
    lanesInGap[track].push(span);
    taken.set(gapTop, lanesInGap);

    // Clamp inside the gap: a track that escaped it would run across a card row.
    const offset = Math.min(TRACK_INSET + track * TRACK_SPACING, gapY - TRACK_INSET);
    const trackY = below ? gapTop + offset : gapTop + gapY - offset;

    for (let i = 0; i < bundle.length; i++) {
      const link = bundle[i];
      const target = targets[i];
      const targetX = target.x + target.width / 2;
      const start = { x: sourceX, y: below ? source.y + source.height : source.y };
      const end = {
        x: targetX,
        y: target.y + target.height / 2 >= trackY ? target.y : target.y + target.height
      };

      routed.push({
        ...link,
        track,
        trackY,
        points:
          sourceX === targetX
            ? [start, end]
            : [start, { x: sourceX, y: trackY }, { x: targetX, y: trackY }, end]
      });
    }
  }

  return routed;
}

export function layoutEventModel(
  descriptor: EventModelDescriptor | null | undefined,
  options: LayoutOptions = {}
): EventModelGraph {
  const minCardWidth = options.cardWidth ?? DEFAULTS.cardWidth
  const maxCardWidth = Math.max(minCardWidth, options.maxCardWidth ?? DEFAULTS.maxCardWidth)
  const cardHeight = options.cardHeight ?? DEFAULTS.cardHeight
  const gapX = options.gapX ?? DEFAULTS.gapX
  const gapY = options.gapY ?? DEFAULTS.gapY
  const sliceGap = options.sliceGap ?? DEFAULTS.sliceGap
  const collapsed = options.collapsedSlices ?? new Set<string>()
  const hidden = options.hiddenSlices ?? new Set<string>()

  // A row, not a lane, is now the unit of vertical space: every lane is one row tall except an
  // EventStream lane the plan split, and the flat plan makes that case identical to the old one.
  const rowHeight = cardHeight + gapY
  const plan =
    options.streamRows === false
      ? ({ rows: [{ key: null, label: null }], rowByElementId: new Map<string, number>() } as StreamRowPlan)
      : streamRowPlan(descriptor, { collapsedSlices: collapsed, hiddenSlices: hidden })

  // Chapters (#298): the bands need a strip above the first lane, and only when a drawn slice has
  // a chapter — otherwise every y is exactly what it was, which is what keeps `layout.spec.ts`'s
  // coordinate pins for unchaptered models honest.
  const drawnSlices = (descriptor?.slices ?? []).filter((s) => !hidden.has(s.name))
  const chapterBandHeight = drawnSlices.some((s) => !!s.chapter) ? CHAPTER_BAND_HEIGHT : 0

  const lanes: LaidOutLane[] = []
  let laneY = chapterBandHeight
  for (const lane of LANE_ORDER) {
    const rows = lane === STREAM_LANE ? plan.rows : [{ key: null, label: null }]
    const height = rows.length * rowHeight
    lanes.push({
      lane,
      y: laneY,
      height,
      rows: rows.map((row, index) => ({
        key: row.key,
        label: row.label,
        y: laneY + index * rowHeight,
        height: rowHeight
      }))
    })
    laneY += height
  }
  const laneTop = new Map(lanes.map((l) => [l.lane, l.y]))

  const nodes: LaidOutNode[] = []
  const slices: LaidOutSlice[] = []
  const edges: LaidOutEdge[] = []
  // Model-wide, unlike the per-slice map the edges use: a link is the one relationship that
  // crosses slices, which is the whole reason it is computed upstream rather than here (#295).
  const placedAcross = new Map<string, LaidOutNode>()

  let cursorX = 0

  for (const slice of descriptor?.slices ?? []) {
    // Hidden slices contribute nothing — not a node, not a placeholder, not a gap. Skipping
    // before the cursor moves is what makes the canvas actually narrower.
    if (hidden.has(slice.name)) continue

    const isCollapsed = collapsed.has(slice.name)
    const elements = isCollapsed ? [] : (slice.elements ?? [])

    // Group by lane, preserving declaration order inside each lane. Declaration order is the
    // producer's statement about sequence (a command before the events it emits), so sorting
    // here would discard information the descriptor deliberately carries.
    // Laid-out nodes of THIS slice, by element id — what the slice's own edges are routed
    // against. Per slice because ids are unique per slice and an edge never crosses one.
    const placed = new Map<string, LaidOutNode>()

    // Cells, not lanes: two events of one slice on different streams are in different cells and
    // each starts again at the column's left edge, which is what puts them under each other.
    const byCell = new Map<string, { lane: EventModelLane; row: number; elements: EventModelElement[] }>()
    for (const element of elements) {
      const row = element.lane === STREAM_LANE ? (plan.rowByElementId.get(element.id) ?? 0) : 0
      const key = `${element.lane}#${row}`
      const cell = byCell.get(key)
      if (cell) cell.elements.push(element)
      else byCell.set(key, { lane: element.lane, row, elements: [element] })
    }

    // One card width per slice column, sized to that column's own labels (#180). Per column and
    // not per card: cards in a column line up under each other, and a lane of ragged widths reads
    // as a broken grid rather than as "this name is longer". Neighbouring columns may differ —
    // they are separated by a slice divider, which is where a width change is legible.
    const cardWidth = isCollapsed ? minCardWidth : cardWidthFor(elements, minCardWidth, maxCardWidth)

    const widest = Math.max(1, ...[...byCell.values()].map((c) => c.elements.length))
    const sliceWidth = isCollapsed
      ? COLLAPSED_WIDTH
      : widest * cardWidth + (widest - 1) * gapX

    for (const cell of byCell.values()) {
      const top = laneTop.get(cell.lane)
      // A lane the contract does not know about is dropped rather than stacked at y=0, where it
      // would silently overlap the wireframe lane and read as a rendering bug rather than as
      // "this descriptor came from a newer JasperFx than this package".
      if (top === undefined) continue

      cell.elements.forEach((element, index) => {
        const node: LaidOutNode = {
          id: element.id,
          element,
          sliceName: slice.name,
          x: cursorX + index * (cardWidth + gapX),
          y: top + cell.row * rowHeight + gapY / 2,
          width: cardWidth,
          height: cardHeight
        }
        nodes.push(node)
        placed.set(node.id, node)
        placedAcross.set(node.id, node)
      })
    }

    slices.push({
      name: slice.name,
      descriptor: slice,
      x: cursorX,
      width: sliceWidth,
      cardWidth,
      collapsed: isCollapsed
    })

    if (!isCollapsed) {
      // Edges reference elements by id and never cross a slice, so an edge whose endpoints are
      // not both present is dropped. A dangling edge is a producer bug; rendering it as a line
      // to the origin would make it look like a modelling statement.
      for (const edge of slice.edges ?? []) {
        const from = placed.get(edge.fromId)
        const to = placed.get(edge.toId)
        // An element in an unknown lane was dropped above, so "placed" — not "declared" — is the
        // right test: an edge to a card nobody drew has nowhere to point either.
        if (from && to) edges.push({ ...edge, points: routeEdge(from, to) })
      }
    }

    cursorX += sliceWidth + sliceGap
  }

  return {
    nodes,
    edges,
    links: routeLinks(descriptor?.links ?? [], placedAcross, gapY),
    lanes,
    slices,
    chapters: chapterBands(slices, chapterBandHeight),
    chapterBandHeight,
    width: Math.max(0, cursorX - sliceGap),
    height: laneY
  }
}

/**
 * One band per contiguous run of drawn slices sharing a chapter (#298). Over the laid-out slices
 * rather than the descriptor, so a hidden slice neither breaks a run nor stretches a band over the
 * gap where it would have been.
 */
function chapterBands(slices: readonly LaidOutSlice[], height: number): LaidOutChapterBand[] {
  if (height <= 0) return []
  const bands: LaidOutChapterBand[] = []
  let open: LaidOutChapterBand | null = null

  for (const slice of slices) {
    const chapter = slice.descriptor.chapter ?? null
    if (chapter === null) {
      open = null
      continue
    }
    if (open && open.name === chapter) {
      open.width = slice.x + slice.width - open.x
      open.slices.push(slice.name)
      continue
    }
    open = { name: chapter, x: slice.x, width: slice.width, y: 0, height, slices: [slice.name] }
    bands.push(open)
  }

  return bands
}
