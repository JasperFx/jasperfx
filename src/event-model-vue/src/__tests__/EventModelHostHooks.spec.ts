import { mount } from '@vue/test-utils'
import { h } from 'vue'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import type { EventModelSliceDescriptor } from '../types'
import { withdrawFundsModel } from './fixtures'

/**
 * Two optional hooks for a host with more to say than the descriptor.
 * Both are additive: a host that uses neither (Bobcat) renders exactly what it did.
 */
describe('host hooks', () => {
  describe('the card-badge slot', () => {
    it('draws nothing extra when the host does not use it', () => {
      const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
      expect(wrapper.findAll('[data-testid="card-badge"]')).toHaveLength(0)
    })

    it('hands the slot each card with its slice, and draws the badge inside that card', () => {
      const seen: string[] = []
      const wrapper = mount(EventModelView, {
        props: { descriptor: withdrawFundsModel() },
        slots: {
          'card-badge': ({ element, slice, sliceName }: { element: { kind: string; label: string }; slice: EventModelSliceDescriptor; sliceName: string }) => {
            seen.push(`${sliceName}/${slice.name}/${element.kind}`)
            return element.kind === 'Event' ? h('span', { class: 'reading' }, '12/min') : null
          }
        }
      })

      const event = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Event')!
      expect(event.find('[data-testid="card-badge"] .reading').text()).toBe('12/min')
      expect(seen).toContain('WithdrawFunds/WithdrawFunds/Event')
      expect(seen.length).toBe(wrapper.findAll('.em-card').length)
    })

    it('leaves a card the host has nothing for with an empty badge, which the stylesheet hides', () => {
      const wrapper = mount(EventModelView, {
        props: { descriptor: withdrawFundsModel() },
        slots: { 'card-badge': () => null }
      })
      for (const badge of wrapper.findAll('[data-testid="card-badge"]')) expect(badge.text()).toBe('')
    })

    it('is part of its card: a click on the badge is a click on the card', async () => {
      const wrapper = mount(EventModelView, {
        props: { descriptor: withdrawFundsModel() },
        slots: { 'card-badge': () => h('span', { class: 'reading' }, '!') }
      })
      const command = wrapper.findAll('.em-card').find((c) => c.attributes('data-kind') === 'Command')!
      await command.find('.reading').trigger('click')
      expect(wrapper.emitted('element-click')![0][0]).toMatchObject({ kind: 'Command', label: 'WithdrawFunds' })
    })
  })

  describe('sliceLabel', () => {
    it('reads the name when the host gives no label', () => {
      const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
      expect(wrapper.find('.em-slice-name').text()).toBe('WithdrawFunds')
    })

    it('says the host label in the header while the slice stays keyed by its name', async () => {
      const model = withdrawFundsModel()
      model.slices![0].name = 'Banking/WithdrawFunds'
      const wrapper = mount(EventModelView, {
        props: { descriptor: model, sliceLabel: (s: EventModelSliceDescriptor) => s.name.split('/')[1]! }
      })

      const header = wrapper.find('.em-slice-name')
      expect(header.text()).toBe('WithdrawFunds')
      expect(header.attributes('title')).toBe('WithdrawFunds')
      expect(wrapper.find('[data-slice="Banking/WithdrawFunds"]').exists()).toBe(true)

      await header.trigger('click')
      expect(wrapper.emitted('slice-click')![0][0]).toMatchObject({ name: 'Banking/WithdrawFunds' })
    })

    it('gives a labelled route the method badge, as it gives a named one', () => {
      const model = withdrawFundsModel()
      model.slices![0].name = 'Banking/POST /accounts/{id}/withdraw'
      const wrapper = mount(EventModelView, {
        props: { descriptor: model, sliceLabel: (s: EventModelSliceDescriptor) => s.name.slice('Banking/'.length) }
      })
      expect(wrapper.find('.em-slice-name .em-route-method').text()).toBe('POST')
    })
  })
})
