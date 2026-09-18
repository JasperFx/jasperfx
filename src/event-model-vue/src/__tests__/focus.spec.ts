import { describe, expect, it } from 'vitest'
import {
  breadcrumbFor,
  fitToRect,
  focusedSliceNames,
  lodFor,
  minimapScale,
  minimapWindow,
  neighbourhoodOf,
  rectForSlices,
  scrollForMinimapPoint,
  selectionFromKey,
  selectionToKey,
  sliceOfSelection,
  viewportFromQuery,
  viewportToQuery,
  worthMapping
} from '../focus'
import { canvasSize, layoutEventModel, CANVAS_PADDING, GUTTER_GAP, GUTTER_WIDTH } from '../layout'
import { chapteredModel, largeModel, linkedModel, withdrawFundsModel } from './fixtures'

/**
 * Issue #296's arithmetic, tested where it lives — as pure functions over a laid-out graph rather
 * than through a component that would need a DOM to have a width at all.
 */
describe('neighbourhood', () => {
  it('is the slice plus everything one link away, in either direction', () => {
    const model = linkedModel()
    // The link runs WithdrawFunds → AccountBalance, so each names the other.
    expect([...neighbourhoodOf(model, 'WithdrawFunds')].sort()).toEqual([
      'AccountBalance',
      'WithdrawFunds'
    ])
    expect([...neighbourhoodOf(model, 'AccountBalance')].sort()).toEqual([
      'AccountBalance',
      'WithdrawFunds'
    ])
  })

  it('stops at one hop — a neighbour of a neighbour is not a neighbour', () => {
    const model = largeModel(6)
    expect([...neighbourhoodOf(model, 'Slice002')].sort()).toEqual([
      'Slice001',
      'Slice002',
      'Slice003'
    ])
  })

  it('degrades to the slice alone when the descriptor carries no links', () => {
    // The path the pinned JasperFx can actually produce: `Links` arrives upstream in 2.69, and
    // this package must not invent one client-side to fill the gap.
    const model = withdrawFundsModel()
    expect(model.links).toBeUndefined()
    expect([...neighbourhoodOf(model, 'WithdrawFunds')]).toEqual(['WithdrawFunds'])
  })

  it('tolerates a null descriptor and an unknown slice', () => {
    expect([...neighbourhoodOf(null, 'nope')]).toEqual(['nope'])
  })

  it('resolves a domain focus to its whole band', () => {
    const model = largeModel(8)
    // Four domains cycled over eight slices: two apiece.
    expect([...focusedSliceNames(model, { kind: 'domain', name: 'Payments' })].sort()).toEqual([
      'Slice001',
      'Slice005'
    ])
  })

  it('resolves a domain nobody declares to nothing, never to the whole model', () => {
    expect(focusedSliceNames(largeModel(4), { kind: 'domain', name: 'Nope' }).size).toBe(0)
  })

  it('resolves a chapter focus to every slice in the chapter, across its runs (#298)', () => {
    // Onboarding is interleaved by Swiping on the canvas, but a focus is about the model, and the
    // model has one Onboarding chapter with three slices in it.
    expect([...focusedSliceNames(chapteredModel(), { kind: 'chapter', name: 'Onboarding' })].sort()).toEqual([
      'AddDog',
      'Enroll',
      'Verify'
    ])
    expect(focusedSliceNames(chapteredModel(), { kind: 'chapter', name: 'Nope' }).size).toBe(0)
  })
})

describe('rectForSlices', () => {
  it('bounds the columns in canvas space, over the full plot height', () => {
    const model = linkedModel()
    const graph = layoutEventModel(model)
    const rect = rectForSlices(graph, neighbourhoodOf(model, 'WithdrawFunds'))!

    const first = graph.slices[0]
    const second = graph.slices[1]
    const plotOrigin = CANVAS_PADDING + GUTTER_WIDTH + GUTTER_GAP

    expect(rect.x).toBe(plotOrigin + first.x)
    expect(rect.width).toBe(second.x + second.width - first.x)
    expect(rect.y).toBe(CANVAS_PADDING)
    // The whole column, not only the lanes that happen to carry cards.
    expect(rect.height).toBe(graph.height)
  })

  it('is null when nothing named is on the canvas', () => {
    const graph = layoutEventModel(withdrawFundsModel(), {
      hiddenSlices: new Set(['WithdrawFunds'])
    })
    expect(rectForSlices(graph, new Set(['WithdrawFunds']))).toBeNull()
  })
})

describe('fitToRect', () => {
  it('scales to the tighter of the two ratios and centres the rect', () => {
    const fit = fitToRect(
      { x: 1000, y: 0, width: 800, height: 400 },
      { width: 400, height: 400 },
      { min: 0.25, max: 2 }
    )
    // 400/800 = 0.5 horizontally, 400/400 = 1 vertically — the narrower one wins.
    expect(fit.zoom).toBe(0.5)
    // The rect's middle is at 1400 unscaled, 700 scaled; centring it in a 400px viewport is 500.
    expect(fit.x).toBe(500)
  })

  it('never blows a small neighbourhood up past 100%', () => {
    // The same argument fit-to-width settled in #182: a small thing at 200% looks like a mistake.
    const fit = fitToRect(
      { x: 0, y: 0, width: 100, height: 100 },
      { width: 1200, height: 800 },
      { min: 0.25, max: 2 }
    )
    expect(fit.zoom).toBe(1)
  })

  it('clamps at the zoom floor rather than shrinking past legibility', () => {
    const fit = fitToRect(
      { x: 0, y: 0, width: 40000, height: 400 },
      { width: 1200, height: 800 },
      { min: 0.25, max: 2 }
    )
    expect(fit.zoom).toBe(0.25)
  })

  it('fits on width alone when the viewport height is not constraining', () => {
    // The canvas's scroller grows to its content, so its height is ABOUT the canvas height and
    // `vh/h` is the zoom the reader already had. Measured on the real model: focus "fitted" 46%
    // to 48%. A zero height says "do not ask me about the vertical".
    const fit = fitToRect(
      { x: 0, y: 0, width: 1800, height: 500 },
      { width: 900, height: 0 },
      { min: 0.25, max: 2 }
    )
    expect(fit.zoom).toBe(0.5)
  })

  it('clamps the scroll offsets at zero', () => {
    const fit = fitToRect(
      { x: 0, y: 0, width: 200, height: 200 },
      { width: 1200, height: 800 },
      { min: 0.25, max: 2 }
    )
    expect(fit.x).toBe(0)
    expect(fit.y).toBe(0)
  })
})

describe('level of detail', () => {
  it('flips at 0.7 and 0.4, inclusive at the top of each band', () => {
    expect(lodFor(1)).toBe('detail')
    expect(lodFor(0.7)).toBe('detail')
    expect(lodFor(0.699)).toBe('compact')
    expect(lodFor(0.4)).toBe('compact')
    expect(lodFor(0.399)).toBe('overview')
    expect(lodFor(0.25)).toBe('overview')
  })
})

describe('breadcrumb', () => {
  it('reads model › domain › slice, with each crumb a step out', () => {
    const model = linkedModel()
    const crumbs = breadcrumbFor(model, { kind: 'slice', name: 'WithdrawFunds' })

    expect(crumbs.map((c) => c.label)).toEqual(['Banking', 'Accounts', 'WithdrawFunds'])
    expect(crumbs[0].target).toBeNull()
    expect(crumbs[1].target).toEqual({ kind: 'domain', name: 'Accounts' })
  })

  it('omits the domain rung rather than inventing one for a slice that declares none', () => {
    const model = withdrawFundsModel()
    model.slices![0].domain = null
    expect(breadcrumbFor(model, { kind: 'slice', name: 'WithdrawFunds' }).map((c) => c.label)).toEqual([
      'Banking',
      'WithdrawFunds'
    ])
  })

  it('is empty when nothing is focused', () => {
    expect(breadcrumbFor(withdrawFundsModel(), null)).toEqual([])
  })

  it('reads model › chapter › slice when the slice has a chapter, and the chapter crumb focuses the chapter (#298)', () => {
    const crumbs = breadcrumbFor(chapteredModel(), { kind: 'slice', name: 'AddDog' })

    // The chapter, not the domain: the band above the slice is the grouping the canvas draws, and
    // a trail showing both would be two orthogonal axes pretending to be one hierarchy.
    expect(crumbs.map((c) => c.label)).toEqual(['K9Crush', 'Onboarding', 'AddDog'])
    expect(crumbs[1].target).toEqual({ kind: 'chapter', name: 'Onboarding' })
  })

  it('falls back to the domain rung for a slice in no chapter', () => {
    expect(breadcrumbFor(chapteredModel(), { kind: 'slice', name: 'Loose' }).map((c) => c.label)).toEqual([
      'K9Crush',
      'Dating',
      'Loose'
    ])
  })

  it('reads model › chapter for a chapter focus', () => {
    const crumbs = breadcrumbFor(chapteredModel(), { kind: 'chapter', name: 'Swiping' })
    expect(crumbs.map((c) => c.label)).toEqual(['K9Crush', 'Swiping'])
    expect(crumbs[1].target).toEqual({ kind: 'chapter', name: 'Swiping' })
  })
})

describe('viewport in the URL', () => {
  it('round-trips zoom, offsets, focus and selection', () => {
    const query = viewportToQuery({
      zoom: 0.55,
      x: 1234.6,
      y: 20,
      focus: { kind: 'slice', name: 'CreditWallet' },
      selection: 'element:CreditWallet/Command/Wallets.CreditWallet'
    })
    expect(query).toEqual({
      z: '0.55',
      x: '1235',
      y: '20',
      focus: 'slice:CreditWallet',
      sel: 'element:CreditWallet/Command/Wallets.CreditWallet'
    })

    expect(viewportFromQuery(query)).toEqual({
      zoom: 0.55,
      x: 1235,
      y: 20,
      focus: { kind: 'slice', name: 'CreditWallet' },
      selection: 'element:CreditWallet/Command/Wallets.CreditWallet'
    })
  })

  it('rounds a continuous zoom to something a human can paste', () => {
    expect(viewportToQuery({ zoom: 0.6173469387755102, x: 0, y: 0 }).z).toBe('0.62')
  })

  it('splits a focus on the FIRST colon, because a slice name may contain one', () => {
    const state = viewportFromQuery({ focus: 'slice:GET /api/a:b' })
    expect(state.focus).toEqual({ kind: 'slice', name: 'GET /api/a:b' })
  })

  it('round-trips a chapter focus (#298)', () => {
    const query = viewportToQuery({ zoom: 1, x: 0, y: 0, focus: { kind: 'chapter', name: 'Onboarding' } })
    expect(query.focus).toBe('chapter:Onboarding')
    expect(viewportFromQuery(query).focus).toEqual({ kind: 'chapter', name: 'Onboarding' })
  })

  it('drops junk rather than landing on a canvas scrolled to NaN', () => {
    expect(viewportFromQuery({ z: 'banana', x: 'nope', focus: 'lane:Onboarding' })).toEqual({})
    expect(viewportFromQuery(null)).toEqual({})
    expect(viewportFromQuery({})).toEqual({})
  })

  it('takes the first value when a router hands back a repeated query parameter', () => {
    expect(viewportFromQuery({ z: ['0.4', '2'] }).zoom).toBe(0.4)
  })
})

describe('selection keys', () => {
  it('round-trips an element through its owning slice, by lookup rather than by parse', () => {
    const model = withdrawFundsModel()
    // The id contains slashes, and so does a route-named slice — parsing the id would be wrong in
    // exactly the case it mattered.
    const key = selectionToKey({
      kind: 'element',
      id: 'AccountBalance/Projection/Bank.BalanceProjection',
      sliceName: 'AccountBalance'
    })
    expect(key).toBe('element:AccountBalance/Projection/Bank.BalanceProjection')
    expect(selectionFromKey(model, key)).toEqual({
      kind: 'element',
      id: 'AccountBalance/Projection/Bank.BalanceProjection',
      sliceName: 'AccountBalance'
    })
  })

  it('drops a key naming something the model does not contain', () => {
    expect(selectionFromKey(withdrawFundsModel(), 'slice:Gone')).toBeNull()
    expect(selectionFromKey(withdrawFundsModel(), 'element:nope')).toBeNull()
    expect(selectionFromKey(withdrawFundsModel(), 'garbage')).toBeNull()
  })

  it('reports the slice Focus would act on', () => {
    expect(sliceOfSelection({ kind: 'slice', name: 'A' })).toBe('A')
    expect(sliceOfSelection({ kind: 'element', id: 'A/x', sliceName: 'A' })).toBe('A')
    expect(sliceOfSelection(null)).toBeNull()
  })
})

describe('minimap geometry', () => {
  it('scales x and y apart, because a 106-slice canvas is 100:1 and a faithful map of it is a hairline', () => {
    const graph = layoutEventModel(largeModel(106))
    const canvas = canvasSize(graph)
    const scale = minimapScale(graph)

    // The measured problem: 106 slices is tens of thousands of px wide and ~550 tall.
    expect(canvas.width).toBeGreaterThan(9000)
    expect(canvas.width * scale.x).toBeLessThanOrEqual(260)

    // Uniform, this map would be a few pixels tall — 222 × 3.6 on the real model. It fills its
    // height box instead, so the four lane bands are bands rather than a smudge.
    expect(canvas.height * scale.y).toBeCloseTo(72, 5)
    expect(scale.y).toBeGreaterThan(scale.x)
  })

  it('never magnifies along x — 1/40 is the ceiling on the axis the model runs along', () => {
    expect(minimapScale(layoutEventModel(largeModel(6))).x).toBe(1 / 40)
  })

  it('is not worth drawing for a model that fits on a couple of screens', () => {
    // A map of a two-slice canvas answers nothing and draws a 30px smudge doing it.
    expect(worthMapping(layoutEventModel(withdrawFundsModel()))).toBe(false)
    expect(worthMapping(layoutEventModel(largeModel(106)))).toBe(true)
  })

  it('places the window from the scroll offsets, undoing the zoom first', () => {
    const graph = layoutEventModel(largeModel(20))
    const scale = minimapScale(graph)
    const window = minimapWindow(graph, { x: 1000, y: 0, width: 800, height: 600 }, 0.5, scale)

    // 1000 scaled px at 50% is 2000 unscaled px, which is 2000 * scale.x on the map.
    expect(window.x).toBeCloseTo(2000 * scale.x, 5)
    expect(window.width).toBeCloseTo(1600 * scale.x, 5)
  })

  it('keeps the window inside the map when the viewport is wider than the canvas', () => {
    const graph = layoutEventModel(largeModel(8))
    const scale = minimapScale(graph)
    const box = canvasSize(graph).width * scale.x
    const window = minimapWindow(graph, { x: 0, y: 0, width: 40000, height: 40000 }, 1, scale)

    expect(window.width).toBeCloseTo(box, 5)
    expect(window.x).toBe(0)
  })

  it('turns a point on the map into a centred scroll offset', () => {
    const scroll = scrollForMinimapPoint(
      { x: 50, y: 10 },
      { width: 800, height: 600 },
      0.5,
      { x: 1 / 40, y: 1 / 40 }
    )
    // 50 map px is 2000 canvas px, 1000 scaled px; centring that in an 800px viewport is 600.
    expect(scroll.x).toBe(600)
    // Clamped rather than negative: 10 map px is 200 scaled px, less than half a 600px viewport.
    expect(scroll.y).toBe(0)
  })
})
