import { describe, expect, it } from 'vitest'
import { CHAPTER_BAND_HEIGHT, COLLAPSED_WIDTH, TRACK_INSET, TRACK_SPACING, layoutEventModel, streamRowPlan } from '../layout'
import { LANE_ORDER } from '../types'
import { chapteredModel, largeModel, linkedThreeSliceModel, twoAggregateModel, withdrawFundsModel } from './fixtures'

/** The withdrawal model with one label replaced — the #180 sizing cases in one place. */
function longLabelModel(label: string) {
  const model = withdrawFundsModel()
  model.slices![0].elements = [
    { id: 'WithdrawFunds/Trigger/long', kind: 'Trigger', lane: 'Wireframe', label }
  ]
  model.slices![0].edges = []
  return model
}

/**
 * The layout is the part that has to be *identical* between the Bobcat console and CritterWatch,
 * so it is pure and asserted on exact coordinates rather than on "looks about right".
 */
describe('layoutEventModel', () => {
  it('places every element of every slice', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    expect(graph.nodes).toHaveLength(9)
    expect(graph.slices.map((s) => s.name)).toEqual(['WithdrawFunds', 'AccountBalance'])
  })

  it('always emits the four canonical lanes, top to bottom, even when a lane is empty', () => {
    // The AccountBalance slice has nothing in the Wireframe or Command lanes; the bands still
    // exist, because a canvas that reflows its lanes per slice is unreadable.
    const graph = layoutEventModel(withdrawFundsModel())
    expect(graph.lanes.map((l) => l.lane)).toEqual([...LANE_ORDER])
    expect(graph.lanes.map((l) => l.y)).toEqual([0, 120, 240, 360])
  })

  it('puts an element in the band for its lane', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const y = (id: string) => graph.nodes.find((n) => n.id === id)!.y
    expect(y('WithdrawFunds/Trigger/Teller screen')).toBe(24)
    expect(y('WithdrawFunds/Command/Bank.WithdrawFunds')).toBe(144)
    expect(y('WithdrawFunds/Event/Bank.FundsWithdrawn')).toBe(264)
    expect(y('AccountBalance/ReadModel/Bank.Balance')).toBe(384)
  })

  it('runs several elements in one lane cell left to right', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const xs = graph.nodes
      .filter((n) => n.sliceName === 'WithdrawFunds' && n.element.lane === 'Command')
      .map((n) => n.x)
    expect(xs).toEqual([0, 204, 408])
  })

  it('preserves declaration order inside a lane rather than sorting', () => {
    // Declaration order is the producer's statement about sequence — a command before the events
    // it emits. Sorting by label would silently discard that.
    const graph = layoutEventModel(withdrawFundsModel())
    const labels = graph.nodes
      .filter((n) => n.sliceName === 'WithdrawFunds' && n.element.lane === 'EventStream')
      .sort((a, b) => a.x - b.x)
      .map((n) => n.element.label)
    expect(labels).toEqual(['FundsWithdrawn', 'AccountOverdrawn'])
  })

  it('sizes a slice column to its widest lane and offsets the next slice past it', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    // WithdrawFunds' widest lane holds 3 cards: 3*180 + 2*24 = 588.
    expect(graph.slices[0].width).toBe(588)
    expect(graph.slices[1].x).toBe(588 + 56)
  })

  it('is deterministic — the same descriptor lays out identically twice', () => {
    // This is the package's whole reason to exist: one descriptor, one picture, in two viewers.
    expect(layoutEventModel(withdrawFundsModel())).toEqual(layoutEventModel(withdrawFundsModel()))
  })

  it('drops an edge whose endpoints are not both present', () => {
    const model = withdrawFundsModel()
    model.slices![0].edges!.push({ fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds', toId: 'nope' })
    const graph = layoutEventModel(model)
    // A dangling edge is a producer bug; drawing a line to nowhere would read as a modelling claim.
    expect(graph.edges.some((e) => e.toId === 'nope')).toBe(false)
    expect(graph.edges).toHaveLength(4)
  })

  it('collapses a named slice to a placeholder and keeps the rest laid out', () => {
    const graph = layoutEventModel(withdrawFundsModel(), {
      collapsedSlices: new Set(['WithdrawFunds'])
    })
    expect(graph.slices[0].collapsed).toBe(true)
    expect(graph.slices[0].width).toBe(COLLAPSED_WIDTH)
    expect(graph.nodes.every((n) => n.sliceName === 'AccountBalance')).toBe(true)
    expect(graph.slices[1].x).toBe(COLLAPSED_WIDTH + 56)
  })

  it('ignores an element in a lane this package does not know', () => {
    // A descriptor from a newer JasperFx must not silently stack an unknown lane at y=0, where it
    // would overlap the wireframe lane and read as a rendering bug.
    const model = withdrawFundsModel()
    model.slices![0].elements!.push({
      id: 'WithdrawFunds/Event/Future',
      kind: 'Event',
      lane: 'Speculative' as never,
      label: 'Future'
    })
    expect(layoutEventModel(model).nodes.some((n) => n.id === 'WithdrawFunds/Event/Future')).toBe(false)
  })

  it('keeps the default width for ordinary type names', () => {
    // The floor is the common case: nothing on this fixture is long enough to want more room, so
    // the canvas looks exactly as it did before #180.
    const graph = layoutEventModel(withdrawFundsModel())
    expect(graph.slices.map((s) => s.cardWidth)).toEqual([180, 180])
    expect(graph.nodes.every((n) => n.width === 180)).toBe(true)
  })

  it('widens a column whose label will not fit two lines at the default width (#180)', () => {
    // The bug was an absolutely-sized card with overflow:hidden — a long route was cut off. The
    // card now wraps at its break opportunities first, and the column only grows when two lines
    // at 180px still are not enough.
    const model = longLabelModel('PUT /api/organizations/{orgId}/subscriptions/{id}/cancel')
    const graph = layoutEventModel(model)
    expect(graph.slices[0].cardWidth).toBe(192)
    expect(graph.nodes[0].width).toBe(192)
    // The neighbouring column keeps its own width, and is pushed right by exactly the difference.
    expect(graph.slices[1].cardWidth).toBe(180)
    expect(graph.slices[1].x).toBe(192 + 56)
  })

  it('caps a column at maxCardWidth rather than letting one label own the canvas', () => {
    const model = longLabelModel('RegisterCustomerAccountForInternationalWireTransferProcessing')
    expect(layoutEventModel(model).slices[0].cardWidth).toBe(214)
    // Past the cap the card clamps the label and keeps the full text on its tooltip.
    expect(layoutEventModel(model, { maxCardWidth: 200 }).slices[0].cardWidth).toBe(200)
  })

  it('pins every card to one width when maxCardWidth equals cardWidth', () => {
    // The escape hatch for a consumer that wants the old fixed grid back.
    const model = longLabelModel('RegisterCustomerAccountForInternationalWireTransferProcessing')
    const graph = layoutEventModel(model, { cardWidth: 180, maxCardWidth: 180 })
    expect(graph.slices.map((s) => s.cardWidth)).toEqual([180, 180])
  })

  it('does not let a hotspot sentence set the width of a column of type names', () => {
    // A hotspot's label IS its text (jasperfx#704) — prose, not a type name. Sizing to it widened
    // every column on a real canvas to fit the finding instead of the model.
    const model = longLabelModel('ClaimNode')
    model.slices![0].elements!.push({
      id: 'WithdrawFunds/Hotspot/disagreement',
      kind: 'Hotspot',
      lane: 'EventStream',
      label: 'EmittedEvents: Derived claims ClaimResult; Declared claims NodeClaimed, ClaimRenewed'
    })
    expect(layoutEventModel(model).slices[0].cardWidth).toBe(180)
  })

  it('routes an edge along a lane as a straight line between the facing card edges (#181)', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const edge = graph.edges.find(
      (e) => e.toId === 'WithdrawFunds/Handler/Bank.AccountHandler'
    )!
    // Command at x 0..180, handler at x 204..384, both mid-band at y 180.
    expect(edge.points).toEqual([
      { x: 180, y: 180 },
      { x: 204, y: 180 }
    ])
  })

  it('routes an edge across lanes as an elbow through the middle of the lane gap', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const edge = graph.edges.find((e) => e.toId === 'WithdrawFunds/Event/Bank.FundsWithdrawn')!
    // Handler bottom (216) down to the event's top (264), turning at the gap's midpoint. A
    // diagonal would cross the band divider at an arbitrary angle and read as a different claim.
    expect(edge.points).toEqual([
      { x: 294, y: 216 },
      { x: 294, y: 240 },
      { x: 90, y: 240 },
      { x: 90, y: 264 }
    ])
  })

  it('drops the elbow when one card sits directly above the other', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const edge = graph.edges.find(
      (e) => e.toId === 'AccountBalance/Projection/Bank.BalanceProjection'
    )!
    expect(edge.points).toEqual([
      { x: 734, y: 336 },
      { x: 734, y: 384 }
    ])
  })

  it('leaves the other face of the source when the edge points backwards', () => {
    // Declaration order is the producer's, and nothing says a slice declares its elements in
    // flow order — an edge pointing back up or left must not start on the face it ends at.
    const model = withdrawFundsModel()
    model.slices![0].edges = [
      {
        fromId: 'WithdrawFunds/Handler/Bank.AccountHandler',
        toId: 'WithdrawFunds/Command/Bank.WithdrawFunds'
      }
    ]
    const [edge] = layoutEventModel(model).edges
    expect(edge.points).toEqual([
      { x: 204, y: 180 },
      { x: 180, y: 180 }
    ])
  })

  it('handles a null or empty descriptor without throwing', () => {
    for (const empty of [null, undefined, { name: 'x' }, { name: 'x', slices: [] }]) {
      const graph = layoutEventModel(empty as never)
      expect(graph.nodes).toHaveLength(0)
      expect(graph.width).toBe(0)
      expect(graph.lanes).toHaveLength(4)
    }
  })
})

/**
 * bobcat#299 — the EventStream lane as one row per aggregate stream.
 *
 * Coordinates rather than structure, for the same reason the rest of this file asserts them: a
 * stream row is only worth anything if the Bobcat console and CritterWatch put the same event on
 * the same line, and "the same line" is a number.
 */
describe('stream rows', () => {
  it('splits the event stream lane into one row per aggregate, plus one for what is on no stream', () => {
    const plan = streamRowPlan(twoAggregateModel())
    expect(plan.rows.map((r) => r.label)).toEqual(['Account', 'Wallet', null])
  })

  it('grows the lane band by its rows and pushes the lanes below it down', () => {
    const graph = layoutEventModel(twoAggregateModel())
    const lane = (name: string) => graph.lanes.find((l) => l.lane === name)!
    // Three rows of (72 + 48).
    expect(lane('EventStream').height).toBe(360)
    expect(lane('EventStream').rows.map((r) => r.y)).toEqual([240, 360, 480])
    expect(lane('ReadModel').y).toBe(600)
    expect(graph.height).toBe(720)
    // Every other lane is still exactly one row, so a viewer never branches on whether rows exist.
    expect(lane('Command').rows).toHaveLength(1)
    expect(lane('Command').rows[0]).toEqual({ key: null, label: null, y: 120, height: 120 })
  })

  it('puts each slice’s events on the row of the aggregate it writes', () => {
    const graph = layoutEventModel(twoAggregateModel())
    const y = (id: string) => graph.nodes.find((n) => n.id === id)!.y
    expect(y('WithdrawFunds/Event/Bank.FundsWithdrawn')).toBe(264)
    expect(y('WithdrawFunds/Event/Bank.AccountOverdrawn')).toBe(264)
    expect(y('TopUpWallet/Event/Bank.WalletToppedUp')).toBe(384)
  })

  it('routes a multi-aggregate slice’s event by appliedEvents, and falls back to its first aggregate', () => {
    const graph = layoutEventModel(twoAggregateModel())
    const y = (id: string) => graph.nodes.find((n) => n.id === id)!.y
    // Wallet applies WalletToppedUp, so it lands on the Wallet row even though Account is declared
    // first on the slice.
    expect(y('SettleTransfer/Event/Bank.WalletToppedUp')).toBe(384)
    // Neither aggregate applies TransferSettled — the slice's first claim wins rather than the
    // event falling off the streams altogether.
    expect(y('SettleTransfer/Event/Bank.TransferSettled')).toBe(264)
  })

  it('puts a published message and an event whose slice names no aggregate on the unlabelled row', () => {
    const graph = layoutEventModel(twoAggregateModel())
    const y = (id: string) => graph.nodes.find((n) => n.id === id)!.y
    expect(y('TopUpWallet/Message/Bank.WalletTopUpNotified')).toBe(504)
    expect(y('AccountBalance/Event/Bank.FundsWithdrawn')).toBe(504)
  })

  it('starts each row at the column’s left edge rather than running the lane’s cards along one line', () => {
    const graph = layoutEventModel(twoAggregateModel())
    const node = (id: string) => graph.nodes.find((n) => n.id === id)!
    const slice = graph.slices.find((s) => s.name === 'TopUpWallet')!
    // The event and the message are in the same lane and different rows, so both start at x0.
    expect(node('TopUpWallet/Event/Bank.WalletToppedUp').x).toBe(slice.x)
    expect(node('TopUpWallet/Message/Bank.WalletTopUpNotified').x).toBe(slice.x)
    // …and the column is therefore one card wide, not two.
    expect(slice.width).toBe(slice.cardWidth * 2 + 24) // its Command lane still holds two cards
  })

  it('still terminates an edge on the card faces when the rows moved them apart', () => {
    const graph = layoutEventModel(twoAggregateModel())
    const edge = graph.edges.find(
      (e) => e.toId === 'WithdrawFunds/Event/Bank.FundsWithdrawn'
    )!
    const to = graph.nodes.find((n) => n.id === edge.toId)!
    expect(edge.points.at(-1)).toEqual({ x: to.x + to.width / 2, y: to.y })
  })

  it('leaves a model with fewer than two aggregates exactly where it was', () => {
    // The acceptance criterion of #299: one stream is not a comparison, so the split must cost a
    // one-aggregate canvas nothing at all — not a row, not a pixel.
    const split = layoutEventModel(withdrawFundsModel())
    const flat = layoutEventModel(withdrawFundsModel(), { streamRows: false })
    expect(split).toEqual(flat)
    expect(split.height).toBe(480)
  })

  it('honours streamRows: false on a model that would otherwise split', () => {
    const flat = layoutEventModel(twoAggregateModel(), { streamRows: false })
    expect(flat.lanes.every((l) => l.rows.length === 1)).toBe(true)
    expect(flat.height).toBe(480)
    expect(flat.nodes.find((n) => n.id === 'TopUpWallet/Event/Bank.WalletToppedUp')!.y).toBe(264)
  })

  it('collapses the lane back when the slices in view are down to one stream', () => {
    // Rows are computed over the slices actually drawn, so filtering a model to one aggregate does
    // not leave a lane of empty rows behind — which is the state a reader reaches by filtering.
    const graph = layoutEventModel(twoAggregateModel(), {
      hiddenSlices: new Set(['TopUpWallet', 'SettleTransfer'])
    })
    expect(graph.lanes.find((l) => l.lane === 'EventStream')!.rows).toHaveLength(1)
    expect(graph.height).toBe(480)
  })

  it('reads a slice’s aggregates off its Aggregate cards when the producer sends no aggregateTypes', () => {
    // Everything below JasperFx.Events 2.60 is this case, including descriptors already persisted.
    const model = twoAggregateModel()
    for (const slice of model.slices!) delete slice.aggregateTypes
    const plan = streamRowPlan(model)
    // SettleTransfer declared its two aggregates only through aggregateTypes and so drops to the
    // unlabelled row; the two slices that draw Aggregate cards still get their own.
    expect(plan.rows.map((r) => r.label)).toEqual(['Account', 'Wallet', null])
    expect(plan.rowByElementId.get('WithdrawFunds/Event/Bank.FundsWithdrawn')).toBe(0)
    expect(plan.rowByElementId.get('SettleTransfer/Event/Bank.TransferSettled')).toBe(2)
  })
})

describe('stream rows at fleet size', () => {
  it('splits a 106-slice model across its streams in one synchronous pass', () => {
    const graph = layoutEventModel(largeModel(106, 4))
    const stream = graph.lanes.find((l) => l.lane === 'EventStream')!
    expect(stream.rows.map((r) => r.label)).toEqual([
      'Aggregate0',
      'Aggregate1',
      'Aggregate2',
      'Aggregate3'
    ])
    // Four rows in the stream lane and one in each of the other three.
    expect(graph.height).toBe(840)
    // Every event landed on the row of the aggregate its slice writes — nothing fell through to an
    // unlabelled row, which is what an `appliedEvents` list this fixture leaves empty would cause
    // if the fallback were not "the slice's first aggregate".
    const events = graph.nodes.filter((n) => n.element.kind === 'Event')
    expect(events).toHaveLength(106)
    expect(new Set(events.map((n) => n.y))).toEqual(new Set([264, 384, 504, 624]))
  })
})

/**
 * bobcat#295 — the corridor router. Exact points, like every other coordinate here: a link is a
 * rendering claim, and "identical in both viewers" has to cover where it runs.
 */
describe('cross-slice links', () => {
  it('routes a link out of its source, along a gap track, and into its target', () => {
    const graph = layoutEventModel(linkedThreeSliceModel())
    const node = (id: string) => graph.nodes.find((n) => n.id === id)!

    const link = graph.links.find((l) => l.toSlice === 'AccountBalance')!
    const source = node('OpenAccount/Event/Bank.AccountOpened')
    const target = node('AccountBalance/Event/Bank.AccountOpened')

    // Four points: down out of the source, along the track, across, into the target.
    expect(link.points).toHaveLength(4)
    expect(link.points[0]).toEqual({ x: source.x + source.width / 2, y: source.y + source.height })
    expect(link.points[1]!.y).toBe(link.trackY)
    expect(link.points[2]!.y).toBe(link.trackY)
    expect(link.points[3]).toEqual({ x: target.x + target.width / 2, y: target.y + target.height })
  })

  it('runs the corridor INSIDE the lane gap, never across a card row', () => {
    // The property the whole design rests on: a link that left its gap would cross the cards the
    // corridor exists to avoid.
    const graph = layoutEventModel(linkedThreeSliceModel())
    const source = graph.nodes.find((n) => n.id === 'OpenAccount/Event/Bank.AccountOpened')!
    const gapTop = source.y + source.height

    for (const link of graph.links) {
      expect(link.trackY).toBeGreaterThanOrEqual(gapTop)
      expect(link.trackY).toBeLessThanOrEqual(gapTop + 48) // gapY
    }
  })

  it('bundles every link leaving one element onto one trunk and one track', () => {
    // Four consumers of an event are four branches off one line, not four lines — the biggest
    // clutter reduction in the design, and the thing a reader already believes about a stream.
    const graph = layoutEventModel(linkedThreeSliceModel())

    expect(graph.links).toHaveLength(2)
    expect(new Set(graph.links.map((l) => l.track))).toEqual(new Set([0]))
    expect(new Set(graph.links.map((l) => l.trackY)).size).toBe(1)
    // One trunk: both leave the same point.
    expect(graph.links[0]!.points[0]).toEqual(graph.links[1]!.points[0])
  })

  it('gives two bundles sharing a gap different tracks when their runs overlap', () => {
    const model = linkedThreeSliceModel()
    // A second source in the same row, routing DOWNWARD like the first so both land in the gap
    // below the event row. Its span sits inside the first bundle's, so they cannot share a track.
    model.links!.push({
      fromSlice: 'AccountBalance',
      fromElementId: 'AccountBalance/Event/Bank.AccountOpened',
      toSlice: 'AccountBalance',
      toElementId: 'AccountBalance/ReadModel/Bank.Balance',
      kind: 'ReadModelRead'
    })

    const graph = layoutEventModel(model)
    const bySource = new Map(graph.links.map((l) => [l.fromElementId, l]))
    const first = bySource.get('OpenAccount/Event/Bank.AccountOpened')!
    const second = bySource.get('AccountBalance/Event/Bank.AccountOpened')!

    // Same gap — so the tracks have to differ, and by exactly one spacing.
    expect(second.track).toBe(first.track + 1)
    expect(second.trackY - first.trackY).toBe(TRACK_SPACING)
  })

  it('lets two bundles share one track when their runs do not overlap', () => {
    // First-fit is what keeps a wide model from accumulating a track per link: two runs that
    // cannot collide belong on the same line. Both bundles below are in the SAME gap — each drops
    // from an event to a read model inside its own slice — so sharing is a real decision here
    // rather than an artefact of them being in different gaps.
    const model = linkedThreeSliceModel()
    model.slices![0]!.elements!.push({
      id: 'OpenAccount/ReadModel/Bank.AccountList',
      kind: 'ReadModel',
      lane: 'ReadModel',
      label: 'AccountList'
    })
    model.links = [
      {
        fromSlice: 'OpenAccount',
        fromElementId: 'OpenAccount/Event/Bank.AccountOpened',
        toSlice: 'OpenAccount',
        toElementId: 'OpenAccount/ReadModel/Bank.AccountList',
        kind: 'ReadModelRead'
      },
      {
        fromSlice: 'AccountBalance',
        fromElementId: 'AccountBalance/Event/Bank.AccountOpened',
        toSlice: 'AccountBalance',
        toElementId: 'AccountBalance/ReadModel/Bank.Balance',
        kind: 'ReadModelRead'
      }
    ]

    const graph = layoutEventModel(model)
    expect(graph.links).toHaveLength(2)

    // One gap, one track, two bundles — and their horizontal runs really are disjoint.
    expect(new Set(graph.links.map((l) => l.trackY)).size).toBe(1)
    expect(graph.links.every((l) => l.track === 0)).toBe(true)

    const spans = graph.links.map((l) => [
      Math.min(...l.points.map((p) => p.x)),
      Math.max(...l.points.map((p) => p.x))
    ])
    expect(spans[0]![1]).toBeLessThan(spans[1]![0])
  })

  it('drops a link whose end is hidden, leaving nothing dangling', () => {
    const graph = layoutEventModel(linkedThreeSliceModel(), {
      hiddenSlices: new Set(['SendWelcome'])
    })

    expect(graph.links).toHaveLength(1)
    expect(graph.links[0]!.toSlice).toBe('AccountBalance')
  })

  it('drops a collapsed slice’s links too, since a collapsed slice draws no cards', () => {
    const graph = layoutEventModel(linkedThreeSliceModel(), {
      collapsedSlices: new Set(['AccountBalance'])
    })

    expect(graph.links.map((l) => l.toSlice)).toEqual(['SendWelcome'])
  })

  it('carries no links at all for a descriptor that has none', () => {
    // Every producer below JasperFx.Events 2.69 — the field is absent, not empty.
    expect(layoutEventModel(withdrawFundsModel()).links).toEqual([])
  })

  it('starts the first track a fixed inset into the gap', () => {
    const graph = layoutEventModel(linkedThreeSliceModel())
    const source = graph.nodes.find((n) => n.id === 'OpenAccount/Event/Bank.AccountOpened')!

    expect(graph.links[0]!.trackY).toBe(source.y + source.height + TRACK_INSET)
  })
})

/**
 * Issue #298 — chapter bands. Over the LAID-OUT slices rather than the descriptor, one per
 * contiguous run, and only when a drawn slice actually declares a chapter.
 */
describe('chapter bands (#298)', () => {
  it('draws one band per contiguous run of one chapter, in declaration order, never reordering', () => {
    const graph = layoutEventModel(chapteredModel())

    // Onboarding, Onboarding, Swiping, Onboarding, (none): three bands, two of them Onboarding.
    expect(graph.chapters.map((b) => b.name)).toEqual(['Onboarding', 'Swiping', 'Onboarding'])
    expect(graph.chapters.map((b) => b.slices)).toEqual([['Enroll', 'AddDog'], ['SwipeOnDog'], ['Verify']])
  })

  it('spans exactly its slices\' columns', () => {
    const graph = layoutEventModel(chapteredModel())
    const [enroll, addDog] = graph.slices
    const onboarding = graph.chapters[0]

    expect(onboarding.x).toBe(enroll.x)
    expect(onboarding.width).toBe(addDog.x + addDog.width - enroll.x)
    expect(onboarding.y).toBe(0)
    expect(onboarding.height).toBe(CHAPTER_BAND_HEIGHT)
  })

  it('pushes the lanes down by the band height so the strip is real room, not an overlay', () => {
    const graph = layoutEventModel(chapteredModel())

    expect(graph.chapterBandHeight).toBe(CHAPTER_BAND_HEIGHT)
    expect(graph.lanes[0].y).toBe(CHAPTER_BAND_HEIGHT)
    expect(graph.height).toBe(CHAPTER_BAND_HEIGHT + 4 * 120)
  })

  it('leaves an unchaptered model coordinate-identical to before', () => {
    const graph = layoutEventModel(withdrawFundsModel())

    expect(graph.chapters).toEqual([])
    expect(graph.chapterBandHeight).toBe(0)
    expect(graph.lanes.map((l) => l.y)).toEqual([0, 120, 240, 360])
  })

  it('does not put a chapterless slice under any band', () => {
    const graph = layoutEventModel(chapteredModel())
    expect(graph.chapters.flatMap((b) => b.slices)).not.toContain('Loose')
  })

  it('follows the filter: hiding the interleaving slice joins the two Onboarding runs into one band', () => {
    const graph = layoutEventModel(chapteredModel(), { hiddenSlices: new Set(['SwipeOnDog']) })

    expect(graph.chapters.map((b) => b.name)).toEqual(['Onboarding'])
    expect(graph.chapters[0].slices).toEqual(['Enroll', 'AddDog', 'Verify'])
  })

  it('drops the strip entirely when every chaptered slice is hidden', () => {
    const graph = layoutEventModel(chapteredModel(), {
      hiddenSlices: new Set(['Enroll', 'AddDog', 'SwipeOnDog', 'Verify'])
    })

    expect(graph.chapters).toEqual([])
    expect(graph.chapterBandHeight).toBe(0)
    expect(graph.lanes[0].y).toBe(0)
  })
})
