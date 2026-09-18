import { describe, expect, it } from 'vitest'
import { chaptersOf, domainsOf, hiddenSliceNames, isEmptyFilter, matchesFilter } from '../filters'
import { layoutEventModel, COLLAPSED_WIDTH } from '../layout'
import type { EventModelDescriptor, EventModelSliceDescriptor } from '../types'
import { withdrawFundsModel } from './fixtures'

function slice(over: Partial<EventModelSliceDescriptor> = {}): EventModelSliceDescriptor {
  return { name: 'S', elements: [], ...over }
}

function model(...slices: EventModelSliceDescriptor[]): EventModelDescriptor {
  return { name: 'M', slices }
}

describe('slice filter', () => {
  it('narrows nothing by default, so the whole model shows', () => {
    const descriptor = withdrawFundsModel()

    expect(isEmptyFilter({})).toBe(true)
    expect(hiddenSliceNames(descriptor, {}).size).toBe(0)
  })

  it('lists the domains a model declares, sorted', () => {
    expect(domainsOf(model(
      slice({ name: 'A', domain: 'Payments' }),
      slice({ name: 'B', domain: 'Accounts' }),
      slice({ name: 'C', domain: 'Accounts' }),
      slice({ name: 'D' })
    ))).toEqual(['Accounts', 'Payments'])
  })

  it('lists the chapters a model declares in the order they first appear — a sequence, not a set', () => {
    // #298: a chapter is a span of the timeline. Alphabetising them would shuffle the story.
    expect(chaptersOf(model(
      slice({ name: 'A', chapter: 'Swiping' }),
      slice({ name: 'B', chapter: 'Onboarding' }),
      slice({ name: 'C', chapter: 'Swiping' }),
      slice({ name: 'D' })
    ))).toEqual(['Swiping', 'Onboarding'])
  })

  it('filters by chapter, excluding a slice that declares none', () => {
    const chaptered = slice({ name: 'A', chapter: 'Swiping' })
    const loose = slice({ name: 'B' })
    const filter = { chapters: new Set(['Swiping']) }

    expect(matchesFilter(chaptered, filter)).toBe(true)
    expect(matchesFilter(loose, filter)).toBe(false)
    expect(isEmptyFilter(filter)).toBe(false)
    expect(isEmptyFilter({ chapters: new Set() })).toBe(true)
  })

  it('excludes a slice with no domain when a domain filter is on', () => {
    // Treating "no domain" as matching every domain would make a narrowing control GROW the
    // canvas, which is the opposite of what the reader asked for.
    const undomained = slice({ name: 'Loose' })

    expect(matchesFilter(undomained, {})).toBe(true)
    expect(matchesFilter(undomained, { domains: new Set(['Accounts']) })).toBe(false)
  })

  it('keeps only the chosen domains', () => {
    const descriptor = model(
      slice({ name: 'A', domain: 'Accounts' }),
      slice({ name: 'P', domain: 'Payments' })
    )

    expect([...hiddenSliceNames(descriptor, { domains: new Set(['Accounts']) })]).toEqual(['P'])
  })

  it('separates bound from unbound slices — the drift view', () => {
    const bound = slice({ name: 'Bound', specifications: [{ identity: 'F/s' }] })
    const unbound = slice({ name: 'Unbound' })

    expect(matchesFilter(bound, { specs: 'bound' })).toBe(true)
    expect(matchesFilter(unbound, { specs: 'bound' })).toBe(false)

    expect(matchesFilter(unbound, { specs: 'unbound' })).toBe(true)
    expect(matchesFilter(bound, { specs: 'unbound' })).toBe(false)

    // 'any' is the same as not asking.
    expect(matchesFilter(bound, { specs: 'any' })).toBe(true)
    expect(matchesFilter(unbound, { specs: 'any' })).toBe(true)
  })

  it('keeps only slices carrying a hotspot', () => {
    const withHotspot = slice({ name: 'H', hotspots: [{ origin: 'PendingSpecification', text: 'not specified' }] })

    expect(matchesFilter(withHotspot, { hotspotsOnly: true })).toBe(true)
    expect(matchesFilter(slice({ name: 'Q' }), { hotspotsOnly: true })).toBe(false)
  })

  it('searches the slice name case-insensitively, ignoring surrounding space', () => {
    const target = slice({ name: 'WithdrawFunds' })

    expect(matchesFilter(target, { search: 'draw' })).toBe(true)
    expect(matchesFilter(target, { search: '  DRAWFUNDS ' })).toBe(true)
    expect(matchesFilter(target, { search: 'deposit' })).toBe(false)
    // Whitespace-only is not a search.
    expect(matchesFilter(target, { search: '   ' })).toBe(true)
    expect(isEmptyFilter({ search: '   ' })).toBe(true)
  })

  it('combines criteria as AND', () => {
    const descriptor = model(
      slice({ name: 'A', domain: 'Accounts', specifications: [{ identity: 'x' }] }),
      slice({ name: 'B', domain: 'Accounts' }),
      slice({ name: 'C', domain: 'Payments', specifications: [{ identity: 'y' }] })
    )

    const hidden = hiddenSliceNames(descriptor, {
      domains: new Set(['Accounts']),
      specs: 'bound'
    })

    expect([...hidden].sort()).toEqual(['B', 'C'])
  })
})

describe('hidden slices in the layout', () => {
  it('costs nothing at all — no node, no placeholder, no width', () => {
    const descriptor = withdrawFundsModel()

    const full = layoutEventModel(descriptor)
    const narrowed = layoutEventModel(descriptor, { hiddenSlices: new Set(['AccountBalance']) })

    expect(narrowed.slices.map((s) => s.name)).toEqual(['WithdrawFunds'])
    expect(narrowed.nodes.every((n) => n.sliceName === 'WithdrawFunds')).toBe(true)
    expect(narrowed.width).toBeLessThan(full.width)
  })

  it('is the distinction that makes the filter answer the problem', () => {
    // A collapsed slice keeps a placeholder, which is right for one slice put away and useless at
    // 106 — 106 placeholders is still thousands of pixels. Hiding is what actually narrows.
    const descriptor = withdrawFundsModel()

    const collapsed = layoutEventModel(descriptor, { collapsedSlices: new Set(['AccountBalance']) })
    const hidden = layoutEventModel(descriptor, { hiddenSlices: new Set(['AccountBalance']) })

    expect(collapsed.slices.map((s) => s.name)).toContain('AccountBalance')
    expect(collapsed.slices.find((s) => s.name === 'AccountBalance')!.width).toBe(COLLAPSED_WIDTH)

    expect(hidden.slices.map((s) => s.name)).not.toContain('AccountBalance')
    expect(hidden.width).toBeLessThan(collapsed.width)
  })

  it('hiding beats collapsing when a slice is in both sets', () => {
    // Rendering a placeholder for something the reader filtered out would put back the width they
    // asked to remove.
    const descriptor = withdrawFundsModel()

    const graph = layoutEventModel(descriptor, {
      collapsedSlices: new Set(['AccountBalance']),
      hiddenSlices: new Set(['AccountBalance'])
    })

    expect(graph.slices.map((s) => s.name)).toEqual(['WithdrawFunds'])
  })

  it('hiding every slice renders an empty canvas rather than a broken one', () => {
    const descriptor = withdrawFundsModel()
    const names = new Set((descriptor.slices ?? []).map((s) => s.name))

    const graph = layoutEventModel(descriptor, { hiddenSlices: names })

    expect(graph.slices).toEqual([])
    expect(graph.nodes).toEqual([])
    expect(graph.edges).toEqual([])
    expect(graph.width).toBe(0)
  })

  it('stays a pure function of (descriptor, options)', () => {
    // The whole constraint on this feature: the same inputs must lay out identically, so the
    // "renders identically in both viewers" claim survives the filter existing.
    const descriptor = withdrawFundsModel()
    const options = { hiddenSlices: new Set(['AccountBalance']) }

    expect(layoutEventModel(descriptor, options)).toEqual(layoutEventModel(descriptor, options))
  })
})
