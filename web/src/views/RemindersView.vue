<script setup lang="ts">
import { ref, onMounted } from 'vue'
import {
  getReminders,
  createReminder,
  pauseReminder,
  resumeReminder,
  deleteReminder,
} from '../api/client'
import { ApiError } from '../api/client'
import type { ReminderDto, ReminderKind } from '../api/types'

const reminders = ref<ReminderDto[]>([])
const scheduledPrompts = ref<ReminderDto[]>([])
const malformedCount = ref(0)

// Add form state
const newKind = ref<ReminderKind>('Reminder')
const newWhen = ref('')
const newText = ref('')
const addError = ref<string | null>(null)
const adding = ref(false)

async function load() {
  const data = await getReminders()
  reminders.value = data.reminders
  scheduledPrompts.value = data.scheduledPrompts
  malformedCount.value = data.malformedCount
}

onMounted(load)

async function handleAdd() {
  addError.value = null
  if (!newWhen.value.trim() || !newText.value.trim()) {
    addError.value = 'When and text are required.'
    return
  }
  adding.value = true
  try {
    await createReminder({ kind: newKind.value, when: newWhen.value.trim(), text: newText.value.trim() })
    newWhen.value = ''
    newText.value = ''
    await load()
  } catch (e) {
    if (e instanceof ApiError) {
      addError.value = e.message
    } else {
      addError.value = 'Unexpected error.'
    }
  } finally {
    adding.value = false
  }
}

async function handlePause(id: string) {
  await pauseReminder(id)
  await load()
}

async function handleResume(id: string) {
  await resumeReminder(id)
  await load()
}

async function handleDelete(id: string) {
  await deleteReminder(id)
  await load()
}
</script>

<template>
  <h1>Reminders</h1>

  <p v-if="malformedCount > 0">
    Warning: {{ malformedCount }} row(s) couldn't be parsed and were skipped.
  </p>

  <section>
    <h2>Add</h2>
    <form @submit.prevent="handleAdd">
      <select v-model="newKind">
        <option value="Reminder">Reminder</option>
        <option value="Prompt">Scheduled prompt</option>
      </select>
      <input
        v-model="newWhen"
        type="text"
        placeholder="2026-06-15 09:00 or 0 6 * * *"
      />
      <input
        v-model="newText"
        type="text"
        placeholder="message or prompt"
      />
      <button type="submit" :disabled="adding">Add</button>
    </form>
    <p v-if="addError" style="color: red;">{{ addError }}</p>
  </section>

  <section>
    <h2>Reminders</h2>
    <table v-if="reminders.length > 0" border="1" cellpadding="4" cellspacing="0">
      <thead>
        <tr>
          <th>When</th>
          <th>Text</th>
          <th>Status</th>
          <th>Next fire</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in reminders" :key="r.id">
          <td><code>{{ r.when }}</code></td>
          <td>{{ r.text }}</td>
          <td>{{ r.status }}</td>
          <td>{{ r.nextFire }}</td>
          <td>
            <button v-if="r.status === 'Active'" @click="handlePause(r.id)">Pause</button>
            <button v-if="r.status === 'Paused'" @click="handleResume(r.id)">Resume</button>
            <button @click="handleDelete(r.id)">Delete</button>
          </td>
        </tr>
      </tbody>
    </table>
    <p v-else>No reminders.</p>
  </section>

  <section>
    <h2>Scheduled prompts</h2>
    <table v-if="scheduledPrompts.length > 0" border="1" cellpadding="4" cellspacing="0">
      <thead>
        <tr>
          <th>When</th>
          <th>Text</th>
          <th>Status</th>
          <th>Next fire</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in scheduledPrompts" :key="r.id">
          <td><code>{{ r.when }}</code></td>
          <td>{{ r.text }}</td>
          <td>{{ r.status }}</td>
          <td>{{ r.nextFire }}</td>
          <td>
            <button v-if="r.status === 'Active'" @click="handlePause(r.id)">Pause</button>
            <button v-if="r.status === 'Paused'" @click="handleResume(r.id)">Resume</button>
            <button @click="handleDelete(r.id)">Delete</button>
          </td>
        </tr>
      </tbody>
    </table>
    <p v-else>No scheduled prompts.</p>
  </section>
</template>
