import { describe, it, expect } from 'vitest'
import {
  LANE_ORDER,
  PROVENANCE_ORDER,
  PROVENANCE_LABEL,
  type AggregateKind,
  type EventModelElementKind,
  type EventModelLane,
  type EventModelProvenance,
  type EventModelRole,
  type HotspotOrigin,
  type SlicePattern,
  type TriggerKind
} from '../types'

/**
 * The header of `types.ts` promises this file exists: these interfaces are a HAND-WRITTEN mirror of
 * a contract that lives in another repo's C#, so nothing but a test stops them drifting. A rename
 * upstream has to fail here rather than silently render a blank lane or an unstyled card.
 *
 * Every list below is transcribed from `JasperFx.Events.EventModeling` at JasperFx.Events 2.56.0.
 * Bumping the pin means re-reading the enums and updating BOTH files together.
 */

/** Exhaustive-by-construction: a missing member is a compile error, an extra one a test failure. */
function members<T extends string>(record: Record<T, true>): string[] {
  return Object.keys(record).sort()
}

describe('the descriptor contract, as of JasperFx.Events 2.56.0', () => {
  it('pins AggregateKind', () => {
    // Stream rows (#299) key on these; a rename upstream would silently put every event of a
    // renamed aggregate on the unlabelled row rather than fail.
    expect(
      members<AggregateKind>({
        WriteAggregate: true, ReadAggregate: true, ConsistentAggregate: true, BoundaryModel: true
      })
    ).toEqual(['BoundaryModel', 'ConsistentAggregate', 'ReadAggregate', 'WriteAggregate'])
  })

  it('pins EventModelElementKind', () => {
    expect(
      members<EventModelElementKind>({
        Trigger: true, Command: true, Handler: true, Aggregate: true, Event: true,
        Message: true, Projection: true, ReadModel: true, ExternalSystem: true, Hotspot: true
      })
    ).toEqual([
      'Aggregate', 'Command', 'Event', 'ExternalSystem', 'Handler',
      'Hotspot', 'Message', 'Projection', 'ReadModel', 'Trigger'
    ])
  })

  it('pins EventModelLane, and LANE_ORDER renders top to bottom', () => {
    expect(
      members<EventModelLane>({ Wireframe: true, Command: true, EventStream: true, ReadModel: true })
    ).toEqual(['Command', 'EventStream', 'ReadModel', 'Wireframe'])

    // Rendering order is part of the contract, so it is pinned as a SEQUENCE, not a set.
    expect(LANE_ORDER).toEqual(['Wireframe', 'Command', 'EventStream', 'ReadModel'])
  })

  it('pins SlicePattern and TriggerKind', () => {
    expect(
      members<SlicePattern>({ Command: true, View: true, Automation: true, Translation: true })
    ).toEqual(['Automation', 'Command', 'Translation', 'View'])

    expect(
      members<TriggerKind>({
        Http: true, Grpc: true, MessageHandler: true, JobScheduler: true, Human: true, External: true
      })
    ).toEqual(['External', 'Grpc', 'Http', 'Human', 'JobScheduler', 'MessageHandler'])
  })

  it('pins HotspotOrigin including the jasperfx#704 addition', () => {
    expect(
      members<HotspotOrigin>({ PendingSpecification: true, Prose: true, SourceDisagreement: true })
    ).toEqual(['PendingSpecification', 'Prose', 'SourceDisagreement'])
  })

  it('pins the provenance ladder, lowest authority first', () => {
    expect(
      members<EventModelProvenance>({ Declared: true, Derived: true, Observed: true })
    ).toEqual(['Declared', 'Derived', 'Observed'])

    // The ORDER is the contract — higher rung wins, so a reordering here would invert precedence
    // for any consumer that ranks by index.
    expect(PROVENANCE_ORDER).toEqual(['Declared', 'Derived', 'Observed'])
    expect(Object.keys(PROVENANCE_LABEL).sort()).toEqual(['Declared', 'Derived', 'Observed'])
  })

  it('pins EventModelRole — a SourceDisagreement hotspot names one of these', () => {
    expect(
      members<EventModelRole>({
        TriggerLabel: true, TriggerType: true, TriggerKind: true, TriggerOrigin: true,
        Pattern: true, CommandType: true, HandlerType: true, AggregateTypes: true,
        EmittedEvents: true, PublishedMessages: true, ProjectionTypes: true, ReadModelTypes: true,
        ExternalSystems: true, Hotspots: true, Specifications: true, Domain: true
      })
    ).toEqual([
      'AggregateTypes', 'CommandType', 'Domain', 'EmittedEvents', 'ExternalSystems', 'HandlerType',
      'Hotspots', 'Pattern', 'ProjectionTypes', 'PublishedMessages', 'ReadModelTypes',
      'Specifications', 'TriggerKind', 'TriggerLabel', 'TriggerOrigin', 'TriggerType'
    ])
  })
})
