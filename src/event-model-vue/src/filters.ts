import type { EventModelDescriptor, EventModelSliceDescriptor } from './types'

/**
 * Which slices a reader wants to see. Every field is a narrowing; an empty or omitted field
 * narrows nothing, so the default criteria show the whole model.
 *
 * Pure data, resolved to a set of names by {@link hiddenSliceNames} and handed to
 * `layoutEventModel` as an option — never applied inside the layout itself. That is what keeps
 * the layout a pure function of `(descriptor, options)`, which is what makes "the same descriptor
 * renders identically in both viewers" a checkable claim rather than a hope (issue #194).
 */
export interface SliceFilter {
  /**
   * Domains to keep. Empty means every domain, including slices that declare none.
   *
   * The natural axis: the descriptor carries `domain` already, and a merged fleet model has real
   * ones. It is also the only axis that partitions a model rather than merely thinning it.
   */
  domains?: ReadonlySet<string>

  /**
   * Chapters to keep (issue #298). Empty means every chapter, including slices that declare none.
   *
   * The other partitioning axis, and the one every Event Modeling tool zooms by: a chapter is a
   * span of the timeline where a domain is a partition of the system. A slice with no chapter is
   * excluded by a chapter filter for the same reason an undomained one is by a domain filter.
   */
  chapters?: ReadonlySet<string>

  /**
   * Narrow by whether a slice has a bound specification — the drift view.
   *
   * `unbound` is the one worth having: on the measured CritterWatch model 125 of 125 slices had
   * no spec, and finding which ones do is the whole question when the drift colour is honestly
   * orange everywhere.
   */
  specs?: 'any' | 'bound' | 'unbound'

  /** Keep only slices carrying at least one hotspot — the findings view. */
  hotspotsOnly?: boolean

  /**
   * Case-insensitive substring of the slice name. Last resort, and deliberately last: a reader
   * who knows the name does not need a canvas, but one who half-remembers it does.
   */
  search?: string
}

/** The domains a model declares, sorted, with slices declaring none reported as `null`. */
export function domainsOf(descriptor: EventModelDescriptor | null | undefined): string[] {
  const domains = new Set<string>()
  for (const slice of descriptor?.slices ?? []) {
    if (slice.domain) domains.add(slice.domain)
  }
  return [...domains].sort((a, b) => a.localeCompare(b))
}

/** The chapters a model declares, in first-appearance order — a chapter is a sequence, not a set. */
export function chaptersOf(descriptor: EventModelDescriptor | null | undefined): string[] {
  const chapters: string[] = []
  for (const slice of descriptor?.slices ?? []) {
    if (slice.chapter && !chapters.includes(slice.chapter)) chapters.push(slice.chapter)
  }
  return chapters
}

/** Does this slice survive the filter? */
export function matchesFilter(slice: EventModelSliceDescriptor, filter: SliceFilter): boolean {
  if (filter.domains && filter.domains.size > 0) {
    // A slice with no domain is not in any domain, so a domain filter excludes it. Treating an
    // absent domain as "matches everything" would make the filter grow the canvas.
    if (!slice.domain || !filter.domains.has(slice.domain)) return false
  }

  if (filter.chapters && filter.chapters.size > 0) {
    if (!slice.chapter || !filter.chapters.has(slice.chapter)) return false
  }

  if (filter.specs === 'bound' && (slice.specifications ?? []).length === 0) return false
  if (filter.specs === 'unbound' && (slice.specifications ?? []).length > 0) return false

  if (filter.hotspotsOnly && (slice.hotspots ?? []).length === 0) return false

  if (filter.search && filter.search.trim().length > 0) {
    if (!slice.name.toLowerCase().includes(filter.search.trim().toLowerCase())) return false
  }

  return true
}

/**
 * The slice names to hide, as `layoutEventModel`'s `hiddenSlices` option wants them.
 *
 * Hidden, not collapsed. A collapsed slice keeps a 48px placeholder so it stays findable, which
 * is right for one slice a reader put away — but the measurement behind issue #194 is 106 slices
 * on a ~10,000px canvas at the 25% zoom floor, and 106 placeholders is still 5,000px. A filter
 * that only collapses does not answer the problem it was asked to answer.
 */
export function hiddenSliceNames(
  descriptor: EventModelDescriptor | null | undefined,
  filter: SliceFilter
): Set<string> {
  const hidden = new Set<string>()
  for (const slice of descriptor?.slices ?? []) {
    if (!matchesFilter(slice, filter)) hidden.add(slice.name)
  }
  return hidden
}

/** True when the filter narrows nothing — used to keep the "showing N of M" line honest. */
export function isEmptyFilter(filter: SliceFilter): boolean {
  return (
    (!filter.domains || filter.domains.size === 0) &&
    (!filter.chapters || filter.chapters.size === 0) &&
    (filter.specs === undefined || filter.specs === 'any') &&
    !filter.hotspotsOnly &&
    (filter.search === undefined || filter.search.trim().length === 0)
  )
}
