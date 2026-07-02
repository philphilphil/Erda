<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { getActivity } from '../api/client'
import type { ActivityDto } from '../api/types'
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'
import TypeBadge from '../components/TypeBadge.vue'
import EmptyState from '../components/EmptyState.vue'

const MAX_ENTRIES = 200

// ── time helpers (ported from the design prototype) ───────────────────────────
const pad = (n: number) => String(n).padStart(2, '0')
function fmtTime(d: Date): string {
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

// ── known activity kinds → chip dot color ─────────────────────────────────────
const KIND_TYPES: { kind: string; color: string; label: string }[] = [
  { kind: 'agent_run', color: 'violet', label: 'agent_run' },
  { kind: 'tool_call', color: 'blue', label: 'tool_call' },
  { kind: 'scheduled_fire', color: 'cyan', label: 'scheduled_fire' },
  { kind: 'error_alert', color: 'red', label: 'error_alert' },
]
const KNOWN_KINDS = KIND_TYPES.map((t) => t.kind)

// ── state ─────────────────────────────────────────────────────────────────────
const entries = ref<ActivityDto[]>([])
const live = ref(true)
const active = ref<Set<string>>(new Set(KNOWN_KINDS))

// Track the newest id seen on the previous render so only genuinely-new rows animate.
const seenMax = ref(0)

let es: EventSource | null = null

async function load() {
  entries.value = await getActivity(100)
}

onMounted(async () => {
  await load()
  seenMax.value = entries.value.length ? entries.value[0].id : 0

  es = new EventSource('/api/activity/stream')
  es.onmessage = (event: MessageEvent) => {
    if (!live.value) return // paused: ignore incoming, but keep the stream open
    try {
      const entry = JSON.parse(event.data as string) as ActivityDto
      entries.value = [entry, ...entries.value].slice(0, MAX_ENTRIES)
    } catch {
      // ignore parse errors
    }
  }
})

onBeforeUnmount(() => {
  es?.close()
  es = null
})

// ── derived ───────────────────────────────────────────────────────────────────
function isVisible(kind: string): boolean {
  // Unknown kinds are always shown so unexpected events never silently vanish.
  if (!KNOWN_KINDS.includes(kind)) return true
  return active.value.has(kind)
}

const shown = computed(() => entries.value.filter((e) => isVisible(e.kind)))

const allOn = computed(() => active.value.size === KNOWN_KINDS.length)

const agentRuns = computed(() => entries.value.filter((e) => e.kind === 'agent_run').length)
const toolCalls = computed(() => entries.value.filter((e) => e.kind === 'tool_call').length)
const errors = computed(() => entries.value.filter((e) => e.kind === 'error_alert').length)

// The max id captured before this render — rows above it are "new" and animate.
const prevMax = computed(() => {
  const max = entries.value.length ? entries.value[0].id : 0
  const prev = seenMax.value
  seenMax.value = Math.max(seenMax.value, max)
  return prev
})

function chipColorVar(color: string): string {
  return `var(--${color === 'muted' ? 'text-faint' : color})`
}

function toggleType(kind: string) {
  const next = new Set(active.value)
  if (next.has(kind)) next.delete(kind)
  else next.add(kind)
  active.value = next
}

function enableAll() {
  active.value = new Set(KNOWN_KINDS)
}

function clearFeed() {
  entries.value = []
}

// ── detail expansion (tool-call arguments etc.) ───────────────────────────────
const expanded = ref<Set<number>>(new Set())
function toggleExpand(id: number) {
  const next = new Set(expanded.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  expanded.value = next
}
function prettyDetail(detail: string): string {
  try {
    return JSON.stringify(JSON.parse(detail), null, 2)
  } catch {
    return detail // not JSON → show as-is
  }
}
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Activity</div>
        <div class="h-sub">
          Live event stream — agent runs, tool calls, scheduled fires, and alerts as they happen.
        </div>
      </div>
      <div class="h-actions">
        <div class="live-toggle">
          <span class="pulse-dot" :style="live ? undefined : { background: 'var(--text-faint)' }" />
          <span
            class="mono"
            :style="{
              fontSize: 'var(--fs-xs)',
              color: live ? 'var(--green)' : 'var(--text-faint)',
            }"
          >
            {{ live ? 'LIVE' : 'PAUSED' }}
          </span>
        </div>
        <button class="btn" @click="live = !live">
          <Icon :name="live ? 'pause' : 'play'" :size="14" />
          {{ live ? 'Pause' : 'Resume' }}
        </button>
        <button class="btn btn-ghost btn-icon" title="Clear feed" @click="clearFeed">
          <Icon name="trash" :size="14" />
        </button>
      </div>
    </header>

    <div class="stat-strip">
      <div class="stat">
        <div class="s-label">Events shown</div>
        <div class="s-val">{{ shown.length }}</div>
      </div>
      <div class="stat">
        <div class="s-label">Agent runs</div>
        <div class="s-val" :style="{ color: 'var(--violet)' }">{{ agentRuns }}</div>
      </div>
      <div class="stat">
        <div class="s-label">Tool calls</div>
        <div class="s-val" :style="{ color: 'var(--blue)' }">{{ toolCalls }}</div>
      </div>
      <div class="stat">
        <div class="s-label">Errors</div>
        <div class="s-val" :style="{ color: errors ? 'var(--red)' : 'var(--text)' }">
          {{ errors }}
        </div>
      </div>
    </div>

    <Card flush title="Event stream" icon="activity">
      <template #actions>
        <span
          class="faint"
          :style="{
            fontSize: 'var(--fs-xs)',
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
          }"
        >
          <Icon name="filter" :size="13" />
          Filter
        </span>
        <div class="chips">
          <button
            v-for="t in KIND_TYPES"
            :key="t.kind"
            class="chip"
            :class="{ off: !active.has(t.kind) }"
            @click="toggleType(t.kind)"
          >
            <span class="dot" :style="{ background: chipColorVar(t.color) }" />
            {{ t.label }}
          </button>
        </div>
        <button v-if="!allOn" class="btn btn-ghost btn-sm" @click="enableAll">All</button>
      </template>

      <EmptyState
        v-if="shown.length === 0"
        icon="filter"
        title="No matching events"
        sub="Adjust the type filters above to see more of the stream."
      />
      <div v-else class="feed">
        <div
          v-for="ev in shown"
          :key="ev.id"
          class="feed-row"
          :class="{ 'is-error': ev.kind === 'error_alert', enter: ev.id > prevMax }"
        >
          <span class="feed-time">{{ fmtTime(new Date(ev.timestampUtc)) }}</span>
          <span class="feed-type"><TypeBadge :kind="ev.kind" /></span>
          <span class="feed-summary">
            <span
              class="sum-text"
              :class="{ expandable: !!ev.detail }"
              :title="ev.detail ? 'Show parameters' : undefined"
              @click="ev.detail && toggleExpand(ev.id)"
            >
              <span v-if="ev.detail" class="caret">{{ expanded.has(ev.id) ? '▾' : '▸' }}</span>
              {{ ev.summary }}
            </span>
            <pre v-if="ev.detail && expanded.has(ev.id)" class="feed-detail">{{ prettyDetail(ev.detail) }}</pre>
          </span>
        </div>
      </div>
    </Card>
  </div>
</template>

<style scoped>
.sum-text.expandable {
  cursor: pointer;
}
.caret {
  color: var(--text-faint);
  font-size: var(--fs-xs);
  margin-right: 2px;
}
.feed-detail {
  margin: 6px 0 2px;
  padding: 8px 10px;
  background: rgba(127, 127, 127, 0.08);
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: var(--fs-xs);
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 320px;
  overflow: auto;
}
</style>
