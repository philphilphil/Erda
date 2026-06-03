<script setup lang="ts">
import { onMounted, onBeforeUnmount } from 'vue'
import Icon from './Icon.vue'

withDefaults(defineProps<{ title: string; maxWidth?: string }>(), {
  maxWidth: '640px',
})
const emit = defineEmits<{ (e: 'close'): void }>()

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}

onMounted(() => document.addEventListener('keydown', onKey))
onBeforeUnmount(() => document.removeEventListener('keydown', onKey))
</script>

<template>
  <div class="modal-overlay" @click.self="emit('close')">
    <div class="modal-card" role="dialog" aria-modal="true" :style="{ maxWidth }">
      <div class="modal-head">
        <span class="modal-title">{{ title }}</span>
        <button class="btn btn-ghost btn-sm btn-icon" title="Close" @click="emit('close')">
          <Icon name="x" :size="15" />
        </button>
      </div>
      <div class="modal-body">
        <slot />
      </div>
    </div>
  </div>
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 50;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.45);
}
.modal-card {
  width: 100%;
  /* max-width comes from the `maxWidth` prop (inline style) */
  max-height: calc(100vh - 40px);
  overflow-y: auto;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: var(--r-lg);
  box-shadow: 0 18px 50px rgba(0, 0, 0, 0.35);
}
.modal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 13px var(--pad-card);
  border-bottom: 1px solid var(--border);
}
.modal-title {
  font-weight: 600;
  color: var(--text);
  font-size: var(--fs);
}
.modal-body {
  padding: var(--pad-card);
}
</style>
