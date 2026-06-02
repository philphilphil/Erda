<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  getReminders,
  createReminder,
  pauseReminder,
  resumeReminder,
  deleteReminder,
  ApiError,
} from '../api/client'
import type { ReminderDto, ReminderKind } from '../api/types'
import { VueDatePicker } from '@vuepic/vue-datepicker'
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'
import StatusBadge from '../components/StatusBadge.vue'
import Banner from '../components/Banner.vue'
import EmptyState from '../components/EmptyState.vue'

const reminders = ref<ReminderDto[]>([])
const scheduledPrompts = ref<ReminderDto[]>([])
const malformedCount = ref(0)

// View state
const adding = ref(false)
const hideDone = ref(true)

// Add form state
const newKind = ref<ReminderKind>('Reminder')
const newSchedKind = ref<'datetime' | 'cron'>('datetime')
const newWhen = ref('')
const newText = ref('')
const addError = ref<string | null>(null)
const submitting = ref(false)

// ---- tiny helpers ported from the design prototype ----
const pad = (n: number) => String(n).padStart(2, '0')

const DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
function describeCron(expr: string): string {
  const p = expr.trim().split(/\s+/)
  if (p.length !== 5) return expr
  const [min, hr, dom, mon, dow] = p
  if (min === '0' && hr === '6' && dom === '*' && dow === '*') return 'Daily at 06:00'
  if (dom === '*' && mon === '*' && dow === '*' && min !== '*' && hr !== '*')
    return `Daily at ${pad(+hr)}:${pad(+min)}`
  if (dow !== '*' && dow !== '?') {
    const days = dow
      .split(',')
      .map((d) => DOW[+d] || d)
      .join(', ')
    return `${days} at ${pad(+hr)}:${pad(+min)}`
  }
  if (min.startsWith('*/')) return `Every ${min.slice(2)} min`
  if (hr.startsWith('*/')) return `Every ${hr.slice(2)} h`
  return expr
}

function isCron(when: string): boolean {
  return when.trim().split(/\s+/).length === 5
}

// ---- derived counts (across both lists) ----
const allItems = computed(() => [...reminders.value, ...scheduledPrompts.value])
const doneCount = computed(() => allItems.value.filter((i) => i.status === 'Done').length)
const activeCount = computed(() => allItems.value.filter((i) => i.status === 'Active').length)
const pausedCount = computed(() => allItems.value.filter((i) => i.status === 'Paused').length)

const visibleReminders = computed(() =>
  hideDone.value ? reminders.value.filter((i) => i.status !== 'Done') : reminders.value,
)
const visiblePrompts = computed(() =>
  hideDone.value ? scheduledPrompts.value.filter((i) => i.status !== 'Done') : scheduledPrompts.value,
)

const canAdd = computed(
  () => String(newWhen.value ?? '').trim().length > 0 && newText.value.trim().length > 0,
)

async function load() {
  const data = await getReminders()
  reminders.value = data.reminders
  scheduledPrompts.value = data.scheduledPrompts
  malformedCount.value = data.malformedCount
}

onMounted(load)

function toggleAdding() {
  adding.value = !adding.value
  if (!adding.value) addError.value = null
}

async function handleAdd() {
  addError.value = null
  if (!canAdd.value) {
    addError.value = 'When and text are required.'
    return
  }
  submitting.value = true
  try {
    await createReminder({
      kind: newKind.value,
      when: String(newWhen.value ?? '').trim(),
      text: newText.value.trim(),
    })
    newWhen.value = ''
    newText.value = ''
    await load()
    adding.value = false
  } catch (e) {
    if (e instanceof ApiError) {
      addError.value = e.message
    } else {
      addError.value = 'Unexpected error.'
    }
  } finally {
    submitting.value = false
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
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Schedules</div>
        <div class="h-sub">
          Verbatim reminders and recurring agent-run prompts. Erda fires each on its schedule
          and logs the result to Activity.
        </div>
      </div>
      <div class="h-actions">
        <button
          class="btn"
          :title="hideDone ? 'Show completed reminders' : 'Hide completed reminders'"
          @click="hideDone = !hideDone"
        >
          <Icon :name="hideDone ? 'eye' : 'eyeoff'" :size="14" />
          {{ hideDone ? `Show done${doneCount ? ` · ${doneCount}` : ''}` : 'Hide done' }}
        </button>
        <button class="btn" :class="adding ? '' : 'btn-primary'" @click="toggleAdding">
          <Icon :name="adding ? 'x' : 'plus'" :size="14" />
          {{ adding ? 'Close' : 'New' }}
        </button>
      </div>
    </header>

    <div class="stat-strip">
      <div class="stat">
        <div class="s-label">Active</div>
        <div class="s-val"><span class="data">{{ activeCount }}</span></div>
      </div>
      <div class="stat">
        <div class="s-label">Reminders</div>
        <div class="s-val"><span class="data">{{ reminders.length }}</span></div>
      </div>
      <div class="stat">
        <div class="s-label">Scheduled prompts</div>
        <div class="s-val"><span class="data">{{ scheduledPrompts.length }}</span></div>
      </div>
      <div class="stat">
        <div class="s-label">Paused</div>
        <div class="s-val"><span class="data">{{ pausedCount }}</span></div>
      </div>
    </div>

    <Banner v-if="malformedCount > 0" tone="warn" icon="alert">
      {{ malformedCount }} row(s) couldn't be parsed and were skipped.
    </Banner>

    <Card v-if="adding" title="New schedule" icon="plus">
      <div class="grid-form">
        <div class="field" style="grid-column: span 3">
          <label>Type</label>
          <select v-model="newKind" class="select">
            <option value="Reminder">Reminder (verbatim)</option>
            <option value="Prompt">Scheduled prompt</option>
          </select>
        </div>
        <div class="field" style="grid-column: span 3">
          <label>Schedule kind</label>
          <select v-model="newSchedKind" class="select" @change="newWhen = ''">
            <option value="datetime">One-time datetime</option>
            <option value="cron">Cron expression</option>
          </select>
        </div>
        <div class="field" style="grid-column: span 6">
          <label>{{ newSchedKind === 'cron' ? 'Cron' : 'When' }}</label>
          <VueDatePicker
            v-if="newSchedKind === 'datetime'"
            v-model="newWhen"
            model-type="yyyy-MM-dd'T'HH:mm"
            format="yyyy-MM-dd HH:mm"
            :time-config="{ is24: true, enableSeconds: false }"
            :min-date="new Date()"
            auto-apply
            placeholder="Select date & time"
          />
          <input
            v-else
            v-model="newWhen"
            class="input mono"
            type="text"
            placeholder="0 6 * * *"
          />
          <span class="hint">
            {{
              newSchedKind === 'cron'
                ? 'min hour dom mon dow — e.g. 0 6 * * *'
                : 'Fires once, at the selected local time'
            }}
          </span>
        </div>
        <div class="field" style="grid-column: span 12">
          <label>{{ newKind === 'Prompt' ? 'Prompt for the agent' : 'Message text' }}</label>
          <textarea
            v-model="newText"
            class="textarea"
            rows="2"
            :placeholder="
              newKind === 'Prompt'
                ? 'Summarise overnight logs and flag anything above WARN…'
                : 'Pick up the dry cleaning'
            "
          />
        </div>
      </div>

      <Banner v-if="addError" tone="warn" icon="alert" style="margin-top: 12px">
        {{ addError }}
      </Banner>

      <div class="row between" style="margin-top: 12px">
        <span class="faint" style="font-size: var(--fs-xs)">
          {{
            newKind === 'Prompt'
              ? 'Runs the agent on a schedule and acts on the result.'
              : 'Fires the exact message to you on WhatsApp.'
          }}
        </span>
        <div class="row" style="gap: 8px">
          <button class="btn btn-ghost" :disabled="submitting" @click="toggleAdding">Cancel</button>
          <button class="btn btn-primary" :disabled="!canAdd || submitting" @click="handleAdd">
            <Icon name="plus" :size="14" />
            Add
          </button>
        </div>
      </div>
    </Card>

    <Card title="Reminders" icon="bell" :sub="`${visibleReminders.length} · verbatim`" flush>
      <table v-if="visibleReminders.length > 0" class="tbl">
        <colgroup>
          <col style="width: 22%" />
          <col />
          <col style="width: 11%" />
          <col style="width: 17%" />
          <col style="width: 13%" />
        </colgroup>
        <thead>
          <tr>
            <th>Schedule</th>
            <th>Message / Prompt</th>
            <th>Status</th>
            <th>Next fire</th>
            <th class="col-actions"></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="r in visibleReminders"
            :key="r.id"
            :class="{ 'is-paused': r.status === 'Paused', 'is-done': r.status === 'Done' }"
          >
            <td>
              <div style="display: flex; flex-direction: column; gap: 1px">
                <span class="mono" style="font-size: var(--fs-sm); white-space: nowrap">{{ r.when }}</span>
                <span class="faint" style="font-size: var(--fs-xs)">
                  {{ isCron(r.when) ? describeCron(r.when) : 'one-time' }}
                </span>
              </div>
            </td>
            <td class="cell-msg"><span class="truncate" :title="r.text">{{ r.text }}</span></td>
            <td><StatusBadge :status="r.status" /></td>
            <td>
              <span
                v-if="r.status === 'Active'"
                class="mono"
                style="font-size: var(--fs-sm); white-space: nowrap"
                >{{ r.nextFire }}</span
              >
              <span v-else-if="r.status === 'Paused'" class="faint" style="font-size: var(--fs-xs)"
                >paused</span
              >
              <span v-else class="faint mono" style="font-size: var(--fs-xs)">—</span>
            </td>
            <td class="col-actions">
              <div class="row-actions">
                <button
                  v-if="r.status === 'Active'"
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Pause"
                  @click="handlePause(r.id)"
                >
                  <Icon name="pause" :size="14" />
                </button>
                <button
                  v-else-if="r.status === 'Paused'"
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Resume"
                  @click="handleResume(r.id)"
                >
                  <Icon name="play" :size="14" />
                </button>
                <button
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Delete"
                  @click="handleDelete(r.id)"
                >
                  <Icon name="trash" :size="14" />
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else class="card-body">
        <EmptyState
          icon="inbox"
          title="Nothing scheduled here"
          sub="Add one above to have Erda fire it on time."
        />
      </div>
    </Card>

    <Card title="Scheduled prompts" icon="zap" :sub="`${visiblePrompts.length} · agent-run`" flush>
      <table v-if="visiblePrompts.length > 0" class="tbl">
        <colgroup>
          <col style="width: 22%" />
          <col />
          <col style="width: 11%" />
          <col style="width: 17%" />
          <col style="width: 13%" />
        </colgroup>
        <thead>
          <tr>
            <th>Schedule</th>
            <th>Message / Prompt</th>
            <th>Status</th>
            <th>Next fire</th>
            <th class="col-actions"></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="r in visiblePrompts"
            :key="r.id"
            :class="{ 'is-paused': r.status === 'Paused', 'is-done': r.status === 'Done' }"
          >
            <td>
              <div style="display: flex; flex-direction: column; gap: 1px">
                <span class="mono" style="font-size: var(--fs-sm); white-space: nowrap">{{ r.when }}</span>
                <span class="faint" style="font-size: var(--fs-xs)">
                  {{ isCron(r.when) ? describeCron(r.when) : 'one-time' }}
                </span>
              </div>
            </td>
            <td class="cell-msg"><span class="truncate" :title="r.text">{{ r.text }}</span></td>
            <td><StatusBadge :status="r.status" /></td>
            <td>
              <span
                v-if="r.status === 'Active'"
                class="mono"
                style="font-size: var(--fs-sm); white-space: nowrap"
                >{{ r.nextFire }}</span
              >
              <span v-else-if="r.status === 'Paused'" class="faint" style="font-size: var(--fs-xs)"
                >paused</span
              >
              <span v-else class="faint mono" style="font-size: var(--fs-xs)">—</span>
            </td>
            <td class="col-actions">
              <div class="row-actions">
                <button
                  v-if="r.status === 'Active'"
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Pause"
                  @click="handlePause(r.id)"
                >
                  <Icon name="pause" :size="14" />
                </button>
                <button
                  v-else-if="r.status === 'Paused'"
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Resume"
                  @click="handleResume(r.id)"
                >
                  <Icon name="play" :size="14" />
                </button>
                <button
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Delete"
                  @click="handleDelete(r.id)"
                >
                  <Icon name="trash" :size="14" />
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else class="card-body">
        <EmptyState
          icon="inbox"
          title="Nothing scheduled here"
          sub="Add one above to have Erda fire it on time."
        />
      </div>
    </Card>
  </div>
</template>
