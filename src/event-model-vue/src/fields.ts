import type {
  EventModelDescriptor,
  EventModelElement,
  EventModelSliceDescriptor,
  FieldDescriptor,
  HotspotDescriptor,
  ScheduleDescriptor,
  TypeDescriptor,
  TypeShapeDescriptor
} from './types'

/**
 * Field shapes (jasperfx#1299) and the trail a field leaves across a slice: select a field on a card and
 * see which other cards of the slice carry it — by the same name, or through a declared mapping — with
 * the fields nothing supplies marked. Pure, like every other decision this package makes about a model.
 */

function sameType(a: TypeDescriptor | null | undefined, b: TypeDescriptor | null | undefined): boolean {
  if (!a || !b) return false
  if (!a.assemblyName || !b.assemblyName) return a.name === b.name
  return (a.fullName ?? a.name) === (b.fullName ?? b.name)
}

function sameField(a: string, b: string): boolean {
  return a.localeCompare(b, undefined, { sensitivity: 'accent' }) === 0
}

/** The shape the model carries for a type, or null. */
export function shapeOf(
  descriptor: EventModelDescriptor | null | undefined,
  type: TypeDescriptor | null | undefined
): TypeShapeDescriptor | null {
  if (!type) return null
  return (descriptor?.shapes ?? []).find((s) => sameType(s.type, type)) ?? null
}

/**
 * The `IncompleteInformation` questions about a slice (jasperfx#1299), joined by `sliceName`. Computed
 * upstream on the model, never here: the canvas only decides where to draw them.
 */
export function questionsFor(
  descriptor: EventModelDescriptor | null | undefined,
  sliceName: string
): HotspotDescriptor[] {
  return (descriptor?.incompleteInformation ?? []).filter(
    (h) => h.origin === 'IncompleteInformation' && h.sliceName === sliceName
  )
}

/** The fields of `type` the model says come from nowhere, within `sliceName`, as `{Type}.{Field}` keys. */
export function unmatchedFields(
  descriptor: EventModelDescriptor | null | undefined,
  sliceName: string,
  type: TypeDescriptor
): Set<string> {
  const unmatched = new Set<string>()
  const prefix = `${type.name}.`
  for (const question of questionsFor(descriptor, sliceName)) {
    if (question.field && question.field.startsWith(prefix)) unmatched.add(question.field.slice(prefix.length))
  }
  return unmatched
}

export interface FieldTrail {
  /** The selected field, on the selected card's type. */
  field: FieldDescriptor
  /** Element ids in the slice whose type carries the field — by name, or as the field's mapping source. */
  elementIds: Set<string>
  /** True when the model says nothing supplies the field. */
  unmatched: boolean
}

/**
 * The trail of one field across a slice: every card whose type has a field of the same name, plus the
 * card the field's declared mapping points at (jasperfx#1299). The selected card itself is on the trail.
 */
export function fieldTrail(
  descriptor: EventModelDescriptor | null | undefined,
  slice: EventModelSliceDescriptor,
  selected: EventModelElement,
  fieldName: string
): FieldTrail | null {
  const shape = shapeOf(descriptor, selected.type)
  const field = (shape?.fields ?? []).find((f) => sameField(f.name, fieldName))
  if (!field) return null

  const elementIds = new Set<string>([selected.id])
  for (const element of slice.elements ?? []) {
    if (!element.type || element.id === selected.id) continue

    if (field.mapping && sameType(field.mapping.source, element.type)) {
      elementIds.add(element.id)
      continue
    }

    const other = shapeOf(descriptor, element.type)
    if ((other?.fields ?? []).some((f) => sameField(f.name, field.name))) elementIds.add(element.id)
  }

  return {
    field,
    elementIds,
    unmatched: selected.type ? unmatchedFields(descriptor, slice.name, selected.type).has(field.name) : false
  }
}

/** A field as a one-line signature: `Total: Decimal`, `Lines: OrderLine[]`, `Note: String?`. */
export function fieldLabel(field: FieldDescriptor): string {
  const many = field.cardinality === 'Many' ? '[]' : ''
  const optional = field.optional ? '?' : ''
  return `${field.name}: ${field.type?.name ?? '?'}${many}${optional}`
}

/**
 * A schedule's cadence as a short label for the automation's card (jasperfx#1298): `every 5m`,
 * `cron 0 9 * * MON`, or the schedule's name.
 */
export function scheduleLabel(schedule: ScheduleDescriptor | null | undefined): string | null {
  if (!schedule) return null
  switch (schedule.kind) {
    case 'Interval':
      return schedule.every ? `every ${timeSpanLabel(schedule.every)}` : 'on an interval'
    case 'Cron':
      return schedule.cron ? `cron ${schedule.cron}` : 'on a cron schedule'
    default:
      return schedule.name ?? 'scheduled'
  }
}

/**
 * A .NET TimeSpan as STJ writes it — `00:05:00`, `7.00:00:00`, `00:00:30.5000000` — read into the
 * shortest unit a person would say: `5m`, `7d`, `30.5s`, `1h 30m`.
 */
export function timeSpanLabel(value: string): string {
  const match = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value.trim())
  if (!match) return value

  const sign = match[1] ? '-' : ''
  const days = Number(match[2] ?? 0)
  const hours = Number(match[3])
  const minutes = Number(match[4])
  const seconds = Number(match[5]) + (match[6] ? Number(`0.${match[6]}`) : 0)

  const parts: string[] = []
  if (days) parts.push(`${days}d`)
  if (hours) parts.push(`${hours}h`)
  if (minutes) parts.push(`${minutes}m`)
  if (seconds) parts.push(`${Number(seconds.toFixed(3))}s`)
  return sign + (parts.length ? parts.join(' ') : '0s')
}
