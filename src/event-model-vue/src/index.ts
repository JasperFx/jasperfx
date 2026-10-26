export { default as EventModelView } from './EventModelView.vue'
export {
  layoutEventModel,
  streamRowPlan,
  canvasSize,
  CHAPTER_BAND_HEIGHT,
  COLLAPSED_WIDTH,
  CANVAS_PADDING,
  GUTTER_GAP,
  GUTTER_WIDTH,
  CARD_PADDING_X,
  LABEL_FONT_SIZE,
  LABEL_LINE_HEIGHT,
  LABEL_TARGET_LINES,
  MAX_LABEL_LINES
} from './layout'
export {
  chaptersOf,
  domainsOf,
  hiddenSliceNames,
  hotspotsOf,
  isEmptyFilter,
  matchesFilter,
  patternsOf
} from './filters'
export type { SliceFilter } from './filters'
// jasperfx#1084 — a source disagreement as a badge on the disputed card. Exported so a host's legend
// and selection detail key what the canvas actually draws, not the raw hotspot list.
export {
  disagreementElementIds,
  disagreementLines,
  disagreementsOf,
  isDisagreementElement,
  placeDisagreements
} from './disagreements'
export type { DisagreementPlacement, DisagreementPresentation } from './disagreements'
// issue #296 — navigation. The decisions are pure functions so a host can round-trip a viewport
// through a URL without re-deriving what a focus or a level of detail means.
export {
  breadcrumbFor,
  fitToRect,
  focusedSliceNames,
  lodFor,
  minimapScale,
  minimapWindow,
  neighbourhoodOf,
  rectForSlices,
  scrollForMinimapPoint,
  selectionFromKey,
  selectionToKey,
  sliceOfSelection,
  slicesInChapter,
  slicesInDomain,
  viewportFromQuery,
  viewportToQuery,
  worthMapping,
  LOD_COMPACT_BELOW,
  LOD_OVERVIEW_BELOW,
  MINIMAP_MIN_CANVAS_WIDTH,
  MINIMAP_SCALE
} from './focus'
export type {
  Crumb,
  FocusTarget,
  LevelOfDetail,
  MinimapScale,
  Rect,
  Selection,
  ViewportState
} from './focus'
export { estimateTextWidth, requiredContentWidth, segmentLabel } from './text'
export { TRIGGER_ICON, TRIGGER_KIND_LABEL, parseRoute } from './icons'
export type {
  EventModelGraph,
  LaidOutChapterBand,
  LaidOutEdge,
  LaidOutLane,
  LaidOutLaneRow,
  LaidOutNode,
  LaidOutSlice,
  LayoutOptions,
  StreamRowPlan
} from './layout'
export { EVENT_MODEL_PALETTE, colorFor, inkFor, DASHED_KINDS, OUTLINED_KINDS } from './palette'
export { LANE_ORDER, LANE_LABEL, PROVENANCE_ORDER, PROVENANCE_LABEL } from './types'
export type {
  AggregateDescriptor,
  AggregateKind,
  EventModelClaim,
  EventModelDescriptor,
  EventModelEdge,
  EventModelElement,
  EventModelElementKind,
  EventModelLane,
  EventModelLink,
  EventModelLinkKind,
  EventModelProvenance,
  EventModelRole,
  EventModelSliceDescriptor,
  DomainAssignmentDescriptor,
  DomainAssignmentScope,
  ExternalSystemDescriptor,
  ExternalSystemDirection,
  HotspotDescriptor,
  HotspotOrigin,
  PublisherOrigin,
  SlicePattern,
  SpecificationDescriptor,
  TriggerKind,
  TypeDescriptor
} from './types'
