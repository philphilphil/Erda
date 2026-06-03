<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  getReminders,
  createReminder,
  updateReminder,
  pauseReminder,
  resumeReminder,
  deleteReminder,
  getSystemSchedules,
  ApiError,
} from '../api/client'
import type { ReminderDto, ReminderKind, SystemScheduleDto } from '../api/types'
import { VueDatePicker } from '@vuepic/vue-datepicker'
import cronstrue from 'cronstrue'
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'
import Modal from '../components/Modal.vue'
import StatusBadge from '../components/StatusBadge.vue'
import Banner from '../components/Banner.vue'
import EmptyState from '../components/EmptyState.vue'

const reminders = ref<ReminderDto[]>([])
const scheduledPrompts = ref<ReminderDto[]>([])
const malformedCount = ref(0)
const systemSchedules = ref<SystemScheduleDto[]>([])

// View state
const adding = ref(false)
const hideDone = ref(true)

// Add form state
const newKind = ref<ReminderKind>('Reminder')
const newSchedKind = ref<'datetime' | 'cron'>('datetime')
const newWhen = ref('')
const newText = ref('')
const newDirectToCodex = ref(false)
const newPreScript = ref('')
const addError = ref<string | null>(null)
const submitting = ref(false)

// Edit modal state (scheduled prompts only)
const editing = ref<ReminderDto | null>(null)
const editSchedKind = ref<'datetime' | 'cron'>('cron')
const editWhen = ref('')
const editText = ref('')
const editDirectToCodex = ref(false)
const editPreScript = ref('')
const editError = ref<string | null>(null)
const savingEdit = ref(false)

const scriptHint =
  'Runs in /bin/sh before the prompt; its stdout is injected. Use {{context}} to place it in the prompt, otherwise it is prepended.'

// ---- cron description (human-readable, via cronstrue) ----
function describeCron(expr: string): string {
  try {
    return cronstrue.toString(expr, { use24HourTimeFormat: true, verbose: false })
  } catch {
    return expr // unparseable → show the raw expression
  }
}

function isCron(when: string): boolean {
  return when.trim().split(/\s+/).length === 5
}

function scheduleLabel(when: string): string {
  return isCron(when) ? describeCron(when) : 'one-time'
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

// Live cronstrue preview under the create form's cron input (empty unless a 5-field cron is typed).
const newCronPreview = computed(() =>
  newSchedKind.value === 'cron' && isCron(String(newWhen.value ?? ''))
    ? describeCron(String(newWhen.value))
    : '',
)

async function load() {
  const data = await getReminders()
  reminders.value = data.reminders
  scheduledPrompts.value = data.scheduledPrompts
  malformedCount.value = data.malformedCount
}

onMounted(async () => {
  await load()
  // Read-only; doesn't change with reminder edits, so fetch once.
  systemSchedules.value = (await getSystemSchedules()).schedules
})

function resetAddForm() {
  newWhen.value = ''
  newText.value = ''
  newDirectToCodex.value = false
  newPreScript.value = ''
}

function toggleAdding() {
  adding.value = !adding.value
  if (!adding.value) {
    addError.value = null
    resetAddForm()
  }
}

async function handleAdd() {
  addError.value = null
  if (!canAdd.value) {
    addError.value = 'When and text are required.'
    return
  }
  submitting.value = true
  try {
    const isPrompt = newKind.value === 'Prompt'
    await createReminder({
      kind: newKind.value,
      when: String(newWhen.value ?? '').trim(),
      text: newText.value.trim(),
      directToCodex: isPrompt ? newDirectToCodex.value : undefined,
      preScript: isPrompt ? newPreScript.value.trim() || null : undefined,
    })
    resetAddForm()
    await load()
    adding.value = false
  } catch (e) {
    addError.value = e instanceof ApiError ? e.message : 'Unexpected error.'
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

// ---- edit modal (scheduled prompts) ----
const editChars = computed(() => editText.value.length)
const editTokens = computed(() => Math.max(1, Math.round(editText.value.length / 4)))
const editCronPreview = computed(() =>
  editSchedKind.value === 'cron' && isCron(editWhen.value) ? describeCron(editWhen.value) : '',
)
const canSaveEdit = computed(
  () => String(editWhen.value ?? '').trim().length > 0 && editText.value.trim().length > 0,
)

function openEdit(r: ReminderDto) {
  editing.value = r
  editError.value = null
  if (isCron(r.when)) {
    editSchedKind.value = 'cron'
    editWhen.value = r.when
  } else {
    editSchedKind.value = 'datetime'
    // VueDatePicker's model-type uses 'T'; a stored one-time may use a space separator.
    editWhen.value = r.when.replace(' ', 'T')
  }
  editText.value = r.text
  editDirectToCodex.value = r.directToCodex
  editPreScript.value = r.preScript ?? ''
}

function closeEdit() {
  editing.value = null
}

async function saveEdit() {
  if (!editing.value) return
  editError.value = null
  if (!canSaveEdit.value) {
    editError.value = 'When and text are required.'
    return
  }
  savingEdit.value = true
  try {
    await updateReminder(editing.value.id, {
      when: String(editWhen.value ?? '').trim(),
      text: editText.value.trim(),
      directToCodex: editDirectToCodex.value,
      preScript: editPreScript.value.trim() || null,
    })
    await load()
    editing.value = null
  } catch (e) {
    editError.value = e instanceof ApiError ? e.message : 'Unexpected error.'
  } finally {
    savingEdit.value = false
  }
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
                ? newCronPreview || 'min hour dom mon dow — e.g. 0 6 * * *'
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

        <template v-if="newKind === 'Prompt'">
          <div class="field" style="grid-column: span 12">
            <label class="check-row">
              <input v-model="newDirectToCodex" type="checkbox" />
              Run directly via Codex (skip the agent)
            </label>
            <span class="hint">
              Good for big prompts (e.g. a daily news digest); web search is on.
            </span>
          </div>
          <div class="field" style="grid-column: span 12">
            <label>Pre-run script <span class="faint">(optional)</span></label>
            <textarea
              v-model="newPreScript"
              class="textarea mono"
              rows="2"
              spellcheck="false"
              placeholder="curl -s https://api.example.com/weather"
            />
            <span class="hint">{{ scriptHint }}</span>
          </div>
        </template>
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
                <span class="faint" style="font-size: var(--fs-xs)">{{ scheduleLabel(r.when) }}</span>
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
                <span class="faint" style="font-size: var(--fs-xs)">{{ scheduleLabel(r.when) }}</span>
              </div>
            </td>
            <td class="cell-msg">
              <div class="msg-cell">
                <span class="truncate" :title="r.text">{{ r.text }}</span>
                <span
                  v-if="r.directToCodex"
                  class="badge sq b-blue"
                  title="Runs directly via Codex (skips the agent)"
                  >Codex</span
                >
                <span
                  v-if="r.preScript"
                  class="badge sq b-muted"
                  title="Runs a pre-run script before the prompt"
                  >script</span
                >
              </div>
            </td>
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
                  class="btn btn-ghost btn-sm btn-icon"
                  title="Edit"
                  @click="openEdit(r)"
                >
                  <Icon name="pencil" :size="14" />
                </button>
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

    <Card title="System scheduled" icon="cpu" sub="read-only · background" flush>
      <div v-if="systemSchedules.length > 0" class="sys-list">
        <div v-for="s in systemSchedules" :key="s.key" class="sys-row">
          <div class="sys-main">
            <span class="sys-icon"><Icon :name="s.icon" :size="15" /></span>
            <span class="sys-name">{{ s.name }}</span>
            <span class="badge" :class="s.enabled ? 'b-green' : 'b-muted'">
              <span class="dot" />{{ s.status }}
            </span>
          </div>
          <div class="sys-desc">{{ s.description }}</div>
          <div class="sys-tags">
            <span v-for="t in s.tags" :key="t" class="badge sq b-muted">{{ t }}</span>
          </div>
        </div>
      </div>
      <div v-else class="card-body">
        <EmptyState icon="cpu" title="No background schedules" sub="Nothing is running on its own." />
      </div>
    </Card>

    <!-- Edit scheduled prompt -->
    <Modal v-if="editing" title="Edit scheduled prompt" max-width="min(1000px, 96vw)" @close="closeEdit">
      <div class="grid-form">
        <div class="field" style="grid-column: span 4">
          <label>Schedule kind</label>
          <select v-model="editSchedKind" class="select" @change="editWhen = ''">
            <option value="datetime">One-time datetime</option>
            <option value="cron">Cron expression</option>
          </select>
        </div>
        <div class="field" style="grid-column: span 8">
          <label>{{ editSchedKind === 'cron' ? 'Cron' : 'When' }}</label>
          <VueDatePicker
            v-if="editSchedKind === 'datetime'"
            v-model="editWhen"
            model-type="yyyy-MM-dd'T'HH:mm"
            format="yyyy-MM-dd HH:mm"
            :time-config="{ is24: true, enableSeconds: false }"
            auto-apply
            placeholder="Select date & time"
          />
          <input v-else v-model="editWhen" class="input mono" type="text" placeholder="0 6 * * *" />
          <span class="hint">
            {{
              editSchedKind === 'cron'
                ? editCronPreview || 'min hour dom mon dow — e.g. 0 6 * * *'
                : 'Fires once, at the selected local time'
            }}
          </span>
        </div>

        <div class="field" style="grid-column: span 12">
          <label>Prompt for the agent</label>
          <textarea
            v-model="editText"
            class="textarea mono"
            rows="18"
            spellcheck="false"
            style="min-height: 380px; font-size: var(--fs-sm); line-height: 1.6"
          />
          <span class="hint">
            {{ editChars.toLocaleString() }} chars · ~{{ editTokens.toLocaleString() }} tok
          </span>
        </div>

        <div class="field" style="grid-column: span 12">
          <label class="check-row">
            <input v-model="editDirectToCodex" type="checkbox" />
            Run directly via Codex (skip the agent)
          </label>
        </div>

        <div class="field" style="grid-column: span 12">
          <label>Pre-run script <span class="faint">(optional)</span></label>
          <textarea
            v-model="editPreScript"
            class="textarea mono"
            rows="2"
            spellcheck="false"
            placeholder="curl -s https://api.example.com/weather"
          />
          <span class="hint">{{ scriptHint }}</span>
        </div>
      </div>

      <Banner v-if="editError" tone="warn" icon="alert" style="margin-top: 12px">
        {{ editError }}
      </Banner>

      <div class="row between" style="margin-top: 14px">
        <span class="faint mono" style="font-size: var(--fs-xs)">{{ editing.id }}</span>
        <div class="row" style="gap: 8px">
          <button class="btn btn-ghost" :disabled="savingEdit" @click="closeEdit">Cancel</button>
          <button class="btn btn-primary" :disabled="!canSaveEdit || savingEdit" @click="saveEdit">
            <Icon name="save" :size="14" />
            Save
          </button>
        </div>
      </div>
    </Modal>
  </div>
</template>

<style scoped>
.msg-cell {
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
}
.check-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-direction: row !important;
  cursor: pointer;
  text-transform: none;
  letter-spacing: 0;
  font-size: var(--fs-sm);
  color: var(--text);
}
.check-row input {
  width: 15px;
  height: 15px;
  accent-color: var(--blue);
  cursor: pointer;
}
.sys-list {
  display: flex;
  flex-direction: column;
}
.sys-row {
  padding: 13px var(--pad-card);
  border-bottom: 1px solid var(--border);
}
.sys-row:last-child {
  border-bottom: none;
}
.sys-main {
  display: flex;
  align-items: center;
  gap: 9px;
}
.sys-icon {
  display: grid;
  place-items: center;
  color: var(--text-faint);
  flex: 0 0 auto;
}
.sys-name {
  font-weight: 500;
  color: var(--text);
  font-size: var(--fs);
}
.sys-main .badge {
  margin-left: auto;
}
.sys-desc {
  color: var(--text-muted);
  font-size: var(--fs-sm);
  line-height: 1.5;
  margin-top: 5px;
}
.sys-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}
</style>
