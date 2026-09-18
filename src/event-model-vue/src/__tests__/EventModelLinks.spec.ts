import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { linkedThreeSliceModel, withdrawFundsModel } from './fixtures'

/**
 * Issue #295, the rendering half: the corridor arrows, the origin chevrons and the toolbar's
 * three-state control.
 *
 * The router is pinned by coordinates in `layout.spec.ts`; this file is about what a reader sees —
 * that a link is distinguishable by kind, that a card says where its input came from, and that a
 * board with no links looks exactly as it did before the feature existed.
 */
describe('EventModelView cross-slice links', () => {
  it('draws one polyline per link, tagged with its kind', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })
    const links = wrapper.findAll('.em-link')

    expect(links).toHaveLength(2)
    expect(links.every((l) => l.attributes('data-kind') === 'EventTriggers')).toBe(true)
    // Four points, so the corridor shape survives into the DOM rather than being flattened.
    expect(links[0]!.attributes('points')!.split(' ')).toHaveLength(4)
  })

  it('draws nothing at all for a descriptor with no links', () => {
    // Every producer below JasperFx.Events 2.69. The canvas has to look exactly as it did.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    expect(wrapper.findAll('.em-link')).toHaveLength(0)
  })

  it('puts an origin chevron on the far end of a link, naming the slice it came from', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })
    const chevrons = wrapper.findAll('.em-origin')

    // The two consuming ends; the source card carries none.
    expect(chevrons.map((c) => c.text())).toEqual(['◂ OpenAccount', '◂ OpenAccount'])
    expect(chevrons[0]!.attributes('title')).toContain('click to jump there')
  })

  it('selects the origin when a chevron is clicked, without also selecting the card it sits on', () => {
    // The chevron lives INSIDE the card's own button, so a click that did not stop propagating
    // would select the consuming card and then jump — landing the reader somewhere with the wrong
    // thing selected.
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })

    wrapper.findAll('.em-origin')[0]!.trigger('click')

    const emitted = wrapper.emitted('element-click')
    expect(emitted).toHaveLength(1)
    expect((emitted![0][0] as { id: string }).id).toBe('OpenAccount/Event/Bank.AccountOpened')
  })

  it('cycles the toolbar control all → selected → none, and stops drawing at none', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })
    const button = wrapper.find('[data-testid="link-mode"]')

    expect(button.attributes('data-mode')).toBe('all')
    expect(wrapper.findAll('.em-link')).toHaveLength(2)

    await button.trigger('click')
    expect(button.attributes('data-mode')).toBe('selected')
    // Nothing is selected yet, so "selected" draws nothing — which is the honest reading of it.
    expect(wrapper.findAll('.em-link')).toHaveLength(0)

    await button.trigger('click')
    expect(button.attributes('data-mode')).toBe('none')
    expect(wrapper.find('.em-links').exists()).toBe(false)

    await button.trigger('click')
    expect(button.attributes('data-mode')).toBe('all')
    expect(wrapper.findAll('.em-link')).toHaveLength(2)
  })

  it('lights the selected slice’s links and leaves the rest at rest', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })

    // Select the consuming slice: one of the two links touches it.
    const cards = wrapper.findAll('.em-card')
    await cards.find((c) => c.text().includes('Balance'))!.trigger('click')

    const lit = wrapper.findAll('.em-link[data-lit]')
    expect(lit).toHaveLength(1)
    expect(lit[0]!.attributes('data-to-slice')).toBe('AccountBalance')
  })

  it('in selected mode draws only the links touching the selection', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })

    await wrapper.find('[data-testid="link-mode"]').trigger('click')
    const cards = wrapper.findAll('.em-card')
    await cards.find((c) => c.text().includes('SendWelcome'))!.trigger('click')

    const drawn = wrapper.findAll('.em-link')
    expect(drawn).toHaveLength(1)
    expect(drawn[0]!.attributes('data-to-slice')).toBe('SendWelcome')
  })

  it('scrolls the viewport to the origin, by assignment rather than a smooth scrollTo', async () => {
    // The bug this pins, found by driving a real browser rather than by reading: the first cut
    // used `scrollTo({ behavior: 'smooth' })`, which is a NO-OP wherever the reader has asked for
    // reduced motion. The chevron selected the origin and the canvas did not move — and the jsdom
    // test passed anyway, because it only ever asserted the selection.
    const wrapper = mount(EventModelView, { props: { descriptor: linkedThreeSliceModel() } })

    const viewport = wrapper.find('.em-viewport').element as HTMLElement
    Object.defineProperty(viewport, 'clientWidth', { value: 400, configurable: true })
    Object.defineProperty(viewport, 'clientHeight', { value: 300, configurable: true })
    viewport.scrollLeft = 900

    // The rightmost consuming card, whose origin is the leftmost slice.
    const chevrons = wrapper.findAll('.em-origin')
    await chevrons[chevrons.length - 1]!.trigger('click')

    // Moved, and toward the origin rather than away from it.
    expect(viewport.scrollLeft).not.toBe(900)
    expect(viewport.scrollLeft).toBeLessThan(900)
  })
})
