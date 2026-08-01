<script setup lang="ts">
// Brand mark — Erda's initial, built as strata.
//
// The name is the earth-mother who sleeps below and rises to give counsel, so the E is drawn as
// three ground layers narrowing with depth, with the spine carrying up past the top layer — the
// form breaking the surface. A letterform rather than a pictogram on purpose: this renders at
// 15–16px, where letters stay legible and small pictograms turn to mush, and it replaces a generic
// three-node graph glyph that could have belonged to any dev tool.
//
// Built for its render size: four strokes, nothing narrower than the stroke weight, and the layer
// spacing kept wide enough that the arms do not merge at 16px. Inherits `currentColor`, which is
// what lets the chip invert with the theme. Kept in sync by hand with public/favicon.svg, which
// bakes the chip in.
</script>

<template>
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2.4"
    stroke-linecap="round"
    aria-hidden="true"
  >
    <path d="M5.5 3v17.5" />
    <path d="M5.5 8h13" />
    <path d="M5.5 14.25h10" />
    <path d="M5.5 20.5h7" />
  </svg>
</template>
