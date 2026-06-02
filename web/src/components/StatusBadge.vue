<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ status: 'Active' | 'Paused' | 'Done' }>()

const STATUS_MAP: Record<string, string> = {
  Active: 'b-green',
  Paused: 'b-amber',
  Done: 'b-muted',
}

const cls = computed(() => STATUS_MAP[props.status] ?? 'b-muted')
const label = computed(() => props.status.toLowerCase())
</script>

<template>
  <span class="badge" :class="cls"><span class="dot" />{{ label }}</span>
</template>
