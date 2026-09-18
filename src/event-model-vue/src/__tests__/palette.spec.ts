import { describe, expect, it } from 'vitest'
import { DASHED_KINDS, EVENT_MODEL_PALETTE, OUTLINED_KINDS, colorFor, inkFor } from '../palette'

/**
 * Pins every value against `JasperFx.Events.EventModeling.EventModelPalette.ColorFor`
 * (JasperFx.Events 2.54.0). A drifted colour looks fine in isolation and only surfaces as
 * "CritterWatch and the console disagree", which is the exact failure this package prevents.
 */
describe('EVENT_MODEL_PALETTE', () => {
  it('matches the canonical JasperFx sticky-note colours', () => {
    expect(EVENT_MODEL_PALETTE).toEqual({
      Trigger: '#FFFFFF',
      Command: '#5B9BD5',
      Handler: '#5B9BD5',
      Aggregate: '#FFF2A8',
      Event: '#F5A623',
      Message: '#5B9BD5',
      Projection: '#7ED321',
      ReadModel: '#7ED321',
      ExternalSystem: '#F8BBD0',
      Hotspot: '#E91E63'
    })
  })

  it('falls back to the upstream neutral for a kind it does not know', () => {
    expect(colorFor('Speculative' as never)).toBe('#CCCCCC')
  })

  it('draws handlers and projections outlined, and non-event messages dashed', () => {
    // Upstream documents these as treatments rather than fills, so a handler and its command
    // share #5B9BD5 without becoming indistinguishable.
    expect([...OUTLINED_KINDS]).toEqual(['Handler', 'Projection'])
    expect([...DASHED_KINDS]).toEqual(['Message'])
  })

  it('uses dark ink on the two pale stickies', () => {
    expect(inkFor('Trigger')).toBe('#1F2933')
    expect(inkFor('Aggregate')).toBe('#1F2933')
    expect(inkFor('Event')).toBe('#0B1F33')
  })
})
