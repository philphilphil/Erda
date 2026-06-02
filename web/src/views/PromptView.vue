<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getPrompt, savePrompt, activateVersion } from '../api/client'
import type { PromptVersionDto } from '../api/types'

const editorContent = ref('')
const note = ref('')
const versions = ref<PromptVersionDto[]>([])
const saveMessage = ref<string | null>(null)
const saving = ref(false)

async function load() {
  const data = await getPrompt()
  editorContent.value = data.activeContent
  versions.value = data.versions
}

onMounted(load)

const charCount = computed(() => editorContent.value.length)
const tokenEstimate = computed(() => Math.floor(editorContent.value.length / 4))

async function handleSave() {
  if (!editorContent.value.trim()) return
  saving.value = true
  saveMessage.value = null
  try {
    await savePrompt({ content: editorContent.value, note: note.value || null })
    note.value = ''
    saveMessage.value = 'Saved — restart to apply.'
    await load()
  } finally {
    saving.value = false
  }
}

async function handleActivate(id: number) {
  await activateVersion(id)
  await load()
}
</script>

<template>
  <h1>System Prompt</h1>

  <p>
    Changes take effect after a restart. Use
    <RouterLink to="/config">Config → Restart Erda</RouterLink> when ready.
  </p>

  <section>
    <textarea
      v-model="editorContent"
      rows="20"
      style="width: 100%; font-family: monospace;"
    ></textarea>
    <div>{{ charCount }} chars / ~{{ tokenEstimate }} tokens</div>
    <div>
      <input v-model="note" type="text" placeholder="Optional note for this version" style="width: 40%;" />
      <button @click="handleSave" :disabled="saving || !editorContent.trim()">
        Save new version
      </button>
    </div>
    <p v-if="saveMessage">{{ saveMessage }}</p>
  </section>

  <section>
    <h2>Version history</h2>
    <table v-if="versions.length > 0" border="1" cellpadding="4" cellspacing="0">
      <thead>
        <tr>
          <th>Created</th>
          <th>Note</th>
          <th>Active</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="v in versions" :key="v.id">
          <td>{{ new Date(v.createdAtUtc).toLocaleString() }}</td>
          <td>{{ v.note ?? '—' }}</td>
          <td>{{ v.isActive ? '✓' : '' }}</td>
          <td>
            <button @click="handleActivate(v.id)" :disabled="v.isActive">Restore</button>
          </td>
        </tr>
      </tbody>
    </table>
    <p v-else>No versions yet.</p>
  </section>
</template>
