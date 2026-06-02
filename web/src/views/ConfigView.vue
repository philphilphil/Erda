<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getConfig, putConfig, restart } from '../api/client'
import type { ConfigItemDto } from '../api/types'

const items = ref<ConfigItemDto[]>([])
const fieldValues = ref<Record<string, string>>({})
const saveMessage = ref<string | null>(null)
const restarting = ref(false)

async function load() {
  items.value = await getConfig()
  // Initialise field values from item.value (pending override or effective)
  const vals: Record<string, string> = {}
  for (const item of items.value) {
    vals[item.key] = item.value ?? ''
  }
  fieldValues.value = vals
}

onMounted(load)

async function handleSave() {
  const values: Record<string, string | null> = {}
  for (const item of items.value) {
    values[item.key] = fieldValues.value[item.key] ?? null
  }
  await putConfig({ values })
  saveMessage.value = 'Overrides saved — restart to apply.'
  await load()
}

async function handleClearAll() {
  const values: Record<string, string | null> = {}
  for (const item of items.value) {
    values[item.key] = null
  }
  await putConfig({ values })
  saveMessage.value = 'All overrides cleared — restart to apply.'
  await load()
}

async function handleRestart() {
  restarting.value = true
  try {
    await restart()
  } catch {
    // Server exits, so the request may error — that's expected
  }
}
</script>

<template>
  <h1>Config</h1>

  <section>
    <div v-for="item in items" :key="item.key" style="margin-bottom: 1rem;">
      <label :for="item.key"><strong>{{ item.label }}</strong></label>
      <div style="color: gray; font-size: 0.9em;">{{ item.hint }}</div>
      <div v-if="item.overridden" style="font-size: 0.85em;">
        (running: {{ item.effective ?? 'none' }})
      </div>
      <input
        :id="item.key"
        v-model="fieldValues[item.key]"
        type="text"
        style="width: 40%;"
      />
    </div>

    <div>
      <button @click="handleSave">Save overrides</button>
      <button @click="handleClearAll">Clear all overrides</button>
    </div>
    <p v-if="saveMessage">{{ saveMessage }}</p>
  </section>

  <section>
    <h2>Restart</h2>
    <p style="color: gray; font-size: 0.9em;">
      Prompt and config changes apply after a restart.
    </p>
    <button @click="handleRestart" :disabled="restarting">Restart Erda</button>
    <p v-if="restarting">Restarting… reload this page in a few seconds.</p>
  </section>
</template>
