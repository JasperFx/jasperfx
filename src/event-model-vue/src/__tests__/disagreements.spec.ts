import { describe, expect, it } from 'vitest'
import {
  disagreementElementIds,
  disagreementLines,
  isDisagreementElement,
  placeDisagreements
} from '../disagreements'
import { layoutEventModel } from '../layout'
import type { EventModelSliceDescriptor, HotspotDescriptor } from '../types'
import { fourSourceModel } from './fixtures'

/**
 * jasperfx#1084 — a source disagreement is drawn as a badge on the card it disputes, so these pin
 * which card that is.
 */
function slice(): EventModelSliceDescriptor {
  return fourSourceModel().slices![0]
}

function disagreement(patch: Partial<HotspotDescriptor>): HotspotDescriptor {
  return {
    origin: 'SourceDisagreement',
    text: `${patch.role}: disagreement`,
    winningClaim: { provenance: 'Observed', value: 'x' },
    losingClaim: { provenance: 'Derived', value: 'y' },
    ...patch
  }
}

describe('disagreements (jasperfx#1084)', () => {
  it('finds the sticky a disagreement was projected into, and only that one', () => {
    const s = slice()
    const stickies = (s.elements ?? []).filter((e) => isDisagreementElement(s, e)).map((e) => e.id)

    expect(stickies).toHaveLength(1)
    expect(stickies[0]).toContain('EmittedEvents')
    // A pending spec is a finding and keeps its sticky.
    expect(disagreementElementIds(s).has('WithdrawFunds/Hotspot/pending')).toBe(false)
  })

  it('marks the cards a list disagreement names', () => {
    const placement = placeDisagreements(slice())

    expect([...placement.byElementId.keys()].sort()).toEqual([
      'WithdrawFunds/Event/Bank.AuditRecorded',
      'WithdrawFunds/Event/Bank.FundsWithdrawn'
    ])
    expect(placement.header).toEqual([])
  })

  it('leaves the cards neither claim names clean', () => {
    const s = slice()
    s.hotspots = [
      disagreement({
        role: 'EmittedEvents',
        winningClaim: { provenance: 'Observed', value: 'AuditRecorded' },
        losingClaim: { provenance: 'Derived', value: '' }
      })
    ]

    expect([...placeDisagreements(s).byElementId.keys()]).toEqual(['WithdrawFunds/Event/Bank.AuditRecorded'])
  })

  it('marks every card of the kind when neither claim names one here', () => {
    const s = slice()
    s.hotspots = [disagreement({ role: 'EmittedEvents' })]

    expect(placeDisagreements(s).byElementId.size).toBe(2)
  })

  it('marks the handler card for a handler disagreement', () => {
    const s = slice()
    s.hotspots = [
      disagreement({
        role: 'HandlerType',
        winningClaim: { provenance: 'Derived', value: 'AccountHandler' },
        losingClaim: { provenance: 'Observed', value: 'AccountHandler939323516' }
      })
    ]

    expect([...placeDisagreements(s).byElementId.keys()]).toEqual(['WithdrawFunds/Handler/Bank.AccountHandler'])
  })

  it('puts a role with no card on the slice header', () => {
    const s = slice()
    const origin = disagreement({ role: 'Origin' })
    const kind = disagreement({ role: 'TriggerKind' })
    const unattributed = disagreement({ role: null })
    s.hotspots = [origin, kind, unattributed]

    const placement = placeDisagreements(s)
    expect(placement.byElementId.size).toBe(0)
    expect(placement.header).toEqual([origin, kind, unattributed])
  })

  it('falls back to the header when the disputed cards are not on the slice', () => {
    const s = slice()
    s.hotspots = [disagreement({ role: 'ReadModelTypes' })]

    expect(placeDisagreements(s).header).toHaveLength(1)
  })

  it('describes each claim with its rung, and its source when the source named itself', () => {
    const [line] = disagreementLines([
      disagreement({
        role: 'HandlerType',
        winningClaim: { provenance: 'Observed', value: 'StartTripHandler', source: 'critterwatch://observed/TripService' },
        losingClaim: { provenance: 'Derived', value: 'TripHandler' }
      })
    ])

    expect(line).toBe(
      'HandlerType: kept Observed (critterwatch://observed/TripService) StartTripHandler; dropped Derived TripHandler'
    )
  })

  it('falls back to the text when the claim pair did not survive the wire', () => {
    expect(disagreementLines([disagreement({ role: 'Origin', losingClaim: null, text: 'the text' })])).toEqual([
      'the text'
    ])
  })

  it('lays out no card for a badged disagreement, and the old card when asked for stickies', () => {
    const ids = (presentation?: 'badge' | 'sticky') =>
      layoutEventModel(fourSourceModel(), { disagreements: presentation }).nodes.map((n) => n.id)

    expect(ids().some((id) => id.includes('EmittedEvents'))).toBe(false)
    expect(ids()).toContain('WithdrawFunds/Hotspot/pending')
    expect(ids('sticky').some((id) => id.includes('EmittedEvents'))).toBe(true)
  })
})
