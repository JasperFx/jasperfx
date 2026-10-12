import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { hiddenSliceNames, hotspotsOf, isEmptyFilter, matchesFilter, patternsOf } from '../filters'
import { queryModel, withdrawFundsModel } from './fixtures'

/**
 * jasperfx#1003 — a read-only endpoint is a `Query` slice, drawn as its own column beside the View
 * it reads rather than folded into it.
 */
describe('Query slices', () => {
  it('marks every slice column with its pattern, so a Query column is styled apart', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: queryModel() } })
    const columns = wrapper.findAll('.em-slice')

    expect(columns.map((c) => c.attributes('data-pattern'))).toEqual(['View', 'Query', 'Query'])
  })

  it('spells the pattern out on a Query column', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: queryModel() } })
    const query = wrapper.find('.em-slice[data-slice="GET /api/accounts/{id}"]')

    expect(query.find('.em-slice-pattern').text()).toBe('Query')
  })

  it('leaves a slice with no pattern unmarked', () => {
    const model = withdrawFundsModel()
    delete model.slices![0].pattern
    const wrapper = mount(EventModelView, { props: { descriptor: model } })

    expect(wrapper.findAll('.em-slice')[0]!.attributes('data-pattern')).toBeUndefined()
  })

  it('draws the read as a dashed ReadModelRead link from the view to the query', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: queryModel() } })
    const links = wrapper.findAll('.em-link')

    expect(links).toHaveLength(1)
    expect(links[0]!.attributes('data-kind')).toBe('ReadModelRead')

    // The query's read-model card names the view it came from
    expect(wrapper.findAll('.em-origin').map((c) => c.text())).toEqual(['◂ AccountSummary'])
  })

  it('renders a query that writes as a pattern-violation hotspot card', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: queryModel() } })
    const card = wrapper.find('.em-card[data-hotspot-origin="PatternViolation"]')

    expect(card.exists()).toBe(true)
    expect(card.find('.em-hotspot-origin').text()).toBe('Pattern violation')
  })
})

describe('filtering by pattern', () => {
  it('lists the patterns a model carries, in first-appearance order', () => {
    expect(patternsOf(queryModel())).toEqual(['View', 'Query'])
  })

  it('keeps only the chosen patterns, excluding a slice whose pattern is not derived yet', () => {
    const filter = { patterns: new Set(['Query'] as const) }

    expect([...hiddenSliceNames(queryModel(), filter)]).toEqual(['AccountSummary'])
    expect(matchesFilter({ name: 'Unpatterned' }, filter)).toBe(false)
    expect(isEmptyFilter(filter)).toBe(false)
    expect(isEmptyFilter({ patterns: new Set() })).toBe(true)
  })

  it('counts a pattern violation as a hotspot in the findings view', () => {
    const model = queryModel()
    const violating = model.slices![2]!

    expect(hotspotsOf(violating)).toHaveLength(1)
    expect([...hiddenSliceNames(model, { hotspotsOnly: true })]).toEqual([
      'AccountSummary',
      'GET /api/accounts/{id}'
    ])
  })
})
