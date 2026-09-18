import type { EventModelDescriptor } from '../types'

/**
 * A two-slice model shaped like what the Bobcat generator will emit from a `.feature` (issue
 * #106): a command slice with a trigger, command, handler, aggregate and two events, and a view
 * slice folding one of those events into a read model.
 */
export function withdrawFundsModel(): EventModelDescriptor {
  return {
    name: 'Banking',
    slices: [
      {
        name: 'WithdrawFunds',
        domain: 'Accounts',
        pattern: 'Command',
        triggerKind: 'Http',
        triggerLabel: 'Teller screen',
        elements: [
          { id: 'WithdrawFunds/Trigger/Teller screen', kind: 'Trigger', lane: 'Wireframe', label: 'Teller screen' },
          { id: 'WithdrawFunds/Command/Bank.WithdrawFunds', kind: 'Command', lane: 'Command', label: 'WithdrawFunds', type: { name: 'WithdrawFunds', fullName: 'Bank.WithdrawFunds' } },
          { id: 'WithdrawFunds/Handler/Bank.AccountHandler', kind: 'Handler', lane: 'Command', label: 'AccountHandler', type: { name: 'AccountHandler', fullName: 'Bank.AccountHandler' } },
          { id: 'WithdrawFunds/Aggregate/Bank.Account', kind: 'Aggregate', lane: 'Command', label: 'Account', type: { name: 'Account', fullName: 'Bank.Account' } },
          { id: 'WithdrawFunds/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' } },
          { id: 'WithdrawFunds/Event/Bank.AccountOverdrawn', kind: 'Event', lane: 'EventStream', label: 'AccountOverdrawn', type: { name: 'AccountOverdrawn', fullName: 'Bank.AccountOverdrawn' } }
        ],
        edges: [
          { fromId: 'WithdrawFunds/Trigger/Teller screen', toId: 'WithdrawFunds/Command/Bank.WithdrawFunds' },
          { fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds', toId: 'WithdrawFunds/Handler/Bank.AccountHandler' },
          { fromId: 'WithdrawFunds/Handler/Bank.AccountHandler', toId: 'WithdrawFunds/Event/Bank.FundsWithdrawn' }
        ],
        specifications: [
          { identity: 'Withdraw Funds/a withdrawal succeeds', feature: 'Withdraw Funds', scenario: 'a withdrawal succeeds' }
        ]
      },
      {
        name: 'AccountBalance',
        domain: 'Accounts',
        pattern: 'View',
        elements: [
          { id: 'AccountBalance/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' } },
          { id: 'AccountBalance/Projection/Bank.BalanceProjection', kind: 'Projection', lane: 'ReadModel', label: 'BalanceProjection', type: { name: 'BalanceProjection', fullName: 'Bank.BalanceProjection' } },
          { id: 'AccountBalance/ReadModel/Bank.Balance', kind: 'ReadModel', lane: 'ReadModel', label: 'Balance', type: { name: 'Balance', fullName: 'Bank.Balance' } }
        ],
        edges: [
          { fromId: 'AccountBalance/Event/Bank.FundsWithdrawn', toId: 'AccountBalance/Projection/Bank.BalanceProjection' }
        ],
        specifications: [{ identity: 'Account Balance/balance reflects a withdrawal' }],
        hotspots: [
          { origin: 'PendingSpecification', text: 'overdraft not specified', specificationIdentity: 'Account Balance/overdraft' }
        ]
      }
    ]
  }
}

/**
 * jasperfx#703 / #704 — a slice as it comes out of a FOUR-SOURCE merge: a Gherkin spec and the C#
 * overlay declared it, Wolverine's chains derived it, and CritterWatch observed it in production.
 *
 * The interesting bit is the disagreement. The code says this slice emits `FundsWithdrawn`;
 * production says it appends `FundsWithdrawn` **and** `AuditRecorded`. Observed is the higher rung
 * so it wins outright — a higher rung REPLACES a list rather than unioning with it — and the
 * dropped claim survives as a `SourceDisagreement` hotspot instead of vanishing.
 *
 * Element shapes match what `EventModelSliceDescriptor.buildGraph` emits: every element carries the
 * effective rung for its role, and each hotspot is projected into a `Hotspot` element whose LABEL
 * is the hotspot's text — which is the only join a viewer has back to the origin.
 */
export function fourSourceModel(): EventModelDescriptor {
  const disagreement =
    'EmittedEvents: Observed claims FundsWithdrawn, AuditRecorded; Derived claims FundsWithdrawn'

  return {
    name: 'Banking',
    slices: [
      {
        name: 'WithdrawFunds',
        domain: 'Accounts',
        pattern: 'Command',
        elements: [
          // Declared — nothing else claims a trigger label.
          { id: 'WithdrawFunds/Trigger/Teller screen', kind: 'Trigger', lane: 'Wireframe', label: 'Teller screen', provenance: 'Declared' },
          { id: 'WithdrawFunds/Hotspot/pending', kind: 'Hotspot', lane: 'Wireframe', label: 'overdraft not specified', provenance: 'Declared' },
          { id: `WithdrawFunds/Hotspot/${disagreement}`, kind: 'Hotspot', lane: 'Wireframe', label: disagreement, provenance: 'Declared' },
          // Derived — read out of the Wolverine chain.
          { id: 'WithdrawFunds/Command/Bank.WithdrawFunds', kind: 'Command', lane: 'Command', label: 'WithdrawFunds', type: { name: 'WithdrawFunds', fullName: 'Bank.WithdrawFunds' }, provenance: 'Derived' },
          { id: 'WithdrawFunds/Handler/Bank.AccountHandler', kind: 'Handler', lane: 'Command', label: 'AccountHandler', type: { name: 'AccountHandler', fullName: 'Bank.AccountHandler' }, provenance: 'Derived' },
          // Observed — production appended both, and that is the claim that won.
          { id: 'WithdrawFunds/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' }, provenance: 'Observed' },
          { id: 'WithdrawFunds/Event/Bank.AuditRecorded', kind: 'Event', lane: 'EventStream', label: 'AuditRecorded', type: { name: 'AuditRecorded', fullName: 'Bank.AuditRecorded' }, provenance: 'Observed' }
        ],
        edges: [],
        hotspots: [
          { origin: 'PendingSpecification', text: 'overdraft not specified', specificationIdentity: 'Account Balance/overdraft' },
          {
            origin: 'SourceDisagreement',
            text: disagreement,
            role: 'EmittedEvents',
            winningClaim: { provenance: 'Observed', value: 'FundsWithdrawn, AuditRecorded' },
            losingClaim: { provenance: 'Derived', value: 'FundsWithdrawn' }
          }
        ]
      }
    ]
  }
}

/**
 * jasperfx#823 — the same two slices, plus the cross-slice `links` the descriptor computes on read
 * and a third slice linked to neither.
 *
 * `WithdrawFunds` emits `FundsWithdrawn`; `AccountBalance` folds it. That is one `EventTriggers`
 * link and therefore a neighbourhood of two, which is what focus (#296) has to fit. `SendWelcome`
 * is the control: it must stay outside the neighbourhood and get dimmed.
 *
 * ⚠️ No producer on the JasperFx this repo pins can emit this document — `Links` arrives in 2.69.
 * It exists so focus-with-neighbourhood is covered at all; `withdrawFundsModel()` is the shape the
 * pin can actually produce, and the degraded path is tested against that.
 */
export function linkedModel(): EventModelDescriptor {
  const model = withdrawFundsModel()
  model.slices!.push({
    name: 'SendWelcome',
    domain: 'Onboarding',
    pattern: 'Automation',
    elements: [
      {
        id: 'SendWelcome/Message/Bank.WelcomeEmail',
        kind: 'Message',
        lane: 'Command',
        label: 'WelcomeEmail',
        type: { name: 'WelcomeEmail', fullName: 'Bank.WelcomeEmail' }
      }
    ],
    edges: []
  })
  model.links = [
    {
      fromSlice: 'WithdrawFunds',
      fromElementId: 'WithdrawFunds/Event/Bank.FundsWithdrawn',
      toSlice: 'AccountBalance',
      toElementId: 'AccountBalance/Event/Bank.FundsWithdrawn',
      kind: 'EventTriggers',
      via: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' }
    }
  ]
  return model
}

/**
 * A model the size of the one that made #296 necessary.
 *
 * The 2026-08-31 review measured CritterWatch's merged fleet model at 106 slices and ~10,000px at
 * the 25% zoom floor. A fixture of the same order is the only honest way to test a feature whose
 * whole justification is that size — `withdrawFundsModel()` fits on a laptop screen at 100%, so it
 * cannot fail any of these cases.
 *
 * Six elements a slice, cycling through four domains, and a chain of links down the declaration
 * order so every slice but the ends has exactly two neighbours.
 */
export function largeModel(sliceCount = 106, aggregateCount = 0): EventModelDescriptor {
  const domains = ['Accounts', 'Payments', 'Onboarding', 'Reporting']
  const slices: EventModelDescriptor['slices'] = []
  const links: NonNullable<EventModelDescriptor['links']> = []
  // #299 — a fleet-sized model whose slices are spread across a handful of streams, which is the
  // only shape that says anything about what the split costs at 106 slices.
  const aggregates = Array.from({ length: aggregateCount }, (_, index) => ({
    name: `Aggregate${index}`,
    fullName: `Bank.Aggregate${index}`
  }))

  for (let index = 0; index < sliceCount; index++) {
    const name = `Slice${String(index).padStart(3, '0')}`
    const aggregate = aggregates.length > 0 ? aggregates[index % aggregates.length] : null
    slices.push({
      name,
      domain: domains[index % domains.length],
      aggregateTypes: aggregate ? [aggregate] : undefined,
      pattern: index % 3 === 0 ? 'View' : 'Command',
      triggerKind: 'Http',
      elements: [
        { id: `${name}/Trigger/screen`, kind: 'Trigger', lane: 'Wireframe', label: `${name}Screen` },
        { id: `${name}/Command/Bank.${name}`, kind: 'Command', lane: 'Command', label: name, type: { name, fullName: `Bank.${name}` } },
        { id: `${name}/Handler/Bank.${name}Handler`, kind: 'Handler', lane: 'Command', label: `${name}Handler` },
        { id: `${name}/Event/Bank.${name}Happened`, kind: 'Event', lane: 'EventStream', label: `${name}Happened` },
        { id: `${name}/Projection/Bank.${name}Projection`, kind: 'Projection', lane: 'ReadModel', label: `${name}Projection` },
        { id: `${name}/ReadModel/Bank.${name}View`, kind: 'ReadModel', lane: 'ReadModel', label: `${name}View` }
      ],
      edges: [
        { fromId: `${name}/Trigger/screen`, toId: `${name}/Command/Bank.${name}` },
        { fromId: `${name}/Command/Bank.${name}`, toId: `${name}/Handler/Bank.${name}Handler` },
        { fromId: `${name}/Handler/Bank.${name}Handler`, toId: `${name}/Event/Bank.${name}Happened` }
      ],
      specifications: index % 5 === 0 ? [{ identity: `${name}/happy path` }] : []
    })

    if (index > 0) {
      const previous = `Slice${String(index - 1).padStart(3, '0')}`
      links.push({
        fromSlice: previous,
        fromElementId: `${previous}/Event/Bank.${previous}Happened`,
        toSlice: name,
        toElementId: `${name}/Command/Bank.${name}`,
        kind: 'EventTriggers',
        via: { name: `${previous}Happened`, fullName: `Bank.${previous}Happened` }
      })
    }
  }

  return {
    name: 'Fleet',
    slices,
    links,
    aggregates: aggregates.map((type) => ({ type, kind: 'WriteAggregate' as const, appliedEvents: [] }))
  }
}

/**
 * bobcat#299 — a model with two aggregate streams, which is the smallest model the stream-row
 * split has anything to say about.
 *
 * Every rule of `streamRowPlan` has a slice here: `WithdrawFunds` and `TopUpWallet` each name one
 * aggregate, `SettleTransfer` names both (and one of its events is in neither aggregate's
 * `appliedEvents`, so it falls back to the first), and `AccountBalance` names none. The published
 * message is the other half of the unlabelled row.
 *
 * `aggregates` carries `appliedEvents` deliberately: without it a multi-aggregate slice can only
 * fall back, and the fallback is exactly the path that must not be the only one tested.
 */
export function twoAggregateModel(): EventModelDescriptor {
  const account = { name: 'Account', fullName: 'Bank.Account' }
  const wallet = { name: 'Wallet', fullName: 'Bank.Wallet' }

  return {
    name: 'Banking',
    aggregates: [
      {
        type: account,
        kind: 'WriteAggregate',
        appliedEvents: [
          { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' },
          { name: 'AccountOverdrawn', fullName: 'Bank.AccountOverdrawn' }
        ]
      },
      {
        type: wallet,
        kind: 'WriteAggregate',
        appliedEvents: [{ name: 'WalletToppedUp', fullName: 'Bank.WalletToppedUp' }]
      }
    ],
    slices: [
      {
        name: 'WithdrawFunds',
        domain: 'Accounts',
        pattern: 'Command',
        aggregateTypes: [account],
        elements: [
          { id: 'WithdrawFunds/Command/Bank.WithdrawFunds', kind: 'Command', lane: 'Command', label: 'WithdrawFunds', type: { name: 'WithdrawFunds', fullName: 'Bank.WithdrawFunds' } },
          { id: 'WithdrawFunds/Aggregate/Bank.Account', kind: 'Aggregate', lane: 'Command', label: 'Account', type: account },
          { id: 'WithdrawFunds/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' } },
          { id: 'WithdrawFunds/Event/Bank.AccountOverdrawn', kind: 'Event', lane: 'EventStream', label: 'AccountOverdrawn', type: { name: 'AccountOverdrawn', fullName: 'Bank.AccountOverdrawn' } }
        ],
        edges: [
          { fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds', toId: 'WithdrawFunds/Event/Bank.FundsWithdrawn' }
        ]
      },
      {
        name: 'TopUpWallet',
        domain: 'Wallets',
        pattern: 'Command',
        aggregateTypes: [wallet],
        elements: [
          { id: 'TopUpWallet/Command/Bank.TopUpWallet', kind: 'Command', lane: 'Command', label: 'TopUpWallet', type: { name: 'TopUpWallet', fullName: 'Bank.TopUpWallet' } },
          { id: 'TopUpWallet/Aggregate/Bank.Wallet', kind: 'Aggregate', lane: 'Command', label: 'Wallet', type: wallet },
          { id: 'TopUpWallet/Event/Bank.WalletToppedUp', kind: 'Event', lane: 'EventStream', label: 'WalletToppedUp', type: { name: 'WalletToppedUp', fullName: 'Bank.WalletToppedUp' } },
          { id: 'TopUpWallet/Message/Bank.WalletTopUpNotified', kind: 'Message', lane: 'EventStream', label: 'WalletTopUpNotified', type: { name: 'WalletTopUpNotified', fullName: 'Bank.WalletTopUpNotified' } }
        ],
        edges: []
      },
      {
        name: 'SettleTransfer',
        domain: 'Accounts',
        pattern: 'Command',
        aggregateTypes: [account, wallet],
        elements: [
          { id: 'SettleTransfer/Event/Bank.WalletToppedUp', kind: 'Event', lane: 'EventStream', label: 'WalletToppedUp', type: { name: 'WalletToppedUp', fullName: 'Bank.WalletToppedUp' } },
          { id: 'SettleTransfer/Event/Bank.TransferSettled', kind: 'Event', lane: 'EventStream', label: 'TransferSettled', type: { name: 'TransferSettled', fullName: 'Bank.TransferSettled' } }
        ],
        edges: []
      },
      {
        name: 'AccountBalance',
        domain: 'Accounts',
        pattern: 'View',
        elements: [
          { id: 'AccountBalance/Event/Bank.FundsWithdrawn', kind: 'Event', lane: 'EventStream', label: 'FundsWithdrawn', type: { name: 'FundsWithdrawn', fullName: 'Bank.FundsWithdrawn' } },
          { id: 'AccountBalance/ReadModel/Bank.Balance', kind: 'ReadModel', lane: 'ReadModel', label: 'Balance', type: { name: 'Balance', fullName: 'Bank.Balance' } }
        ],
        edges: [
          { fromId: 'AccountBalance/Event/Bank.FundsWithdrawn', toId: 'AccountBalance/ReadModel/Bank.Balance' }
        ]
      }
    ]
  }
}

/**
 * bobcat#295 — the three-slice model the link router is pinned against: one event with two
 * consumers, which is the case bundling exists for.
 *
 * `OpenAccount` emits `AccountOpened`; `AccountBalance` folds it into a view and `SendWelcome`
 * reacts to it. Both links leave the SAME element, so they share one trunk and one track.
 */
export function linkedThreeSliceModel(): EventModelDescriptor {
  return {
    name: 'Banking',
    slices: [
      {
        name: 'OpenAccount',
        domain: 'Accounts',
        pattern: 'Command',
        elements: [
          { id: 'OpenAccount/Command/Bank.OpenAccount', kind: 'Command', lane: 'Command', label: 'OpenAccount', type: { name: 'OpenAccount', fullName: 'Bank.OpenAccount' } },
          { id: 'OpenAccount/Event/Bank.AccountOpened', kind: 'Event', lane: 'EventStream', label: 'AccountOpened', type: { name: 'AccountOpened', fullName: 'Bank.AccountOpened' } }
        ],
        edges: []
      },
      {
        name: 'AccountBalance',
        domain: 'Accounts',
        pattern: 'View',
        elements: [
          { id: 'AccountBalance/Event/Bank.AccountOpened', kind: 'Event', lane: 'EventStream', label: 'AccountOpened', type: { name: 'AccountOpened', fullName: 'Bank.AccountOpened' } },
          { id: 'AccountBalance/ReadModel/Bank.Balance', kind: 'ReadModel', lane: 'ReadModel', label: 'Balance', type: { name: 'Balance', fullName: 'Bank.Balance' } }
        ],
        edges: []
      },
      {
        name: 'SendWelcome',
        domain: 'Onboarding',
        pattern: 'Automation',
        elements: [
          { id: 'SendWelcome/Command/Bank.SendWelcome', kind: 'Command', lane: 'Command', label: 'SendWelcome', type: { name: 'SendWelcome', fullName: 'Bank.SendWelcome' } }
        ],
        edges: []
      }
    ],
    links: [
      {
        fromSlice: 'OpenAccount',
        fromElementId: 'OpenAccount/Event/Bank.AccountOpened',
        toSlice: 'AccountBalance',
        toElementId: 'AccountBalance/Event/Bank.AccountOpened',
        kind: 'EventTriggers',
        via: { name: 'AccountOpened', fullName: 'Bank.AccountOpened' }
      },
      {
        fromSlice: 'OpenAccount',
        fromElementId: 'OpenAccount/Event/Bank.AccountOpened',
        toSlice: 'SendWelcome',
        toElementId: 'SendWelcome/Command/Bank.SendWelcome',
        kind: 'EventTriggers',
        via: { name: 'AccountOpened', fullName: 'Bank.AccountOpened' }
      }
    ]
  }
}

/**
 * Four slices in two chapters, with the second chapter INTERLEAVED (#298): Onboarding, Onboarding,
 * Swiping, Onboarding. Declaration order is the producer's statement about sequence and the canvas
 * never reorders, so this draws three bands — two of them named Onboarding — rather than two.
 * `Loose` declares no chapter and sits under no band.
 */
export function chapteredModel(): EventModelDescriptor {
  const command = (slice: string, chapter: string | null) => ({
    name: slice,
    domain: 'Dating',
    chapter,
    pattern: 'Command' as const,
    elements: [
      {
        id: `${slice}/Command/App.${slice}`,
        kind: 'Command' as const,
        lane: 'Command' as const,
        label: slice,
        type: { name: slice, fullName: `App.${slice}` }
      }
    ],
    edges: []
  })
  return {
    name: 'K9Crush',
    slices: [
      command('Enroll', 'Onboarding'),
      command('AddDog', 'Onboarding'),
      command('SwipeOnDog', 'Swiping'),
      command('Verify', 'Onboarding'),
      command('Loose', null)
    ]
  }
}
