<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { getActivity } from '../api/client'
import type { ActivityDto } from '../api/types'

const MAX_ENTRIES = 200

const allEntries = ref<ActivityDto[]>([])
const activeFilter = ref<string>('All')
const filters = ['All', 'agent_run', 'tool_call', 'scheduled_fire', 'error_alert']

let es: EventSource | null = null

async function load() {
  allEntries.value = await getActivity(100)
}

onMounted(async () => {
  await load()

  es = new EventSource('/api/activity/stream')
  es.onmessage = (event: MessageEvent) => {
    try {
      const entry = JSON.parse(event.data as string) as ActivityDto
      allEntries.value = [entry, ...allEntries.value].slice(0, MAX_ENTRIES)
    } catch {
      // ignore parse errors
    }
  }
})

onBeforeUnmount(() => {
  es?.close()
  es = null
})

const filteredEntries = computed(() => {
  if (activeFilter.value === 'All') return allEntries.value
  return allEntries.value.filter((e) => e.kind === activeFilter.value)
})
</script>

<template>
  <h1>Activity</h1>

  <div>
    <button
      v-for="f in filters"
      :key="f"
      @click="activeFilter = f"
      :style="activeFilter === f ? 'font-weight: bold;' : ''"
    >
      {{ f }}
    </button>
  </div>

  <table v-if="filteredEntries.length > 0" border="1" cellpadding="4" cellspacing="0" style="width: 100%;">
    <thead>
      <tr>
        <th>Time</th>
        <th>Kind</th>
        <th>Summary</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="entry in filteredEntries" :key="entry.id">
        <td style="white-space: nowrap;">{{ new Date(entry.timestampUtc).toLocaleString() }}</td>
        <td><code>{{ entry.kind }}</code></td>
        <td>{{ entry.summary }}</td>
      </tr>
    </tbody>
  </table>
  <p v-else>No activity entries.</p>
</template>
