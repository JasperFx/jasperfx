<script setup lang="ts">
/**
 * A second rendering of the same `EventModelGraph`, small enough to be a map (issue #296).
 *
 * Bare `<rect>`s and nothing else — no text, no edges, no cards, no interaction per node. That is
 * not minimalism for its own sake: at 106 slices the graph holds ~600 nodes, and the minimap
 * repaints on every scroll frame. A rect per node in one SVG is cheap enough that it does; a
 * second copy of the card markup would not be.
 *
 * It also tracks the filter bar for free. The graph handed here is the one the canvas drew, so a
 * slice the reader filtered out is already absent from it — the minimap cannot disagree with the
 * canvas about what exists, because it is not given a second chance to decide.
 */
import { computed, ref } from 'vue'
import { CANVAS_PADDING, GUTTER_GAP, GUTTER_WIDTH, canvasSize, type EventModelGraph } from './layout'
import { minimapScale, minimapWindow, type Rect } from './focus'
import { colorFor } from './palette'

const props = defineProps<{
  graph: EventModelGraph
  zoom: number
  /** The scroller's live offsets and box, in scaled pixels. */
  scroll: { x: number; y: number; width: number; height: number }
  /** Slice names the reader is focused on, dimmed out of the map when a focus is active. */
  focused?: ReadonlySet<string> | null
}>()

const emit = defineEmits<{
  /** A point on the minimap, in minimap pixels — the host turns it into a scroll offset. */
  'goto': [point: { x: number; y: number }]
}>()

const scale = computed(() => minimapScale(props.graph))

const box = computed(() => {
  const canvas = canvasSize(props.graph)
  return { width: canvas.width * scale.value.x, height: canvas.height * scale.value.y }
})

/** Plot space → canvas space, the same offset `rectForSlices` applies. */
const PLOT_ORIGIN = CANVAS_PADDING + GUTTER_WIDTH + GUTTER_GAP

const rects = computed(() =>
  props.graph.nodes.map((node) => ({
    id: node.id,
    sliceName: node.sliceName,
    x: (PLOT_ORIGIN + node.x) * scale.value.x,
    y: (CANVAS_PADDING + node.y) * scale.value.y,
    // A card is ~4.5 × 1.8px at 1/40 and well under a pixel wide on the 106-slice model, and a
    // sub-pixel rect renders as nothing at all — so every rect keeps a floor. The map is a shape,
    // not a measurement.
    width: Math.max(1, node.width * scale.value.x),
    height: Math.max(1, node.height * scale.value.y),
    fill: colorFor(node.element.kind)
  }))
)

const window = computed<Rect>(() =>
  minimapWindow(props.graph, props.scroll, props.zoom, scale.value)
)

const dragging = ref(false)
const svg = ref<SVGSVGElement | null>(null)

function pointFor(event: MouseEvent): { x: number; y: number } | null {
  const element = svg.value
  if (!element) return null
  const bounds = element.getBoundingClientRect()
  return { x: event.clientX - bounds.left, y: event.clientY - bounds.top }
}

function onDown(event: MouseEvent) {
  if (event.button !== 0) return
  // The minimap sits over the canvas, so a press here must never also start a canvas pan.
  event.preventDefault()
  event.stopPropagation()
  const point = pointFor(event)
  if (!point) return

  dragging.value = true
  emit('goto', point)
  globalThis.window.addEventListener('mousemove', onMove)
  globalThis.window.addEventListener('mouseup', onUp)
}

function onMove(event: MouseEvent) {
  if (!dragging.value) return
  const point = pointFor(event)
  if (point) emit('goto', point)
}

function onUp() {
  dragging.value = false
  globalThis.window.removeEventListener('mousemove', onMove)
  globalThis.window.removeEventListener('mouseup', onUp)
}

defineExpose({ endDrag: onUp })
</script>

<template>
  <svg
    ref="svg"
    class="em-minimap"
    data-testid="event-model-minimap"
    :width="box.width"
    :height="box.height"
    :viewBox="`0 0 ${box.width} ${box.height}`"
    :data-dragging="dragging ? 'true' : undefined"
    role="img"
    aria-label="Model overview"
    @mousedown="onDown"
  >
    <rect class="em-minimap-ground" x="0" y="0" :width="box.width" :height="box.height" />
    <rect
      v-for="rect in rects"
      :key="rect.id"
      class="em-minimap-node"
      :data-slice="rect.sliceName"
      :data-dimmed="focused && focused.size > 0 && !focused.has(rect.sliceName) ? 'true' : undefined"
      :x="rect.x"
      :y="rect.y"
      :width="rect.width"
      :height="rect.height"
      :fill="rect.fill"
    />
    <rect
      class="em-minimap-window"
      data-testid="minimap-window"
      :x="window.x"
      :y="window.y"
      :width="window.width"
      :height="window.height"
    />
  </svg>
</template>

<style scoped>
.em-minimap {
  display: block;
  border: 1px solid currentColor;
  border-radius: 4px;
  cursor: pointer;
  /* Opaque ground: the map hangs over the canvas, and a transparent one reads as a rendering
     artefact rather than as a second picture of the same model. */
  background: transparent;
}
.em-minimap[data-dragging='true'] {
  cursor: grabbing;
}
.em-minimap-ground {
  fill: currentColor;
  opacity: 0.06;
}
.em-minimap-node {
  opacity: 0.85;
}
/* A focused neighbourhood reads on the map the same way it reads on the canvas — the map is how a
   reader answers "where is this?", which is only useful if "this" is visible on it. */
.em-minimap-node[data-dimmed='true'] {
  opacity: 0.18;
}
.em-minimap-window {
  fill: currentColor;
  fill-opacity: 0.12;
  stroke: currentColor;
  stroke-width: 1;
  stroke-opacity: 0.8;
  pointer-events: none;
}
</style>
