/**
 * Label text metrics and break opportunities — the sizing half of issue bobcat#180.
 *
 * The canvas cards were absolutely sized at 180px with `overflow: hidden`, so a long command name
 * (and worse, an HTTP trigger label like `POST /accounts/{id}/deposit`) was simply cut off. The
 * fix has to hold the package's central invariant: layout is *pure*, so there is no measurement
 * pass and no `canvas.measureText` — position and size must be a function of the descriptor alone,
 * or "the same descriptor renders identically in both viewers" stops being checkable.
 *
 * So the width of a label is *estimated* from a small per-character model rather than measured.
 * The estimate only ever chooses a column width; the rendering absorbs whatever it gets wrong —
 * text wraps at the break opportunities `segmentLabel` finds, the card clamps to
 * `MAX_LABEL_LINES`, and the full name is on the card's tooltip either way. An estimate that is
 * 10% out therefore costs a slightly roomy or slightly tight column, never a clipped name.
 */

/**
 * Relative advance widths, in em, for a 13px humanist sans (the console's Helvetica Neue stack).
 * Three tiers is enough for the job: the error that matters is confusing `WITHDRAWAL` with
 * `illillill`, and those differ by more than a factor of two.
 */
const EXTRA_WIDE = new Set('MWmw@%'.split(''))
const NARROW = new Set('iltfrI.,:;\'"`!|()[]{}- '.split(''))
const EXTRA_WIDE_EM = 0.88
const WIDE_EM = 0.72
const NARROW_EM = 0.31
const DEFAULT_EM = 0.55

/** Characters a line may break after. A camel-case hump is handled separately, below. */
const DELIMITERS = new Set(['/', '\\', '.', '_', '-', ':', ',', '+', ' '])

/** Estimated rendered width of `text` in px at `fontSize`. */
export function estimateTextWidth(text: string, fontSize: number): number {
  let em = 0
  for (const ch of text) {
    if (EXTRA_WIDE.has(ch)) em += EXTRA_WIDE_EM
    else if (NARROW.has(ch)) em += NARROW_EM
    else if (ch >= 'A' && ch <= 'Z') em += WIDE_EM
    else em += DEFAULT_EM
  }
  return em * fontSize
}

/**
 * Split a label into the pieces a line may break between.
 *
 * CSS gives a browser break opportunities at spaces and hyphens and nowhere else, which is exactly
 * no help for the labels on this canvas: `DepositMoneyIntoAccount` and `Bank.Accounts.Withdraw`
 * are each one unbreakable word to the layout engine. The component renders these segments with
 * `<wbr>` between them, so wrapping happens at a camel hump or after a delimiter — where a reader
 * would break the name themselves — instead of mid-word or not at all.
 *
 * A delimiter stays with the segment it ends, so a route reads `/accounts/` `{id}/` `deposit`
 * rather than orphaning the slash onto the next line.
 */
export function segmentLabel(label: string): string[] {
  const segments: string[] = []
  let current = ''

  for (let i = 0; i < label.length; i++) {
    const ch = label[i]
    const prev = i > 0 ? label[i - 1] : ''
    const isHump = /[a-z0-9]/.test(prev) && /[A-Z]/.test(ch)
    if (isHump && current) {
      segments.push(current)
      current = ''
    }

    current += ch

    if (DELIMITERS.has(ch)) {
      segments.push(current)
      current = ''
    }
  }

  if (current) segments.push(current)
  return segments.length > 0 ? segments : [label]
}

/**
 * The content width a label wants, in px: wide enough for its longest unbreakable segment, and
 * wide enough that the whole label fits in `targetLines` lines.
 *
 * `targetLines` is the width the column is *sized* to aim at, not the ceiling the card renders to.
 * Sizing at two lines and clamping the render at three is deliberate: a name that needs a second
 * line is ordinary and should widen the column a little, while a name that still needs a third
 * has hit the caller's width cap and is being helped by the clamp rather than by more width.
 */
export function requiredContentWidth(label: string, fontSize: number, targetLines: number): number {
  const full = estimateTextWidth(label, fontSize)
  if (targetLines <= 1) return full

  const longest = Math.max(
    ...segmentLabel(label).map((segment) => estimateTextWidth(segment.trimEnd(), fontSize))
  )
  return Math.max(longest, full / targetLines)
}
