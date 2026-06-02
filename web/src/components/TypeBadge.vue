<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ kind: string }>()

// Activity event kinds → badge color.
const KIND_COLOR: Record<string, string> = {
  agent_run: 'b-violet',
  tool_call: 'b-blue',
  scheduled_fire: 'b-cyan',
  error_alert: 'b-red',
}

const cls = computed(() => KIND_COLOR[props.kind] ?? 'b-muted')
</script>

<template>
  <span class="badge sq" :class="cls"><span class="dot" />{{ kind }}</span>
</template>
