<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getConfig, putConfig, restart } from '../api/client'
import type { ConfigItemDto } from '../api/types'
import Card from '../components/Card.vue'
import Banner from '../components/Banner.vue'
import Icon from '../components/Icon.vue'

const items = ref<ConfigItemDto[]>([])
const loadedValues = ref<Record<string, string>>({})
const fieldValues = ref<Record<string, string>>({})
const pendingRestart = ref(false)
const restarting = ref(false)

async function load() {
  items.value = await getConfig()
  const vals: Record<string, string> = {}
  for (const item of items.value) {
    vals[item.key] = item.value ?? ''
  }
  loadedValues.value = vals
  fieldValues.value = { ...vals }
}

onMounted(load)

// Group items by item.group, preserving first-seen order.
const groups = computed<{ name: string; items: ConfigItemDto[] }[]>(() => {
  const order: string[] = []
  const byGroup = new Map<string, ConfigItemDto[]>()
  for (const item of items.value) {
    let bucket = byGroup.get(item.group)
    if (!bucket) {
      bucket = []
      byGroup.set(item.group, bucket)
      order.push(item.group)
    }
    bucket.push(item)
  }
  return order.map((name) => ({ name, items: byGroup.get(name)! }))
})

const dirty = computed(() => {
  const cur = fieldValues.value
  const loaded = loadedValues.value
  const keys = new Set([...Object.keys(cur), ...Object.keys(loaded)])
  for (const key of keys) {
    if ((cur[key] ?? '') !== (loaded[key] ?? '')) return true
  }
  return false
})

function groupIcon(name: string): string {
  switch (name) {
    case 'Model & reasoning':
      return 'cpu'
    case 'Error watch':
      return 'bell'
    case 'Reminders':
      return 'clock'
    default:
      return 'sliders'
  }
}

// Long values (timezones, paths) read better full-width.
function fieldSpan(item: ConfigItemDto): number {
  const k = item.key.toLowerCase()
  const l = item.label.toLowerCase()
  if (
    k.includes('timezone') ||
    k.includes('tz') ||
    k.includes('path') ||
    k.includes('url') ||
    k.includes('endpoint') ||
    l.includes('timezone') ||
    l.includes('path') ||
    l.includes('url') ||
    l.includes('endpoint')
  ) {
    return 12
  }
  return 6
}

async function handleSave() {
  const values: Record<string, string | null> = {}
  for (const item of items.value) {
    const v = fieldValues.value[item.key] ?? ''
    values[item.key] = v === '' ? null : v
  }
  await putConfig({ values })
  pendingRestart.value = true
  await load()
}

async function handleClearAll() {
  const values: Record<string, string | null> = {}
  for (const item of items.value) {
    values[item.key] = null
  }
  await putConfig({ values })
  pendingRestart.value = true
  await load()
}

function handleDiscard() {
  fieldValues.value = { ...loadedValues.value }
}

async function handleRestart() {
  restarting.value = true
  try {
    await restart()
  } catch {
    // Server exits, so the request may error — that's expected.
  }
}
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Config</div>
        <div class="h-sub">
          Runtime settings for the agent process. Most changes take effect only after a restart.
        </div>
      </div>
      <div class="h-actions">
        <button class="btn" :disabled="!dirty" @click="handleSave">
          <Icon name="save" :size="14" />
          Save changes
        </button>
        <button class="btn btn-danger" :disabled="restarting" @click="handleRestart">
          <Icon name="power" :size="14" />
          Restart agent
        </button>
      </div>
    </header>

    <Banner
      v-if="pendingRestart"
      tone="warn"
      icon="alert"
      strong="Restart required."
    >
      Saved configuration is staged. Restart the agent to apply model, polling, and runtime changes.
    </Banner>
    <Banner v-else tone="info" icon="info" strong="Heads up.">
      Changes to model, intervals, and runtime limits apply on the next restart.
    </Banner>

    <div class="grid-2" style="align-items: start">
      <Card
        v-for="group in groups"
        :key="group.name"
        :title="group.name"
        :icon="groupIcon(group.name)"
      >
        <div class="grid-form">
          <div
            v-for="item in group.items"
            :key="item.key"
            class="field"
            :style="`grid-column: span ${fieldSpan(item)}`"
          >
            <label :for="item.key">{{ item.label }}</label>
            <input
              :id="item.key"
              v-model="fieldValues[item.key]"
              class="input mono"
              type="text"
            />
            <span v-if="item.hint" class="hint">{{ item.hint }}</span>
            <span v-if="item.overridden" class="faint">
              (running: {{ item.effective ?? 'none' }})
            </span>
          </div>
        </div>
      </Card>
    </div>

    <div class="row between" style="margin-top: 4px">
      <button class="btn btn-ghost" @click="handleClearAll">Clear all overrides</button>
      <div class="row" style="gap: 8px">
        <button class="btn btn-ghost" :disabled="!dirty" @click="handleDiscard">Discard</button>
        <button class="btn btn-primary" :disabled="!dirty" @click="handleSave">
          <Icon name="save" :size="14" />
          Save changes
        </button>
      </div>
    </div>

    <p v-if="restarting" class="hint" style="margin-top: 12px">Restarting…</p>
  </div>
</template>
