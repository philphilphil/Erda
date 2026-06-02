<script setup lang="ts">
// Brand mark — a small node/graph glyph. No props.
</script>

<template>
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    :stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
  >
    <circle :cx="12" :cy="6" :r="2.2" fill="currentColor" stroke="none" />
    <circle :cx="6" :cy="17" :r="2.2" fill="currentColor" stroke="none" />
    <circle :cx="18" :cy="17" :r="2.2" fill="currentColor" stroke="none" />
    <path d="M12 8.2 7 15M12 8.2 17 15M8 17h8" />
  </svg>
</template>
