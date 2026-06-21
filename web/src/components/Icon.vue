<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{ name: string; size?: number }>(), {
  size: 16,
})

// Simple geometric stroke icons (1.6 weight). "circle::cx,cy,r" entries
// render as <circle>, everything else as a <path d="...">.
const PATHS: Record<string, string[]> = {
  clock: ['circle::12,12,9', 'M12 7v5l3 2'],
  bell: ['M6 9a6 6 0 0 1 12 0c0 5 2 6 2 6H4s2-1 2-6', 'M10 20a2 2 0 0 0 4 0'],
  code: ['M9 8 5 12l4 4', 'M15 8l4 4-4 4', 'M13 5l-2 14'],
  activity: ['M3 12h4l2 6 4-14 2 8h6'],
  sliders: ['M4 7h10', 'M18 7h2', 'M4 17h2', 'M10 17h10', 'circle::15,7,2.4', 'circle::7,17,2.4'],
  plus: ['M12 5v14', 'M5 12h14'],
  pause: ['M9 6v12', 'M15 6v12'],
  play: ['M7 5l12 7-12 7z'],
  trash: ['M4 7h16', 'M9 7V5h6v2', 'M6 7l1 13h10l1-13'],
  save: ['M5 4h11l3 3v13H5z', 'M8 4v5h7V4', 'M8 14h8v6H8z'],
  rotate: ['M4 4v6h6', 'M4 10a8 8 0 1 1 1.5 7'],
  alert: ['M12 4 2 20h20z', 'M12 10v5', 'M12 18h.01'],
  info: ['circle::12,12,9', 'M12 11v5', 'M12 8h.01'],
  x: ['M6 6l12 12', 'M18 6 6 18'],
  panel: ['M4 5h16v14H4z', 'M9 5v14'],
  search: ['circle::11,11,7', 'M21 21l-4-4'],
  check: ['M5 13l4 4 10-11'],
  power: ['M12 4v8', 'M7 6a8 8 0 1 0 10 0'],
  inbox: ['M4 13h5l1 3h4l1-3h5', 'M4 13 7 5h10l3 8v6H4z'],
  cpu: ['M7 7h10v10H7z', 'M10 10h4v4h-4z', 'M9 3v2M15 3v2M9 19v2M15 19v2M3 9h2M3 15h2M19 9h2M19 15h2'],
  globe: ['circle::12,12,9', 'M3 12h18', 'M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18'],
  mic: ['M9 5a3 3 0 0 1 6 0v6a3 3 0 0 1-6 0z', 'M5 11a7 7 0 0 0 14 0', 'M12 18v3'],
  note: ['M6 3h9l3 3v15H6z', 'M9 8h6M9 12h6M9 16h3'],
  zap: ['M13 3 5 14h6l-1 7 8-11h-6z'],
  dot: ['circle::12,12,3'],
  terminal: ['M5 5h14v14H5z', 'M8 9l3 3-3 3', 'M13 15h4'],
  hash: ['M6 9h12M5 15h12M10 4 8 20M16 4l-2 16'],
  filter: ['M3 5h18l-7 8v6l-4-2v-4z'],
  sun: ['circle::12,12,4', 'M12 2v2.5', 'M12 19.5V22', 'M4.2 4.2l1.7 1.7', 'M18.1 18.1l1.7 1.7', 'M2 12h2.5', 'M19.5 12H22', 'M4.2 19.8l1.7-1.7', 'M18.1 5.9l1.7-1.7'],
  moon: ['M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z'],
  eye: ['M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z', 'circle::12,12,3'],
  eyeoff: ['M4 4l16 16', 'M9.6 5.3A9.7 9.7 0 0 1 12 5c6 0 10 7 10 7a17 17 0 0 1-3 3.8', 'M6.3 7.6A16 16 0 0 0 2 12s4 7 10 7a9.6 9.6 0 0 0 3-.5', 'M9.9 9.9a3 3 0 0 0 4.2 4.2'],
  chat: ['M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z'],
  pencil: ['M4 20h4L18.5 9.5l-4-4L4 16z', 'M13.5 6.5l4 4'],
  workflow: ['M4 5h6v5H4z', 'M14 14h6v5h-6z', 'M9 7.5h4a2 2 0 0 1 2 2V14'],
  arrowright: ['M5 12h14', 'M13 6l6 6-6 6'],
  more: ['circle::5,12,1.5', 'circle::12,12,1.5', 'circle::19,12,1.5'],
}

interface CircleSpec {
  type: 'circle'
  cx: string
  cy: string
  r: string
}
interface PathSpec {
  type: 'path'
  d: string
}
type Shape = CircleSpec | PathSpec

const shapes = computed<Shape[]>(() =>
  (PATHS[props.name] ?? []).map((s): Shape => {
    if (s.startsWith('circle::')) {
      const [cx, cy, r] = s.slice(8).split(',')
      return { type: 'circle', cx, cy, r }
    }
    return { type: 'path', d: s }
  }),
)
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    :stroke-width="1.6"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <template v-for="(s, i) in shapes" :key="i">
      <circle v-if="s.type === 'circle'" :cx="s.cx" :cy="s.cy" :r="s.r" />
      <path v-else :d="s.d" />
    </template>
  </svg>
</template>
