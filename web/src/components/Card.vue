<script setup lang="ts">
import { useSlots } from 'vue'
import Icon from './Icon.vue'

withDefaults(
  defineProps<{
    title?: string
    icon?: string
    sub?: string
    flush?: boolean
    noHeadBorder?: boolean
  }>(),
  {
    flush: false,
    noHeadBorder: false,
  },
)

const slots = useSlots()
</script>

<template>
  <section class="card">
    <div
      v-if="title || slots.actions"
      class="card-head"
      :class="{ 'no-border': noHeadBorder }"
    >
      <div class="card-title">
        <span v-if="icon" class="ct-icon"><Icon :name="icon" /></span>
        <span v-if="title">{{ title }}</span>
        <span v-if="sub" class="card-sub">{{ sub }}</span>
      </div>
      <div v-if="slots.actions" class="row" style="gap: 8px">
        <slot name="actions" />
      </div>
    </div>
    <div class="card-body" :class="{ flush }">
      <slot />
    </div>
  </section>
</template>
