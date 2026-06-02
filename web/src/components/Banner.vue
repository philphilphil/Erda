<script setup lang="ts">
import Icon from './Icon.vue'

withDefaults(
  defineProps<{
    tone?: 'info' | 'warn'
    icon?: string
    strong?: string
  }>(),
  {
    tone: 'info',
    icon: 'info',
  },
)
</script>

<template>
  <div class="banner" :class="tone">
    <Icon :name="icon" />
    <div>
      <span v-if="strong" class="b-strong">{{ strong }} </span>
      <span class="b-text"><slot /></span>
    </div>
  </div>
</template>
