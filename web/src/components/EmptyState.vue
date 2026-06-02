<script setup lang="ts">
import Icon from './Icon.vue'

withDefaults(
  defineProps<{
    icon?: string
    title: string
    sub?: string
  }>(),
  {
    icon: 'inbox',
  },
)
</script>

<template>
  <div class="empty">
    <div class="e-icon"><Icon :name="icon" /></div>
    <div class="e-title">{{ title }}</div>
    <div v-if="sub" class="e-sub">{{ sub }}</div>
    <slot name="action" />
  </div>
</template>
