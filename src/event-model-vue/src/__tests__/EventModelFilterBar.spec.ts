import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { chapteredModel, withdrawFundsModel } from './fixtures'
import type { EventModelDescriptor } from '../types'

// Both fixture slices carry a specification, so the drift split needs a model that has one of
// each. Kept local and minimal — the shared fixture is about rendering, not about binding.
function mixedBinding(): EventModelDescriptor {
  return {
    name: 'Mixed',
    slices: [
      {
        name: 'Bound',
        domain: 'Accounts',
        elements: [{ id: 'Bound/Command/C', kind: 'Command', lane: 'Command', label: 'C' }],
        specifications: [{ identity: 'F/bound' }]
      },
      {
        name: 'Unbound',
        domain: 'Payments',
        elements: [{ id: 'Unbound/Command/D', kind: 'Command', lane: 'Command', label: 'D' }]
      }
    ]
  }
}

/**
 * Issue #194 — the filter bar. Zoom cannot make 106 slices navigable and no tuning of it will:
 * CritterWatch's merged model is ~10,000px wide at the 25% floor, and the floor is deliberate
 * because below it the labels stop being labels. So render fewer slices, not smaller ones.
 */
describe('EventModelView filter bar', () => {
  const mountView = (props = {}) =>
    mount(EventModelView, { props: { descriptor: withdrawFundsModel(), ...props } })

  it('is shown by default, in the shared component, so both consoles behave the same', () => {
    expect(mountView().find('[data-testid="event-model-filter"]').exists()).toBe(true)
  })

  it('can be turned off by a host that owns its own controls', () => {
    expect(
      mountView({ filterable: false }).find('[data-testid="event-model-filter"]').exists()
    ).toBe(false)
  })

  it('always says how many slices are showing, not only while filtering', async () => {
    // A canvas that silently renders a subset is how a reader concludes a slice does not exist.
    const wrapper = mountView()
    expect(wrapper.find('[data-testid="filter-count"]').text()).toBe('2 of 2')

    await wrapper.find('[data-testid="filter-hotspots"]').trigger('click')
    expect(wrapper.find('[data-testid="filter-count"]').text()).toBe('1 of 2')
  })

  it('narrows the canvas to the unbound slices — the drift view', async () => {
    const wrapper = mountView({ descriptor: mixedBinding() })

    await wrapper.find('[data-testid="filter-unbound"]').trigger('click')

    expect(wrapper.find('[data-slice="Unbound"]').exists()).toBe(true)
    expect(wrapper.find('[data-slice="Bound"]').exists()).toBe(false)
  })

  it('narrows to the bound slices too, and the two are exclusive', async () => {
    const wrapper = mountView({ descriptor: mixedBinding() })

    await wrapper.find('[data-testid="filter-bound"]').trigger('click')
    expect(wrapper.find('[data-slice="Bound"]').exists()).toBe(true)
    expect(wrapper.find('[data-slice="Unbound"]').exists()).toBe(false)

    // Turning on the other replaces it rather than producing an impossible AND.
    await wrapper.find('[data-testid="filter-unbound"]').trigger('click')
    expect(wrapper.find('[data-slice="Unbound"]').exists()).toBe(true)
    expect(wrapper.find('[data-slice="Bound"]').exists()).toBe(false)
  })

  it('toggles a chip off, restoring the whole model', async () => {
    const wrapper = mountView({ descriptor: mixedBinding() })

    await wrapper.find('[data-testid="filter-unbound"]').trigger('click')
    await wrapper.find('[data-testid="filter-unbound"]').trigger('click')

    expect(wrapper.find('[data-testid="filter-count"]').text()).toBe('2 of 2')
  })

  it('offers one chip per declared domain and filters by it', async () => {
    const wrapper = mountView({ descriptor: mixedBinding() })
    const chips = wrapper.findAll('[data-domain]')

    expect(chips.map((c) => c.text())).toEqual(['Accounts', 'Payments'])

    await chips[0].trigger('click')

    expect(wrapper.find('[data-testid="filter-count"]').text()).toBe('1 of 2')
    expect(wrapper.find('[data-slice="Bound"]').exists()).toBe(true)
    expect(wrapper.find('[data-slice="Unbound"]').exists()).toBe(false)
    expect(chips[0].attributes('aria-pressed')).toBe('true')
  })

  it('offers one chip per declared chapter, in the model\'s order, and filters by it (#298)', async () => {
    const wrapper = mountView({ descriptor: chapteredModel() })
    const chips = wrapper.findAll('[data-chapter][class*="em-filter-chip"]')

    // Declaration order, not alphabetical — Onboarding comes before Swiping in the story.
    expect(chips.map((c) => c.text())).toEqual(['Onboarding', 'Swiping'])

    await chips[1].trigger('click')

    expect(wrapper.find('[data-testid="filter-count"]').text()).toBe('1 of 5')
    expect(wrapper.find('[data-slice="SwipeOnDog"]').exists()).toBe(true)
    expect(wrapper.find('[data-slice="Enroll"]').exists()).toBe(false)
    // A chapterless slice is not in any chapter, so a chapter filter excludes it.
    expect(wrapper.find('[data-slice="Loose"]').exists()).toBe(false)
    expect(chips[1].attributes('aria-pressed')).toBe('true')
  })

  it('offers no chapter chips for a model with no chapters', () => {
    expect(mountView().findAll('[data-chapter]')).toHaveLength(0)
  })

  it('searches by slice name', async () => {
    const wrapper = mountView()

    await wrapper.find('[data-testid="filter-search"]').setValue('balance')

    expect(wrapper.find('[data-slice="AccountBalance"]').exists()).toBe(true)
    expect(wrapper.find('[data-slice="WithdrawFunds"]').exists()).toBe(false)
  })

  it('offers a clear only while something is narrowed', async () => {
    const wrapper = mountView()
    expect(wrapper.find('[data-testid="filter-clear"]').exists()).toBe(false)

    await wrapper.find('[data-testid="filter-hotspots"]').trigger('click')
    expect(wrapper.find('[data-testid="filter-clear"]').exists()).toBe(true)

    await wrapper.find('[data-testid="filter-clear"]').trigger('click')
    expect(wrapper.find('[data-testid="filter-count"]').text()).toBe('2 of 2')
    expect(wrapper.find('[data-testid="filter-clear"]').exists()).toBe(false)
  })

  it('tells the host what the reader narrowed to', async () => {
    const wrapper = mountView({ descriptor: mixedBinding() })

    await wrapper.find('[data-testid="filter-unbound"]').trigger('click')

    const emitted = wrapper.emitted('filter-change')
    expect(emitted).toBeTruthy()
    expect(emitted![0][0]).toMatchObject({ specs: 'unbound' })
  })

  it('shows an empty state rather than a broken canvas when nothing survives', async () => {
    const wrapper = mountView()

    await wrapper.find('[data-testid="filter-search"]').setValue('nothing matches this')

    expect(wrapper.find('[data-testid="event-model-empty"]').exists()).toBe(true)
  })
})

describe('EventModelView collapse chevron', () => {
  it('collapses one slice without stealing the header click', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })

    expect(wrapper.findAll('.em-card')).toHaveLength(9)

    await wrapper.find('[data-slice="WithdrawFunds"] .em-slice-collapse').trigger('click')

    // Collapsed, so its cards are gone but the column — and therefore the slice — is still there.
    expect(wrapper.find('[data-slice="WithdrawFunds"]').exists()).toBe(true)
    expect(wrapper.findAll('.em-card')).toHaveLength(3)

    // The drill-down is untouched: the chevron stops propagation rather than replacing the click.
    expect(wrapper.emitted('slice-click')).toBeFalsy()
  })

  it('expands again', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const chevron = () => wrapper.find('[data-slice="WithdrawFunds"] .em-slice-collapse')

    await chevron().trigger('click')
    await chevron().trigger('click')

    expect(wrapper.findAll('.em-card')).toHaveLength(9)
  })

  it('unions with whatever the host collapsed', async () => {
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel(), collapsedSlices: new Set(['AccountBalance']) }
    })

    await wrapper.find('[data-slice="WithdrawFunds"] .em-slice-collapse').trigger('click')

    expect(wrapper.findAll('.em-card')).toHaveLength(0)
    expect(wrapper.findAll('.em-slice')).toHaveLength(2)
  })

  it('collapsing every slice is not an empty canvas', () => {
    // The columns, headers and spec counts are all still there and all still mean something.
    // Reporting "No slices to render" would tell the reader their model vanished.
    const wrapper = mount(EventModelView, {
      props: {
        descriptor: withdrawFundsModel(),
        collapsedSlices: new Set(['WithdrawFunds', 'AccountBalance'])
      }
    })

    expect(wrapper.find('[data-testid="event-model-empty"]').exists()).toBe(false)
    expect(wrapper.findAll('.em-slice')).toHaveLength(2)
    expect(wrapper.findAll('.em-card')).toHaveLength(0)
  })
})
