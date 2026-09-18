import { mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { layoutEventModel } from '../layout'
import { minimapScale } from '../focus'
import { chapteredModel, largeModel, linkedModel, withdrawFundsModel } from './fixtures'
import type { EventModelDescriptor } from '../types'

/**
 * Issue #296 — navigation, on the canvas rather than in the arithmetic (`focus.spec.ts` has that).
 *
 * happy-dom gives every element a zero box, so a viewport has to be *told* how big it is before
 * anything that fits, centres or maps can be asserted — the same trick the #182 zoom cases use.
 */
const VIEWPORT = { width: 900, height: 600 }

const mounted: VueWrapper[] = []

function canvas(descriptor: EventModelDescriptor, props: Record<string, unknown> = {}) {
  const wrapper = mount(EventModelView, {
    props: { descriptor, ...props },
    attachTo: document.body
  })
  mounted.push(wrapper)

  const element = wrapper.find('.em-viewport').element as HTMLElement
  Object.defineProperty(element, 'clientWidth', { value: VIEWPORT.width, configurable: true })
  Object.defineProperty(element, 'clientHeight', { value: VIEWPORT.height, configurable: true })
  element.getBoundingClientRect = () =>
    ({ left: 0, top: 0, width: VIEWPORT.width, height: VIEWPORT.height }) as DOMRect

  return { wrapper, element }
}

afterEach(() => {
  while (mounted.length > 0) mounted.pop()!.unmount()
})

const zoomOf = (wrapper: VueWrapper) => wrapper.find('.em-zoom-level').text()
const lodOf = (wrapper: VueWrapper) => wrapper.find('.em-viewport').attributes('data-lod')

describe('chapter bands (#298)', () => {
  it('draws one band per contiguous run, spanning its slices', () => {
    const { wrapper } = canvas(chapteredModel())
    const bands = wrapper.findAll('.em-chapter-band')

    expect(bands.map((b) => b.text())).toEqual(['Onboarding', 'Swiping', 'Onboarding'])

    const graph = layoutEventModel(chapteredModel())
    expect(bands[0].attributes('style')).toContain(`width: ${graph.chapters[0].width}px`)
  })

  it('draws no bands, and no strip, for a model without chapters', () => {
    const { wrapper } = canvas(withdrawFundsModel())
    expect(wrapper.findAll('.em-chapter-band')).toHaveLength(0)
    expect(wrapper.find('[data-slice="WithdrawFunds"]').attributes('style')).toContain('top: 0px')
  })

  it('clicking a band focuses the whole chapter — every run of it — and dims the rest', async () => {
    const { wrapper } = canvas(chapteredModel())

    await wrapper.findAll('.em-chapter-band')[0].trigger('click')
    await wrapper.vm.$nextTick()

    // All three Onboarding slices are in focus, including Verify in the second run.
    for (const name of ['Enroll', 'AddDog', 'Verify']) {
      expect(wrapper.find(`[data-slice="${name}"]`).attributes('data-dimmed')).toBeUndefined()
    }
    expect(wrapper.find('[data-slice="SwipeOnDog"]').attributes('data-dimmed')).toBe('true')
    expect(wrapper.find('[data-slice="Loose"]').attributes('data-dimmed')).toBe('true')

    // Both Onboarding bands light up; the Swiping band dims.
    const bands = wrapper.findAll('.em-chapter-band')
    expect(bands[0].attributes('data-focused')).toBe('true')
    expect(bands[2].attributes('data-focused')).toBe('true')
    expect(bands[1].attributes('data-dimmed')).toBe('true')

    // The breadcrumb is model › chapter.
    const crumbs = wrapper.findAll('.em-crumb').map((c) => c.text())
    expect(crumbs).toEqual(['K9Crush', 'Onboarding'])
  })

  it('a focused slice in a chapter steps out through its chapter, not its domain', async () => {
    const { wrapper } = canvas(chapteredModel())

    await wrapper.findAll('.em-slice-focus')[1].trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.findAll('.em-crumb').map((c) => c.text())).toEqual(['K9Crush', 'Onboarding', 'AddDog'])
  })
})

describe('focus (#296)', () => {
  it('needs a selection before it will move anything', async () => {
    const { wrapper } = canvas(linkedModel())
    expect(wrapper.find('[data-testid="focus-selection"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="event-model-crumbs"]').exists()).toBe(false)
  })

  it('fits the slice and its linked neighbours, and dims the rest', async () => {
    const { wrapper } = canvas(linkedModel())

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    // WithdrawFunds and its one linked neighbour AccountBalance are in; SendWelcome is not.
    expect(wrapper.find('[data-slice="WithdrawFunds"]').attributes('data-dimmed')).toBeUndefined()
    expect(wrapper.find('[data-slice="AccountBalance"]').attributes('data-dimmed')).toBeUndefined()
    expect(wrapper.find('[data-slice="SendWelcome"]').attributes('data-dimmed')).toBe('true')

    // Fitted, not merely marked: the two columns are wider than 900px at 100%.
    expect(zoomOf(wrapper)).not.toBe('100%')
  })

  it('degrades to the slice alone on a descriptor with no links', async () => {
    // The only path the pinned JasperFx can produce today, so it is the one that has to work.
    const { wrapper } = canvas(withdrawFundsModel())

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[data-slice="WithdrawFunds"]').attributes('data-dimmed')).toBeUndefined()
    expect(wrapper.find('[data-slice="AccountBalance"]').attributes('data-dimmed')).toBe('true')
    expect(wrapper.find('[data-testid="event-model-crumbs"]').text()).toContain('WithdrawFunds')
  })

  it('focuses the owning slice when the reader selected a card', async () => {
    const { wrapper } = canvas(linkedModel())

    await wrapper.findAll('.em-card').find((c) => c.text() === 'BalanceProjection')!.trigger('click')
    expect(wrapper.find('[data-testid="focus-selection"]').attributes('title')).toContain(
      'AccountBalance'
    )

    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[data-slice="SendWelcome"]').attributes('data-dimmed')).toBe('true')
  })

  it('focuses straight from the slice header, without needing the toolbar', async () => {
    // The reason this control exists rather than only the toolbar button: the slice name opens the
    // host's drill-down, and in the Bobcat console that is a MODAL drawer whose overlay covers the
    // toolbar. "Select, then press Focus" is unreachable the moment the host reacts.
    const { wrapper } = canvas(linkedModel())
    const buttons = wrapper.findAll('.em-slice-focus')
    expect(buttons).toHaveLength(3)

    await buttons[0].trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[data-testid="event-model-crumbs"]').text()).toContain('WithdrawFunds')
    expect(wrapper.find('[data-slice="SendWelcome"]').attributes('data-dimmed')).toBe('true')
    // And it did NOT open the drill-down on the way past.
    expect(wrapper.emitted('slice-click')).toBeUndefined()
  })

  it('still emits element-click, so a host drawer keeps working', async () => {
    const { wrapper } = canvas(withdrawFundsModel())
    await wrapper.findAll('.em-card').find((c) => c.text() === 'WithdrawFunds')!.trigger('click')
    expect(wrapper.emitted('element-click')![0][0]).toMatchObject({ label: 'WithdrawFunds' })
  })

  it('reads model › domain › slice, and a crumb steps out to the band', async () => {
    const { wrapper } = canvas(linkedModel())

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    const crumbs = wrapper.findAll('.em-crumb')
    expect(crumbs.map((c) => c.text())).toEqual(['Banking', 'Accounts', 'WithdrawFunds'])

    await crumbs[1].trigger('click')
    await wrapper.vm.$nextTick()
    // The Accounts band is both banking slices; the Onboarding one drops out.
    expect(wrapper.find('[data-slice="AccountBalance"]').attributes('data-dimmed')).toBeUndefined()
    expect(wrapper.find('[data-slice="SendWelcome"]').attributes('data-dimmed')).toBe('true')
    expect(wrapper.findAll('.em-crumb').map((c) => c.text())).toEqual(['Banking', 'Accounts'])
  })

  it('restores the viewport the reader had before they focused, on Esc', async () => {
    const { wrapper, element } = canvas(largeModel(40))

    await wrapper.find('.em-zoom-out').trigger('click')
    await wrapper.vm.$nextTick()
    element.scrollLeft = 4000
    await wrapper.find('.em-viewport').trigger('scroll')
    const before = zoomOf(wrapper)

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'Slice010')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()
    expect(zoomOf(wrapper)).not.toBe(before)

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    globalThis.window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[data-testid="event-model-crumbs"]').exists()).toBe(false)
    expect(zoomOf(wrapper)).toBe(before)
    expect(element.scrollLeft).toBe(4000)
  })

  it('walks two rungs and still returns to where the reader started', async () => {
    // Esc means "put me back where I was", not "undo one rung" — a reader who went model → domain
    // → slice pressed it once.
    const { wrapper, element } = canvas(linkedModel())
    element.scrollLeft = 120
    await wrapper.find('.em-viewport').trigger('scroll')

    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()
    await wrapper.findAll('.em-crumb')[1].trigger('click')
    await wrapper.vm.$nextTick()

    await wrapper.find('[data-testid="focus-clear"]').trigger('click')
    await wrapper.vm.$nextTick()
    await wrapper.vm.$nextTick()
    expect(zoomOf(wrapper)).toBe('100%')
    expect(element.scrollLeft).toBe(120)
  })

  it('leaves the viewport alone when the focus resolves to nothing on the canvas', async () => {
    const { wrapper } = canvas(withdrawFundsModel())
    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    // Filter it away, then focus it: a fit to an empty rectangle is worse than not moving.
    await wrapper.find('[data-testid="filter-search"]').setValue('AccountBalance')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    expect(zoomOf(wrapper)).toBe('100%')
  })

  it('drops the focus when the descriptor is swapped for a different model', async () => {
    const { wrapper } = canvas(linkedModel())
    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    await wrapper.setProps({ descriptor: largeModel(4) })
    await wrapper.vm.$nextTick()
    // A focus on a slice the new model has never heard of would dim the whole canvas.
    expect(wrapper.find('[data-testid="event-model-crumbs"]').exists()).toBe(false)
    expect(wrapper.findAll('[data-dimmed="true"]')).toHaveLength(0)
  })
})

describe('level of detail (#296)', () => {
  it('flips the attribute at 0.7 and 0.4, and starts at detail', async () => {
    const { wrapper } = canvas(withdrawFundsModel())
    expect(lodOf(wrapper)).toBe('detail')

    // 100 → 85 → 70: still detail at exactly the threshold.
    await wrapper.find('.em-zoom-out').trigger('click')
    await wrapper.find('.em-zoom-out').trigger('click')
    expect(zoomOf(wrapper)).toBe('70%')
    expect(lodOf(wrapper)).toBe('detail')

    await wrapper.find('.em-zoom-out').trigger('click')
    expect(zoomOf(wrapper)).toBe('55%')
    expect(lodOf(wrapper)).toBe('compact')

    await wrapper.find('.em-zoom-out').trigger('click')
    expect(zoomOf(wrapper)).toBe('40%')
    expect(lodOf(wrapper)).toBe('compact')

    await wrapper.find('.em-zoom-out').trigger('click')
    expect(zoomOf(wrapper)).toBe('25%')
    expect(lodOf(wrapper)).toBe('overview')
  })

  it('re-renders no cards to change level — the DOM is identical at every one', async () => {
    // The whole design: CSS switches on one attribute. A `v-if` per level would re-render 600
    // cards on every notch of a pinch, and the two consoles could disagree about what "less"
    // means. Same markup at 100% and at 25% is what proves neither happened.
    const { wrapper } = canvas(largeModel(12))
    const detail = wrapper.find('.em-plot').html()

    for (let i = 0; i < 6; i++) await wrapper.find('.em-zoom-out').trigger('click')
    expect(lodOf(wrapper)).toBe('overview')
    expect(wrapper.find('.em-plot').html()).toBe(detail)
  })

  it('says what each column is, since at overview the cards carry no text', () => {
    const { wrapper } = canvas(largeModel(4))
    // The pattern rides the slice header and is revealed by the stylesheet at overview.
    expect(wrapper.findAll('.em-slice-pattern').map((p) => p.text())).toEqual([
      'View',
      'Command',
      'Command',
      'View'
    ])
  })
})

describe('minimap (#296)', () => {
  it('draws one rect per card and a window, and no text', () => {
    const model = largeModel(106)
    const { wrapper } = canvas(model)
    const map = wrapper.find('[data-testid="event-model-minimap"]')

    expect(map.exists()).toBe(true)
    expect(map.findAll('.em-minimap-node')).toHaveLength(layoutEventModel(model).nodes.length)
    expect(map.find('[data-testid="minimap-window"]').exists()).toBe(true)
    expect(map.findAll('text')).toHaveLength(0)
  })

  it('omits a filtered-out slice, because it is drawing the graph the canvas drew', async () => {
    const { wrapper } = canvas(largeModel(60))
    await wrapper.find('[data-testid="filter-search"]').setValue('Slice01')
    await wrapper.vm.$nextTick()

    const slices = new Set(
      wrapper
        .find('[data-testid="event-model-minimap"]')
        .findAll('.em-minimap-node')
        .map((r) => r.attributes('data-slice'))
    )
    // Slice010..Slice019 and nothing else — the map cannot disagree with the canvas about what
    // exists, because it is handed the graph the canvas drew rather than the descriptor.
    expect([...slices].sort()).toEqual(
      Array.from({ length: 10 }, (_, i) => `Slice01${i}`)
    )
  })

  it('is not drawn at all for a model that fits on a couple of screens', () => {
    // A map of a two-slice canvas answers nothing and draws a 30px smudge doing it.
    const { wrapper } = canvas(withdrawFundsModel())
    expect(wrapper.find('[data-testid="event-model-minimap"]').exists()).toBe(false)
  })

  it('tracks the scroll offset and the zoom', async () => {
    const model = largeModel(30)
    const { wrapper, element } = canvas(model)
    const scale = minimapScale(layoutEventModel(model))

    element.scrollLeft = 2000
    await wrapper.find('.em-viewport').trigger('scroll')
    const at100 = Number(wrapper.find('[data-testid="minimap-window"]').attributes('x'))
    expect(at100).toBeCloseTo(2000 * scale.x, 4)

    // Half the zoom is twice the canvas behind the same offset, so the window slides right.
    await wrapper.find('.em-zoom-out').trigger('click')
    await wrapper.vm.$nextTick()
    const width100 = Number(wrapper.find('[data-testid="minimap-window"]').attributes('width'))
    expect(width100).toBeGreaterThan(VIEWPORT.width * scale.x)
  })

  it('dims what the focus dropped, so the map answers "where is this?"', async () => {
    const { wrapper } = canvas(largeModel(30))
    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'Slice010')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    const lit = new Set(
      wrapper
        .find('[data-testid="event-model-minimap"]')
        .findAll('.em-minimap-node')
        .filter((r) => r.attributes('data-dimmed') === undefined)
        .map((r) => r.attributes('data-slice'))
    )
    // The chain of links makes Slice009 and Slice011 the neighbours, and nothing else.
    expect([...lit].sort()).toEqual(['Slice009', 'Slice010', 'Slice011'])
  })

  it('scrolls the canvas when the map is dragged', async () => {
    const model = largeModel(30)
    const { wrapper, element } = canvas(model)
    const map = wrapper.find('[data-testid="event-model-minimap"]')
    ;(map.element as unknown as HTMLElement).getBoundingClientRect = () =>
      ({ left: 0, top: 0, width: 220, height: 120 }) as DOMRect

    await map.trigger('mousedown', { button: 0, clientX: 100, clientY: 10 })
    const scale = minimapScale(layoutEventModel(model))
    // 100 map px, centred in a 900px viewport at 100% zoom.
    expect(element.scrollLeft).toBeCloseTo(100 / scale.x - VIEWPORT.width / 2, 0)

    // And the press must not also have started a canvas pan.
    expect(wrapper.find('.em-viewport').attributes('data-panning')).toBeUndefined()
  })

  it('can be turned off by a host with nowhere to put it', () => {
    const { wrapper } = canvas(withdrawFundsModel(), { minimap: false })
    expect(wrapper.find('[data-testid="event-model-minimap"]').exists()).toBe(false)
  })
})

describe('wheel zoom and the viewport report (#296)', () => {
  it('keeps the point under the cursor fixed, continuously', async () => {
    const { wrapper, element } = canvas(largeModel(40))
    element.scrollLeft = 1000

    // The cursor is 300px into a viewport scrolled to 1000, so it sits over canvas x = 1300.
    await wrapper.find('.em-viewport').trigger('wheel', { deltaY: -200, ctrlKey: true, clientX: 300, clientY: 0 })
    await wrapper.vm.$nextTick()

    const zoom = Number(/scale\(([\d.]+)\)/.exec(wrapper.find('.em-zoomed').attributes('style')!)![1])
    expect(zoom).toBeGreaterThan(1)
    // Not a stop — the button ladder keeps those, a pinch is analogue.
    expect([0.25, 0.4, 0.55, 0.7, 0.85, 1, 1.25, 1.5, 2]).not.toContain(zoom)
    expect(element.scrollLeft).toBeCloseTo(1300 * zoom - 300, 4)
  })

  it('clamps at the same floor and ceiling the buttons use', async () => {
    const { wrapper } = canvas(withdrawFundsModel())
    const viewport = wrapper.find('.em-viewport')
    for (let i = 0; i < 20; i++) {
      await viewport.trigger('wheel', { deltaY: 400, ctrlKey: true, clientX: 10, clientY: 10 })
    }
    await wrapper.vm.$nextTick()
    expect(zoomOf(wrapper)).toBe('25%')
  })

  it('leaves a plain wheel scrolling', async () => {
    const { wrapper } = canvas(withdrawFundsModel())
    await wrapper.find('.em-viewport').trigger('wheel', { deltaY: -200, clientX: 10, clientY: 10 })
    expect(zoomOf(wrapper)).toBe('100%')
  })

  it('reports zoom, offsets, focus and selection so a host can put them in a URL', async () => {
    const { wrapper, element } = canvas(linkedModel())

    element.scrollLeft = 240
    await wrapper.find('.em-viewport').trigger('scroll')
    await wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!.trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await wrapper.vm.$nextTick()

    const reports = wrapper.emitted('viewport-change')!
    const last = reports[reports.length - 1][0] as Record<string, unknown>
    expect(last.focus).toEqual({ kind: 'slice', name: 'WithdrawFunds' })
    expect(last.selection).toBe('slice:WithdrawFunds')
    expect(typeof last.zoom).toBe('number')
  })
})

describe('restoring a viewport (#296)', () => {
  it('lands on the zoom and offsets a link carried', async () => {
    const { wrapper, element } = canvas(largeModel(40), {
      initialViewport: { zoom: 0.55, x: 3000, y: 40 }
    })
    await wrapper.vm.$nextTick()
    await wrapper.vm.$nextTick()

    expect(zoomOf(wrapper)).toBe('55%')
    expect(element.scrollLeft).toBe(3000)
    expect(lodOf(wrapper)).toBe('compact')
  })

  it('re-fits a focused link rather than trusting offsets measured on another window', async () => {
    const { wrapper } = canvas(linkedModel(), {
      initialViewport: {
        zoom: 2,
        x: 99999,
        y: 0,
        focus: { kind: 'slice', name: 'WithdrawFunds' },
        selection: 'element:AccountBalance/ReadModel/Bank.Balance'
      }
    })
    await wrapper.vm.$nextTick()
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[data-testid="event-model-crumbs"]').text()).toContain('WithdrawFunds')
    expect(wrapper.find('[data-slice="SendWelcome"]').attributes('data-dimmed')).toBe('true')
    // The stored zoom is discarded in favour of a fresh fit — 200% is not what fits two columns.
    expect(zoomOf(wrapper)).not.toBe('200%')
    expect(
      wrapper
        .findAll('.em-card')
        .some((c) => c.attributes('data-selected') === 'true')
    ).toBe(true)
  })

  it('ignores a viewport naming things this model does not have', async () => {
    const { wrapper } = canvas(withdrawFundsModel(), {
      initialViewport: { focus: { kind: 'slice', name: 'Gone' }, selection: 'element:nope' }
    })
    await wrapper.vm.$nextTick()

    // The focus is honoured as a statement — the breadcrumb says so — but nothing moved and no
    // card is pretending to be selected.
    expect(zoomOf(wrapper)).toBe('100%')
    expect(wrapper.findAll('[data-selected="true"]')).toHaveLength(0)
  })
})
