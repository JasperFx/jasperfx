import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { fourSourceModel, twoAggregateModel, withdrawFundsModel } from './fixtures'

describe('EventModelView', () => {
  it('renders one card per element with its canonical colour', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const cards = wrapper.findAll('.em-card')
    expect(cards).toHaveLength(9)

    const event = cards.find((c) => c.text() === 'FundsWithdrawn')!
    expect(event.attributes('style')).toContain('background: #F5A623')
  })

  it('draws an outlined kind with a transparent fill so it stays distinct from its command', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const handler = wrapper.findAll('.em-card').find((c) => c.text() === 'AccountHandler')!
    expect(handler.attributes('style')).toContain('background: transparent')
  })

  it('shows an empty state rather than a blank canvas', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: { name: 'x', slices: [] } } })
    expect(wrapper.find('[data-testid="event-model-empty"]').exists()).toBe(true)
    expect(wrapper.findAll('.em-card')).toHaveLength(0)
  })

  it('tolerates a null descriptor', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: null } })
    expect(wrapper.find('[data-testid="event-model-empty"]').exists()).toBe(true)
  })

  it('emits the clicked element so a host can drill down to the bound spec', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const card = wrapper.findAll('.em-card').find((c) => c.text() === 'WithdrawFunds')!
    card.trigger('click')
    expect(wrapper.emitted('element-click')![0][0]).toMatchObject({
      kind: 'Command',
      label: 'WithdrawFunds'
    })
  })

  it('emits the clicked slice so a host can open its bound scenarios (#108)', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const name = wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!
    name.trigger('click')
    const emitted = wrapper.emitted('slice-click')![0][0]
    expect(emitted).toMatchObject({ name: 'WithdrawFunds' })
  })

  it('gives a long label break opportunities instead of clipping it (#180)', () => {
    // A PascalCase name is one unbreakable word to a browser, so the fixed-width card simply cut
    // it off. The label is rendered in segments separated by <wbr>, which is what lets it wrap.
    const model = withdrawFundsModel()
    model.slices![0].elements![1].label = 'DepositMoneyIntoAccount'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const label = wrapper
      .findAll('.em-card-label')
      .find((l) => l.text() === 'DepositMoneyIntoAccount')!
    expect(label.findAll('wbr')).toHaveLength(4)
    // The text itself is untouched — the breaks are opportunities, not inserted characters.
    expect(label.text()).toBe('DepositMoneyIntoAccount')
  })

  it('renders the card at the type scale the width was computed from', () => {
    // layout.ts owns the type scale; a stylesheet with a second opinion about it is a clipped
    // label with no traceable symptom.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const style = wrapper.find('.em-card').attributes('style')!
    expect(style).toContain('font-size: 13px')
    expect(style).toContain('padding: 6px 8px')
  })

  it('keeps a long slice name inside its own column, with the full name on the title', () => {
    const model = withdrawFundsModel()
    model.slices![0].name = 'WithdrawFundsFromAnInternationalAccount'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const name = wrapper.find('.em-slice-name')
    expect(name.attributes('title')).toBe('WithdrawFundsFromAnInternationalAccount')
    // The header row is what the column bounds; the name gives way inside it (#180, #183).
    expect(wrapper.find('.em-slice-header').attributes('style')).toContain('max-width: 580px')
  })

  it('draws a polyline per edge, behind the cards and pointer-inert (#181)', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const lines = wrapper.findAll('.em-edge')
    expect(lines).toHaveLength(4)
    const handlerEdge = lines.find(
      (l) => l.attributes('data-to') === 'WithdrawFunds/Handler/Bank.AccountHandler'
    )!
    expect(handlerEdge.attributes('points')).toBe('180,180 204,180')
    expect(handlerEdge.attributes('marker-end')).toBe('url(#em-arrow)')
  })

  it('draws nothing for an edge whose endpoints were not both drawn', () => {
    const model = withdrawFundsModel()
    model.slices![0].edges!.push({
      fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds',
      toId: 'nope'
    })
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    // A line to nowhere would read as a modelling claim rather than the producer bug it is.
    expect(wrapper.findAll('.em-edge')).toHaveLength(4)
  })

  it('badges each slice with its bound-specification count (#183)', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const badges = wrapper.findAll('.em-slice-specs')
    expect(badges.map((b) => b.text())).toEqual(['1 spec', '1 spec'])
    expect(badges[0].attributes('title')).toBe('Withdraw Funds/a withdrawal succeeds')
  })

  it('spells out a slice with no specification as the drift case, not as a zero', () => {
    const model = withdrawFundsModel()
    model.slices![0].specifications = []
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const badge = wrapper.findAll('.em-slice-specs')[0]
    expect(badge.text()).toBe('no spec')
    expect(badge.attributes('data-outcome')).toBe('none')
  })

  it('carries the run verdict on the badge where evidence named the slice', () => {
    const wrapper = mount(EventModelView, {
      props: {
        descriptor: withdrawFundsModel(),
        sliceOutcomes: { 'Withdraw Funds/a withdrawal succeeds': 'failed' }
      }
    })
    const badge = wrapper.findAll('.em-slice-specs')[0]
    expect(badge.attributes('data-outcome')).toBe('failed')
    // A slice the evidence did not name stays unmarked rather than defaulting to green.
    expect(wrapper.findAll('.em-slice-specs')[1].attributes('data-outcome')).toBeUndefined()
  })

  it('marks each slice with a glyph for its trigger kind (#184)', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const icons = wrapper.findAll('.em-trigger-icon')
    // Only the HTTP-triggered slice declares a kind; the view slice gets no invented glyph.
    expect(icons).toHaveLength(1)
    expect(icons[0].attributes('data-kind')).toBe('Http')
    expect(icons[0].find('title').text()).toBe('HTTP endpoint')
  })

  it('puts the route on the icon tooltip, where it costs no width', () => {
    const model = withdrawFundsModel()
    model.slices![0].triggerOrigin = 'POST /api/accounts/{accountId}/withdrawals'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    expect(wrapper.find('.em-trigger-icon title').text()).toBe(
      'HTTP endpoint · POST /api/accounts/{accountId}/withdrawals'
    )
  })

  it('renders a route trigger label as a method badge plus the path', () => {
    const model = withdrawFundsModel()
    model.slices![0].elements![0].label = 'POST /api/accounts/{accountId}/withdrawals'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const card = wrapper
      .findAll('.em-card')
      .find((c) => c.attributes('data-kind') === 'Trigger')!
    expect(card.find('.em-route-method').text()).toBe('POST')
    expect(card.text()).toContain('/api/accounts/{accountId}/withdrawals')
  })

  it('leaves a human trigger label alone — only a route gets the badge', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const card = wrapper
      .findAll('.em-card')
      .find((c) => c.attributes('data-kind') === 'Trigger')!
    expect(card.find('.em-route-method').exists()).toBe(false)
    expect(card.text()).toBe('Teller screen')
  })

  it('zooms in and out in stops, and clamps at each end (#182)', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const level = () => wrapper.find('.em-zoom-level').text()
    expect(level()).toBe('100%')

    await wrapper.find('.em-zoom-in').trigger('click')
    expect(level()).toBe('125%')
    await wrapper.find('.em-zoom-out').trigger('click')
    await wrapper.find('.em-zoom-out').trigger('click')
    expect(level()).toBe('85%')

    for (let i = 0; i < 10; i++) await wrapper.find('.em-zoom-out').trigger('click')
    expect(level()).toBe('25%')
    expect(wrapper.find('.em-zoom-out').attributes('disabled')).toBeDefined()

    for (let i = 0; i < 20; i++) await wrapper.find('.em-zoom-in').trigger('click')
    expect(level()).toBe('200%')
    expect(wrapper.find('.em-zoom-in').attributes('disabled')).toBeDefined()

    await wrapper.find('.em-zoom-level').trigger('click')
    expect(level()).toBe('100%')
  })

  it('scales the wrapper and sizes its box to match, so the scroller still knows the width', async () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const zoomed = () => wrapper.find('.em-zoomed').attributes('style')!
    // Canvas is 12+132+12+graph.width+12 wide; a transform alone would leave the scroller
    // thinking the canvas was still its 100% self.
    expect(zoomed()).toContain('transform: scale(1)')
    const unscaled = /width: (\d+)px/.exec(zoomed())![1]

    await wrapper.find('.em-zoom-out').trigger('click')
    expect(zoomed()).toContain('transform: scale(0.85)')
    expect(/width: (\d+)px/.exec(zoomed())![1]).toBe(String(Math.round(Number(unscaled) * 0.85)))
  })

  it('fits to the measured viewport width, and never zooms in to fill it', async () => {
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel() },
      attachTo: document.body
    })
    const viewport = wrapper.find('.em-viewport').element
    Object.defineProperty(viewport, 'clientWidth', { value: 500, configurable: true })

    await wrapper.find('.em-zoom-fit').trigger('click')
    // The canvas is 12 + 132 + 12 + 1028 (the graph) + 12 = 1196 wide, so 500 / 1196 = 0.418.
    expect(wrapper.find('.em-zoom-level').text()).toBe('42%')

    Object.defineProperty(viewport, 'clientWidth', { value: 4000, configurable: true })
    await wrapper.find('.em-zoom-fit').trigger('click')
    // A small model blown up to fill the window looks like a mistake, not like a fit.
    expect(wrapper.find('.em-zoom-level').text()).toBe('100%')
    wrapper.unmount()
  })

  it('leaves the zoom alone when the viewport has not been measured yet', async () => {
    // Server-rendered, hidden, or mid-transition: clientWidth 0 must not collapse the canvas.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    await wrapper.find('.em-zoom-fit').trigger('click')
    expect(wrapper.find('.em-zoom-level').text()).toBe('100%')
  })

  it('pans on a drag of the background, and not on a press of a card', async () => {
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel() },
      attachTo: document.body
    })
    const viewport = wrapper.find('.em-viewport')

    await wrapper.find('.em-card').trigger('mousedown', { button: 0 })
    expect(viewport.attributes('data-panning')).toBeUndefined()

    await viewport.trigger('mousedown', { button: 0, clientX: 200, clientY: 100 })
    expect(viewport.attributes('data-panning')).toBe('true')

    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 150, clientY: 100 }))
    expect(viewport.element.scrollLeft).toBe(50)

    window.dispatchEvent(new MouseEvent('mouseup'))
    await wrapper.vm.$nextTick()
    expect(viewport.attributes('data-panning')).toBeUndefined()
    wrapper.unmount()
  })

  it('zooms around the middle of the viewport, not the top-left corner', async () => {
    // transform-origin is 0 0, so a bare zoom change anchors at the far left — on a 41,000px
    // canvas that throws a reader who is looking at slice 80 back to slice 1.
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel() },
      attachTo: document.body
    })
    const viewport = wrapper.find('.em-viewport').element
    Object.defineProperty(viewport, 'clientWidth', { value: 400, configurable: true })
    Object.defineProperty(viewport, 'clientHeight', { value: 300, configurable: true })
    viewport.scrollLeft = 600

    // The middle of the view is at 800 unscaled; at 125% that point sits at 1000, so the offset
    // has to become 1000 - 200 = 800 for it to stay in the middle.
    await wrapper.find('.em-zoom-in').trigger('click')
    await wrapper.vm.$nextTick()
    expect(viewport.scrollLeft).toBe(800)
    wrapper.unmount()
  })

  it('never scrolls to a negative offset when zooming out at the left edge', async () => {
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel() },
      attachTo: document.body
    })
    const viewport = wrapper.find('.em-viewport').element
    Object.defineProperty(viewport, 'clientWidth', { value: 400, configurable: true })
    viewport.scrollLeft = 0

    await wrapper.find('.em-zoom-out').trigger('click')
    await wrapper.vm.$nextTick()
    expect(viewport.scrollLeft).toBe(0)
    wrapper.unmount()
  })

  it('badges a route-named slice with its verb, the way a route card is drawn (#184)', () => {
    // Wolverine names a query slice for its verb and route, so the header was spending its
    // scarcest width on a word the glyph beside it already says.
    const model = withdrawFundsModel()
    model.slices![0].name = 'GET /api/clients/{clientId}/accounts'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const name = wrapper.find('.em-slice-name')

    expect(name.find('.em-route-method').text()).toBe('GET')
    expect(name.text()).toContain('/api/clients/{clientId}/accounts')
    // The name itself is untouched — this is only how it is drawn.
    expect(name.attributes('title')).toBe('GET /api/clients/{clientId}/accounts')
  })

  it('leaves an ordinary slice name alone', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const name = wrapper.find('.em-slice-name')
    expect(name.find('.em-route-method').exists()).toBe(false)
    expect(name.text()).toBe('WithdrawFunds')
  })

  it('opens the drill-down from the spec badge as well as the name (#183)', () => {
    // It names the specifications, so a reader clicks it expecting to see them.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    wrapper.findAll('.em-slice-specs')[1].trigger('click')
    expect(wrapper.emitted('slice-click')![0][0]).toMatchObject({ name: 'AccountBalance' })
  })

  it('renders the four lane captions', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    expect(wrapper.findAll('.em-lane-label').map((l) => l.text())).toEqual([
      'Wireframe / Trigger',
      'Command',
      'Event Stream',
      'Read Model'
    ])
  })

  it('colours a slice from run evidence keyed by spec identity (#107)', () => {
    const wrapper = mount(EventModelView, {
      props: {
        descriptor: withdrawFundsModel(),
        sliceOutcomes: { 'Withdraw Funds/a withdrawal succeeds': 'failed' }
      }
    })
    const slice = wrapper.find('[data-slice="WithdrawFunds"]')
    expect(slice.attributes('data-outcome')).toBe('failed')
    // A slice no run evidence named stays unmarked rather than defaulting to green.
    expect(wrapper.find('[data-slice="AccountBalance"]').attributes('data-outcome')).toBeUndefined()
  })

  it('hides a collapsed slice cards but keeps its column', () => {
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel(), collapsedSlices: new Set(['WithdrawFunds']) }
    })
    expect(wrapper.findAll('.em-card')).toHaveLength(3)
    expect(wrapper.find('[data-slice="WithdrawFunds"]').exists()).toBe(true)
  })
})

/**
 * jasperfx#703 / #704 — the canvas draws WHERE a claim came from, and draws a dropped claim as a
 * finding rather than as another magenta sticky.
 */
describe('EventModelView — provenance and source disagreement', () => {
  function cards() {
    return mount(EventModelView, { props: { descriptor: fourSourceModel() } }).findAll('.em-card')
  }

  it('stamps the ladder rung on every card that carries one', () => {
    const byLabel = new Map(cards().map((c) => [c.text(), c.attributes('data-provenance')]))

    expect(byLabel.get('Teller screen')).toBe('Declared')
    expect(byLabel.get('WithdrawFunds')).toBe('Derived')
    expect(byLabel.get('FundsWithdrawn')).toBe('Observed')
    expect(byLabel.get('AuditRecorded')).toBe('Observed')
  })

  it('leaves the fill colour alone — it means the KIND, in every viewer', () => {
    // The second visual channel must not touch the first: two viewers agreeing on what a colour
    // means is the whole reason this package exists, so an Observed event is still event-orange.
    const observed = cards().find((c) => c.text() === 'FundsWithdrawn')!
    expect(observed.attributes('style')).toContain('background: #F5A623')
  })

  it('omits the attribute entirely on a descriptor from a pre-2.56 producer', () => {
    // Absent, not 'Declared' — the viewer must not invent a rung nobody claimed.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    expect(
      wrapper.findAll('.em-card').every((c) => c.attributes('data-provenance') === undefined)
    ).toBe(true)
  })

  it('marks a source disagreement apart from a pending-specification hotspot', () => {
    const all = cards()
    const pending = all.find((c) => c.text().includes('overdraft not specified'))!
    const conflict = all.find((c) => c.attributes('data-hotspot-origin') === 'SourceDisagreement')!

    expect(pending.attributes('data-hotspot-origin')).toBe('PendingSpecification')
    expect(conflict.attributes('data-hotspot-origin')).toBe('SourceDisagreement')
  })

  it('puts both claims on the tooltip, so the reader can decide which source to fix', () => {
    const conflict = cards().find(
      (c) => c.attributes('data-hotspot-origin') === 'SourceDisagreement'
    )!
    const title = conflict.attributes('title')!

    expect(title).toContain('Kept: Observed claims FundsWithdrawn, AuditRecorded')
    expect(title).toContain('Dropped: Derived claims FundsWithdrawn')
  })

  it('renders a disagreement as a finding, not as a sentence (#178)', () => {
    // The card used to lead with the role name and a clipped sentence, and the reviewer who
    // designed the feature read it as a malformed events list. It now says what it is.
    const conflict = cards().find(
      (c) => c.attributes('data-hotspot-origin') === 'SourceDisagreement'
    )!

    expect(conflict.find('.em-hotspot-origin').text()).toBe('Sources disagree')
    expect(conflict.find('.em-hotspot-role').text()).toBe('EmittedEvents')

    const claims = conflict.findAll('.em-hotspot-claim')
    expect(claims.map((c) => c.attributes('data-claim'))).toEqual(['kept', 'dropped'])
    // Kept first, each with the rung that claimed it — the ladder is the reason one won.
    expect(claims[0].text()).toBe('Observed FundsWithdrawn, AuditRecorded')
    expect(claims[1].text()).toBe('Derived FundsWithdrawn')
  })

  it('names what kind of finding an ordinary hotspot is, then states it', () => {
    const pending = cards().find(
      (c) => c.attributes('data-hotspot-origin') === 'PendingSpecification'
    )!
    expect(pending.find('.em-hotspot-origin').text()).toBe('Pending spec')
    expect(pending.find('.em-hotspot-text').text()).toBe('overdraft not specified')
    expect(pending.find('.em-hotspot-claim').exists()).toBe(false)
  })

  it('degrades a disagreement with no surviving claim pair to its text', () => {
    // Half a finding — one claim, no counterpart — would be worse than the sentence it replaced.
    const model = fourSourceModel()
    for (const slice of model.slices ?? []) {
      for (const hotspot of slice.hotspots ?? []) {
        if (hotspot.origin === 'SourceDisagreement') hotspot.losingClaim = null
      }
    }
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const conflict = wrapper
      .findAll('.em-card')
      .find((c) => c.attributes('data-hotspot-origin') === 'SourceDisagreement')!
    expect(conflict.find('.em-hotspot-claim').exists()).toBe(false)
    expect(conflict.find('.em-hotspot-text').text()).toContain('EmittedEvents')
  })

  it('explains the rung on the tooltip of an ordinary card', () => {
    const observed = cards().find((c) => c.text() === 'AuditRecorded')!
    const title = observed.attributes('title')!

    expect(title).toContain('Bank.AuditRecorded')
    expect(title).toContain('seen happening in a running system')
  })

  it('does not mark a non-hotspot card with a hotspot origin', () => {
    expect(
      cards()
        .filter((c) => c.attributes('data-kind') !== 'Hotspot')
        .every((c) => c.attributes('data-hotspot-origin') === undefined)
    ).toBe(true)
  })
})

describe('EventModelView stream rows (#299)', () => {
  it('captions each stream in the gutter and tints alternate rows', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: twoAggregateModel() } })
    const captions = wrapper.findAll('.em-stream-row-label')
    expect(captions.map((c) => c.text())).toEqual(['Account', 'Wallet', '—'])
    // The unlabelled row says what it is on hover rather than in the gutter, where a phrase would
    // be wider than the two type names it sits under.
    expect(captions[2].attributes('title')).toContain('On no aggregate stream')
    expect(wrapper.findAll('.em-stream-band')).toHaveLength(3)
    expect(wrapper.findAll('.em-stream-band[data-alt]')).toHaveLength(1)
  })

  it('draws no row chrome at all for a model the layout did not split', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    expect(wrapper.findAll('.em-stream-row-label')).toHaveLength(0)
    expect(wrapper.findAll('.em-stream-band')).toHaveLength(0)
  })

  it('marks the split lane so its own caption moves to the top of the band', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: twoAggregateModel() } })
    const split = wrapper.findAll('.em-lane-label').filter((l) => l.attributes('data-split'))
    expect(split.map((l) => l.text())).toEqual(['Event Stream'])
  })
})
