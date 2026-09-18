# @jasperfx/event-model-vue

Vue renderer for a `JasperFx.Events.EventModeling.EventModelDescriptor`. Issue #108.

One descriptor, one picture, in every viewer that draws one. **This package lives in the
`jasperfx` repository, beside the descriptor type it renders** — deliberately not inside either
console that consumes it.

That neutrality is the point. The renderer is consumed by Stoat's design-time canvas and by
CritterWatch's runtime canvas, and a shared component owned by one of its consumers acquires that
consumer's release cadence and licence question. Here it has neither: the package is MIT, it
versions with the descriptor whose shape it draws, and both consoles take it from npm by version.

> **Moved from `bobcat/src/Bobcat.EventModel.FrontEnd` (2026-09-17).** npm trusted publishing is
> bound to an org + repo + workflow filename, so the binding did not survive the move. The first
> publish from this home needs a one-time `NPM_TOKEN` bootstrap — see
> `.github/workflows/publish-event-model-vue.yml`.

## Why it renders a descriptor and not a lifecycle

CritterWatch's original `EventModelingView.vue` took its runtime `Lifecycle` model and transformed
it inside the component. That made it unshareable — the Bobcat console has no `Lifecycle`, and
CritterWatch has no Bobcat generator.

JasperFx.Events 2.54.0 (jasperfx#687) settled this upstream. `EventModelSliceDescriptor` carries
`Elements` and `Edges` as, in the contract's own words, "the rendering contract every viewer can
draw from without a second transform". So each producer adapts to the descriptor once —
`lifecycleToEventModel.ts` stays on the CritterWatch side, the Bobcat generator emits descriptors
directly (#106) — and this component stays common. The two surfaces now agree on a **published
type** rather than on a copied file, which is what actually guarantees identical rendering.

## Usage

```bash
npm install @jasperfx/event-model-vue
```

Published with provenance from `.github/workflows/publish-event-model-vue.yml` (npm Trusted
Publishing). Every consumer takes it from the registry by version — there is no `file:` link any
more, because no consumer lives in this repository.

```ts
import { EventModelView } from '@jasperfx/event-model-vue'
import '@jasperfx/event-model-vue/style.css'
```

```vue
<EventModelView
  :descriptor="model"
  :slice-outcomes="outcomes"
  @element-click="drillDown"
/>
```

`slice-outcomes` maps a specification identity (`{Feature}/{Scenario}`) to `passed` / `failed` /
`notRun`, which is the run evidence issue #107 puts on the wire. A slice no evidence names stays
unmarked rather than defaulting to green.

`vue` is the one peer dependency — a consumer supplies its own, because bundling a
second copy of Vue gives you two reactivity systems that cannot see each other.

## Provenance, and disagreement between sources (0.4.0)

JasperFx.Events 2.56.0 (jasperfx#703 / #704) gave the descriptor a three-rung ladder —
`Declared < Derived < Observed` — and made a dropped claim a finding instead of a silent loss.
Four producers now feed one descriptor: Gherkin specs, the C# overlay, Wolverine's chains, and
CritterWatch's runtime observation. This package renders both halves.

**The ladder rides a second visual channel, never the fill.** Fill colour means the element KIND,
and two viewers agreeing on what a colour means is the whole reason this package exists. So
`EventModelElement.provenance` becomes a `data-provenance` attribute on the card plus a corner
marker on `Observed` only. `Declared` and `Derived` are deliberately left looking exactly as they
did: an unattributed model reads `Declared` rather than absent, so fading it would fade most of a
typical canvas — and the new information is "production has *seen* this", not "this was only
written down".

**A source disagreement is a finding, not another sticky.** `HotspotOrigin.SourceDisagreement` gets
`data-hotspot-origin` and a double outline, and its `winningClaim` / `losingClaim` go on the
tooltip. *The code says this slice emits `FundsWithdrawn`; production says `FundsWithdrawn` **and**
`AuditRecorded`* is arguably the most valuable thing a four-source model produces, and it deserves
better than the generic magenta every other hotspot gets.

⚠️ The producer projects each hotspot into an element with `ForLabel(name, Hotspot, hotspot.Text)`,
so the label **is** the hotspot text — that is the only join a viewer has back to the origin, and
`hotspotFor` relies on it.

Both fields are optional. A descriptor from a producer still on JasperFx.Events < 2.56 renders
exactly as it did on 0.3.0, with no attribute invented for a rung nobody claimed. Bobcat's own C#
still pins 2.54.0, so its generated descriptors are in that category until that pin moves.

⚠️ **Not the same axis as CritterWatch's `LifecycleProvenance`**, despite `Observed` appearing in
both. That one reconciles a single edge across static and runtime discovery, where `Confirmed`
means "both sources agree" — not "seen in production". This one is a ladder of authority.
`Declared` and `Derived` both map onto its `Inferred`, and its `Confirmed` has no rung here:
agreement is expressed by the *absence* of a `SourceDisagreement` hotspot.

## Edges are drawn, and routed here (0.6.0, bobcat#181)

`EventModelSliceDescriptor.Edges` is computed upstream from the typed roles on every read —
precisely so no renderer invents its own opinion about what connects to what — and this component
used to lay them out and then draw nothing. It draws them now, as one pointer-inert SVG layer
behind the cards.

**The route is computed in `layout.ts`, not in the component.** A polyline is as much a rendering
claim as a coordinate, so `LaidOutEdge.points` joins position in the pure layer: same descriptor,
same picture, checkable by a test. Two shapes, because the canvas has two kinds of relationship:

- **Along a lane** (command → handler → aggregate) the flow is left to right, so the edge is a
  straight line between the two facing card edges.
- **Across lanes** (command → event, event → projection) it is an orthogonal elbow turning at the
  middle of the lane gap. A diagonal would cross the band divider at an arbitrary angle and read as
  a different kind of statement; the elbow reads as "down into the next lane". A card sitting
  directly above its partner gets one straight drop instead of an elbow with two zero-length legs.

Both are direction-aware — an edge pointing back up or left leaves the *other* face of its source,
because declaration order is the producer's and nothing promises it matches flow order.

An edge whose endpoints were not both *drawn* is dropped, which is a slightly stronger test than
the "not both declared" one it replaces: an element in a lane this package does not know is
dropped from the canvas, and an arrow into empty space would read as a modelling claim rather than
as the producer bug it is.

## Reading a big model: zoom, pan, and what each slice is (0.7.0)

The 2026-08-31 review of a real 106-slice canvas produced four notes, all answered here so both
consoles inherit one answer.

**Zoom and pan (bobcat#182).** Zoom is a CSS transform on a wrapper, deliberately *not* a scale
factor threaded into `layoutEventModel`: layout is a pure function of the descriptor, and letting
the viewport change it would put the two viewers' agreement at the mercy of how wide someone's
window happens to be. The wrapper carries its own scaled `width`/`height`, because a transform does
not change layout size and the scroller would otherwise still think the canvas was its 100% self.
Stops (25%–200%) rather than a continuous ramp, so a reader can return to a zoom they had; the
level doubles as the reset. **Fit** is the one zoom that is not a step — "all of it on screen" is a
measurement, not a preference — and it never zooms *in* to fill, because a small model blown up to
200% looks like a mistake. It is clamped at 25% like every other zoom, so a genuinely enormous
model fits as far as legibility allows and no further. Drag-to-pan on the background; a drag that
starts on a button is not a pan.

**Bound-specification count (bobcat#183).** A badge on every slice header — `3 specs`, or `no spec`
spelled out, because zero is the drift case the canvas already colours orange and it should read as
a finding. Where the host passes `sliceOutcomes` the badge carries the verdict too, so a failing
slice says so at the same glance it says it has three specs.

**Trigger kind and routes (bobcat#184).** A 12px glyph per `triggerKind` in the slice header —
globe, envelope, clock, person, paired arrows, box-with-arrow — with `triggerOrigin` on its tooltip,
which is where the route lives now that wolverine#4181 stopped the HTTP source claiming
`TriggerLabel`. Inline path data rather than an icon dependency: this component ships to two
consoles with different icon sets, and a shared third is the one thing neither host wants. A trigger
card whose label *is* a route renders its verb as an outlined badge and the path beside it — the
verb is fixed vocabulary a reader recognises by shape, and outlined rather than filled because fill
means the element kind.

**A source disagreement reads as a finding (bobcat#178).** The producer makes the hotspot's text the
element label, so the card used to lead with the role name and a clipped sentence — and the reviewer
who designed the feature read it as a malformed events list and asked what it was for. That reading
was the finding. A hotspot card now renders structure: what kind of finding it is, then (for a
disagreement) the role and both claims with their rungs, kept above dropped and the dropped one
struck through. All from the typed `role`/`winningClaim`/`losingClaim`, so nothing is parsed back
out of the sentence, and a disagreement whose pair did not survive the wire degrades to its text
rather than to half a finding. Promoting findings out of the lanes into a strip above the canvas —
the other candidate on the issue — stays unbuilt: position is what says which slice a finding
belongs to, and a strip would have to repeat that in words.

## Three things the first pass left on the table (0.8.0)

Shaking the 0.7.0 canvas out against a real 106-slice model turned up three gaps, each in one of
the features it had just shipped:

- **Zoom anchored at the top-left.** `transform-origin` is `0 0`, so changing the zoom moved the
  canvas out from under the reader — on a 41,000px model, zooming in while reading slice 80 threw
  them back to slice 1. The scroll offset now moves with the scale, so "closer" means closer to
  what you were looking at. It clamps at zero rather than scrolling negative at the left edge.
- **The spec badge was a label.** It names the specifications bound to a slice, so a reader clicks
  it expecting to see them, and nothing happened. It is a button now, opening the same drawer the
  slice name opens.
- **A route-named slice spent its header width on the verb.** Wolverine names a query slice for its
  verb and route (`GET /api/clients/{id}/accounts`), so the header's scarcest resource went on a
  word the trigger glyph beside it already says. The verb is badged the way it is on a route card —
  three characters instead of eight, and the path starts where the eye expects it. The name itself
  is untouched; this is only how it is drawn.

## Chapters: the band above the slices (0.12.0, bobcat#298)

Every Event Modeling tool surveyed uses **chapters** as the answer to "zoom into a part":
eventmodelers.ai draws "a wide blue arrow spanning several slices … useful once a model grows past
a single screen's width and needs a table of contents"; Miro uses frames; emlang documents are per
chapter. The descriptor now carries `chapter` on a slice (jasperfx#824) — Bobcat's emlang import
keeps the board's chapter, a curated file declares `chapter:`, a `.feature` tags `@chapter:` — and
the canvas draws it.

**Bands.** `layoutEventModel` emits `graph.chapters`: one `LaidOutChapterBand` per **contiguous
run** of drawn slices sharing a chapter, spanning exactly their columns, in a strip
`CHAPTER_BAND_HEIGHT` (28px) tall above the first lane. The strip exists only when a drawn slice
has a chapter — `graph.chapterBandHeight` is 0 otherwise and an unchaptered model is
coordinate-identical to 0.11.0. A band is the wide arrow: `currentColor` at low alpha, clipped to an
arrowhead on the right, the chapter's name once, and at `overview` the name grows to 24px because
the bands are what a reader navigates a 106-slice model by when the cards are colour blocks.

**The canvas never reorders.** Declaration order is the producer's statement about sequence, so a
chapter whose slices are interleaved with another's draws one band per run, each with the same
name, rather than being pulled together. Slices with no chapter sit under no band. Hidden slices
are not drawn and so are in no band: filter a chapter's middle slice away and its neighbours become
one run. Nothing orders chapters relative to each other — upstream carries the name and nothing
else — so the order on the canvas is the order the producer declared its slices in.

**Focus hierarchy.** A band is a button: click it and the whole chapter — every run of it — fits the
viewport and the rest dims, the same way a slice's ⌖ does. The ladder is now model → **chapter** →
slice → bound spec. The breadcrumb's middle rung is the slice's chapter when it has one and its
domain otherwise, never both: the two are orthogonal (a bounded context has many chapters), and a
trail showing both would be two axes pretending to be one hierarchy. `focus=chapter:<name>` is a
legal URL focus, `slicesInChapter` is exported beside `slicesInDomain`, and `FocusTarget.kind`
gains `'chapter'`.

**Filter.** The filter bar gains one chip per chapter, in the model's order rather than
alphabetical (a chapter is a sequence, not a set), beside the domain chips. `SliceFilter.chapters`
narrows like `domains`, and a chapterless slice is excluded by a chapter filter for the same
reason an undomained one is by a domain filter. `chaptersOf(descriptor)` lists them.

## Cause and effect, drawn (0.11.0, bobcat#295)

`EventModelDescriptor.links` has been computed upstream since JasperFx.Events 2.69 — one entry per
cross-slice cause→effect relationship, joined from the roles every slice already stamps. The canvas
laid them out and drew nothing. Now it draws them, and the *how* is most of the design.

**Corridor routing.** The grid leaves an empty horizontal band between card rows. A link drops out
of its source into the band, runs along a **track** inside it, and rises or drops into its target —
so it never crosses a card on the way. Tracks are allocated per band, **first-fit over x-intervals,
left to right**: two runs that overlap get different tracks and cannot overprint, two that cannot
collide share one, so a wide model does not accumulate a track per link.

**Bundled by source.** Every link leaving one element shares one trunk and one track. Four
consumers of an event are four branches off one line, not four lines — the trunk *is* the stream
leaving the fact, which is how a reader already thinks about it, and it is the single biggest
clutter reduction in the feature.

⚠️ **The honest limit.** A link between lanes that are not adjacent runs its corridor in the band
beside the *source*, so its far vertical leg passes the rows in between at the target's x. The
corridor never crosses a card; that one leg may. Routing through every intervening band is a much
bigger router for a case that is rare on a real board.

**The chevron is the other half, and at 106 slices it is the important one.** Event Modeling's own
convention is to repeat a sticky where it is consumed rather than draw a connector back — and the
descriptor already repeats. So an element that is the far end of a link carries `◂ OpenAccount` in
its corner, naming where its input came from. Clicking it selects that origin and scrolls to it. A
slice name stays readable at a zoom where a 3,000px arrow does not.

**Faint at rest, lit by selection.** Links draw thinner and fainter than intra-slice edges — an
edge is a statement about one slice's internals, these cross the whole board, and at fleet size
they would otherwise become the picture. Selecting a card or slice lights its links; the toolbar's
**⇢ all / selected / none** cycles how much of the layer is drawn at all, and `none` is the canvas
exactly as it was before this shipped.

**One glyph per kind, no labels:** `EventTriggers` and `MessageTriggers` solid, `EventConsumed`
dotted, `ReadModelRead` dashed. The far end's own label already says what travelled.

`layoutEventModel` gains a fourth output beside `edges`: `links: LaidOutLink[]`, each with its
`points`, its `track` and the `trackY` it runs along — pure and synchronous like everything else
here, and pinned to exact coordinates in `layout.spec.ts`, because where a link runs is as much a
rendering claim as where a card sits.

⚠️ **Not in this release:** hover-driven highlighting (selection-driven is in) and the off-screen
`⇢ N` partner badge. Both are about a reader's pointer and viewport rather than about the
descriptor, and both are additive.

## A stream is a row (0.10.0, bobcat#299)

Two slices that write `Account` put their events on the same horizontal line inside the Event
Stream lane, and that they are on one stream is then visible **with no arrow at all**. That is
decision 2 of the canvas design and the reason this is a layout change rather than a new link kind:
a shared aggregate is not a cause→effect relationship, and fanning every event of an aggregate out
to every slice that touches it draws noise where the canvas should be making a statement.

The lane becomes one row per aggregate, in the model's `aggregates` order (first appearance),
captioned in the gutter under the lane's own caption. `layoutEventModel` does it; `streamRowPlan`
is the same decision exported on its own, because *which slices share a stream* is a question about
the model rather than about the picture, and a host drawing its own legend should ask the function
the layout asks.

**Three rules, and the first is why no canvas you have already drawn moved.**

1. **Fewer than two aggregates in view ⇒ one flat row.** A row is a comparison; with one stream
   there is nothing to compare and the split would only cost height. Rows are computed over the
   slices actually *drawn*, not over the descriptor, so filtering a 106-slice model down to one
   aggregate collapses the lane back rather than leaving a stack of empty rows behind.
2. **An event sits on the aggregate whose `appliedEvents` names it**, and when a slice names
   several aggregates but no applied-event list answers, on the slice's first aggregate. The
   fallback is not a formality: a producer that cannot resolve an apply set statically emits an
   empty list, so no layout decision may *require* one.
3. **A published message is on no stream, and neither is an event whose slice writes no
   aggregate.** They share the trailing unlabelled row. The design left the messages row
   "above/below the stream rows"; one row that means *in this lane, on no stream* says more than
   two rows both captioned by their absence, and it is one row of height rather than two.

**What it costs.** 0.04ms: a 106-slice model across four streams lays out in 0.60ms against 0.56ms
flat, both sub-millisecond, both one synchronous pass, measured over 50 runs. Set
`LayoutOptions.streamRows: false` to keep the flat lane exactly as it was.

**Rendering.** `LaidOutLane` gains `rows` — always at least one, so a viewer never branches on
whether a lane was split — each with its absolute `y`, its `height`, the aggregate's type identity
as `key`, and the gutter caption as `label` (`null` on the unlabelled row, which says what it is on
hover instead). Alternate rows carry a 3.5% tint, which is deliberately the thing that still
separates them at `overview`: the captions are hidden there, and they cannot be counter-scaled the
way a column name is — the gutter is 132px wide, 33px at the 25% floor, and a caption drawn big
enough to read there would spill across the plot.

⚠️ **This release corrects a wire mirror that was wrong, not merely incomplete.** `aggregates` was
typed here as `EventModelElement[]` — which is what a slice's Aggregate *cards* are, not what the
model document carries. The real shape is `AggregateDescriptor` (`type`, `kind`, `appliedEvents`),
and `EventModelSliceDescriptor` grows the `aggregateTypes` it always had on the wire. Nothing in
this package had ever read either member, so the error was invisible until a row needed
`appliedEvents` to decide where an event goes. A consumer that read `descriptor.aggregates` as
elements was reading a member no producer fills that way.

A descriptor from a producer below JasperFx.Events 2.60 carries no `aggregateTypes`; the plan falls
back to the slice's `Aggregate` cards, which is the same claim projected into the rendering
contract.

## Navigating a big model: focus, level of detail, minimap, place (0.9.0, bobcat#296)

0.7.0 gave the canvas nine zoom stops and 0.8.0 gave it a filter bar, and a 106-slice model is
*still* ~10,000px wide at the 25% floor. What was missing was never magnification. It was being
able to say **look at this part**, see less when far out, know where you are, and send someone the
view. All four ride the same `transform: scale()` wrapper #182 built, and none of them reaches into
`layoutEventModel` — the decisions live in `focus.ts` as pure functions over the laid-out graph.

**Focus.** Pick a slice (the ⌖ in its header, or select a card and press **Focus**) and the canvas
fits that slice's **neighbourhood** — the slice plus every slice one `link` away — and dims the
rest. The breadcrumb reads `Fleet › Reporting › Slice079`; each crumb is a step out, and Esc does
the whole journey at once, back to the zoom and scroll the reader had before they focused.

⚠️ **The neighbourhood degrades to the slice alone when the descriptor carries no `links`**, which
is every descriptor a producer below JasperFx.Events 2.69 can emit — including the version this
repo pins today. Nothing here derives a link client-side: two viewers inventing their own joins is
exactly what the upstream computation (jasperfx#823) exists to prevent. Drawing the links is #295.

Two ways in rather than one, and the second is not a convenience: the slice name opens the host's
drill-down, and in the Bobcat console that is a **modal drawer whose overlay then covers the
toolbar** — "select, then press Focus" is unreachable the moment the host reacts to the selection.
Found by driving the real 106-slice canvas, not by reading the code.

**Level of detail.** A `data-lod` attribute on the viewport, set from the scale, that CSS switches
on. No re-layout, no `v-if`, and identical in both consoles by construction — a canvas of 700 cards
must not re-render because someone nudged the wheel, and two hosts must not each decide for
themselves what "less" means.

| level | scale | what it draws |
|-------|-------|---------------|
| `detail` | ≥ 0.7 | today's rendering, defined by having no rules at all |
| `compact` | 0.4–0.7 | kind colour, one-line label, spec badge; glyphs, verb badges and hotspot text drop |
| `overview` | < 0.4 | cards are colour blocks with no text; each column paints its own name and pattern, counter-scaled to survive the transform; lane captions stay |

The overview label sits *over* the top of its column rather than above it: there is only
`CANVAS_PADDING` of room above the plot — three pixels at the 25% floor — and a label hoisted into
it is simply clipped, which is what the first cut of this did.

**Minimap.** The same `EventModelGraph` again as bare `<rect>`s, below the canvas and right-aligned.
Rects only — no text, no edges — so it costs nothing to repaint on every scroll frame, and it
tracks the filter bar for free because it is handed the graph the canvas drew rather than the
descriptor.

Two things it does that the issue did not ask for, both because the real model demanded them:

- **x and y scale independently.** A uniform "~1/40" is right for ordinary proportions and wrong for
  this canvas: 106 slices is ~53,000 × 550px, near 100:1, and scaled faithfully into a corner it
  measured **222 × 3.6px** — not a map of anything. x keeps the 1/40 ceiling; y fills its box. The
  cost is card *shape*; what survives is how far along the model you are and which lanes carry
  anything, which is all a minimap is ever asked.
- **It is not drawn at all below ~2,500px of canvas** (about six slices). A canvas that fits on two
  screens has nothing for a map to answer, and a map of a two-slice model is a 30px smudge.

It is also **in flow rather than floated over a corner**: the viewport has no height of its own —
it grows to whatever the scaled canvas needs — so at the 25% floor a 106-slice model is ~140px tall
and a 72px overlay covers half the plot.

**Place.** Continuous, cursor-anchored wheel zoom (the nine stops stay as the button ladder, which
is what they were good at; a pinch is an analogue gesture and snapping it feels broken). The
component emits `viewport-change` with `{ zoom, x, y, focus, selection }` and accepts an
`initialViewport` back; `viewportToQuery` / `viewportFromQuery` are exported so a Bobcat link and a
CritterWatch link to "CreditWallet, focused" mean the same thing. **Where** that state is kept is
the host's call — the Bobcat console mirrors it into the route query with `replace`, so a URL to a
part of a 106-slice model can be pasted into a PR and Back does not walk every notch of a zoom.

A restored *focus* is re-fitted rather than restored from its offsets: a link opened on a narrower
window should still frame the neighbourhood, not land on coordinates measured somewhere else.

One non-obvious rule in the fit. `scale = clamp(min(vw/w, vh/h))` is the issue's formula, but the
scroller's height is *about the canvas height* — it grows to its content — so `vh/h` reduces to the
zoom the reader already had, and on the real model focus "fitted" 46% to 48%. `fitToRect` therefore
takes `viewport.height <= 0` to mean "the height is not constraining" and fits on width alone, and
the component decides which case it is by asking whether the scroller actually scrolls vertically.

## Card sizing: wrap, widen, then clamp (0.5.0, bobcat#180)

Cards were absolutely sized at 180px with `overflow: hidden`, so a long command name — and worse,
an HTTP trigger label like `POST /accounts/{id}/deposit` — was simply cut off mid-glyph. The
strategy is decided here once so both consoles inherit it, in this order:

1. **Wrap at the points a reader would break the name themselves.** CSS gives a browser break
   opportunities at spaces and hyphens and nowhere else, and `DepositMoneyIntoAccount` has neither
   — it is one unbreakable word. `segmentLabel` splits at camel humps and after `/ \ . _ - : , +`,
   and the card renders the segments with `<wbr>` between them.
2. **Widen the column only when wrapping is not enough.** `cardWidth` (180) became the *floor*, and
   a column grows to fit its own widest label in `LABEL_TARGET_LINES` (2) lines. Per column, not
   per card: cards in a column line up under each other, and ragged widths inside one lane read as
   a broken grid rather than as "this name is longer".
3. **Clamp past the cap.** `maxCardWidth` (320) stops one pathological route owning the canvas;
   beyond it the label clamps to `MAX_LABEL_LINES` (3) with an ellipsis. The full text was already
   on the card's tooltip, which is what makes truncation acceptable rather than lossy.

Set `maxCardWidth` equal to `cardWidth` to get the old fixed grid back.

**Widths are estimated, never measured** — `estimateTextWidth` is a small per-character model, not
`canvas.measureText` and not a DOM pass, because layout must stay a pure function of the descriptor
(below). The estimate only ever chooses a column width, and steps 1 and 3 absorb whatever it gets
wrong: an estimate 10% out costs a slightly roomy or slightly tight column, never a clipped name.
That is also why the type scale (`LABEL_FONT_SIZE`, `LABEL_LINE_HEIGHT`, `CARD_PADDING_X`) lives in
`layout.ts` and is written onto the card as inline style — a width computed from one font size and
rendered at another is a clipped label with no traceable symptom.

**Considered and rejected:** shrink-to-fit type scale (a canvas of six different text sizes reads
as noise, and the small end is unreadable at the zoom levels these are viewed at), and
truncate-with-title alone (it hides exactly the distinction — `OrderPlaced` vs `OrderPlacedV2` —
that the reader came to the canvas to see).

## Layout is pure, and that is the point

`layoutEventModel(descriptor, options)` is synchronous and free of any graph library — no elk, no
worker, no measurement pass. The acceptance criterion for this package is that the same descriptor
renders identically in both viewers, and "identically" is only checkable if position is a function
of the descriptor alone. `layout.spec.ts` therefore asserts exact coordinates.

Slices are vertical columns in declaration order; lanes are horizontal bands in the canonical
top-to-bottom order (`Wireframe`, `Command`, `EventStream`, `ReadModel`); an element sits where its
slice column meets its lane band, and several elements in one cell run left to right.

Three behaviours worth knowing, each pinned by a test:

- **Declaration order is preserved, never sorted.** It is the producer's statement about sequence.
- **A dangling edge is dropped.** Drawing a line to nowhere would read as a modelling claim rather
  than the producer bug it is.
- **An unknown lane is dropped, not stacked at y=0**, where it would overlap the wireframe lane and
  look like a rendering bug rather than "this descriptor came from a newer JasperFx".

## Development

```bash
npm install
npm test          # vitest run
npm run typecheck # vue-tsc -b
npm run build     # vite lib build + rolled-up .d.ts
```

CI gate: `.github/workflows/event-model-frontend.yml`.
