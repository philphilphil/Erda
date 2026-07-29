<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getVoiceMemos, deleteVoiceMemo, voiceMemoAudioUrl } from '../api/client'
import type { VoiceMemoDto, VoiceMemoSource } from '../api/types'
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'

const memos = ref<VoiceMemoDto[]>([])
const loading = ref(true)
const error = ref<string | null>(null)
const deleting = ref<Set<number>>(new Set())

async function load() {
  loading.value = true
  error.value = null
  try {
    memos.value = await getVoiceMemos()
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load voice memos.'
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(m: VoiceMemoDto) {
  if (!confirm(`Delete "${m.fileName}" and its audio? The Obsidian note is kept.`)) return
  const next = new Set(deleting.value)
  next.add(m.id)
  deleting.value = next
  try {
    await deleteVoiceMemo(m.id)
    memos.value = memos.value.filter((x) => x.id !== m.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to delete.'
  } finally {
    const done = new Set(deleting.value)
    done.delete(m.id)
    deleting.value = done
  }
}

// ── formatting helpers ──────────────────────────────────────────────────────────
function fmtDate(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function fmtBytes(n: number): string {
  if (!n) return '—'
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`
  return `${(n / (1024 * 1024)).toFixed(1)} MB`
}

// Vault path → the note's display name (basename without the .md extension).
function noteName(path: string | null): string | null {
  if (!path) return null
  const base = path.split('/').pop() ?? path
  return base.replace(/\.md$/i, '')
}

const STATUS_COLOR: Record<string, string> = {
  filed: 'var(--green)',
  raw: 'var(--amber)',
  failed: 'var(--red)',
  answered: 'var(--blue)',
  pending: 'var(--text-faint)',
}
function statusColor(s: string): string {
  return STATUS_COLOR[s] ?? 'var(--text-faint)'
}

// Source badge: where the audio came from. Reuses the global .badge chip + colour classes.
const SOURCE_LABEL: Record<VoiceMemoSource, string> = {
  upload: 'Upload',
  'apple-memo': 'Apple memo',
  'whatsapp-voice': 'WhatsApp',
}
const SOURCE_CLASS: Record<VoiceMemoSource, string> = {
  upload: 'b-blue',
  'apple-memo': 'b-violet',
  'whatsapp-voice': 'b-cyan',
}
function sourceLabel(s: VoiceMemoSource): string {
  return SOURCE_LABEL[s] ?? s
}
function sourceClass(s: VoiceMemoSource): string {
  return SOURCE_CLASS[s] ?? 'b-muted'
}

// One-line preview of an answered turn's transcript (the full text goes in the title attribute).
function transcriptPreview(t: string | null): string {
  const flat = (t ?? '').replace(/\s+/g, ' ').trim()
  return flat || '—'
}

const count = computed(() => memos.value.length)
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Voice memos</div>
        <div class="h-sub">
          Archive of every voice message Erda receives: API uploads (iOS Shortcut), Apple Voice Memos
          shared through WhatsApp, and WhatsApp voice notes. Play the original audio or delete the entry
          — the Obsidian note it produced is always kept.
        </div>
      </div>
      <div class="h-actions">
        <button class="btn btn-ghost btn-icon" title="Refresh" @click="load">
          <Icon name="rotate" :size="14" />
        </button>
      </div>
    </header>

    <div v-if="error" class="banner-error">{{ error }}</div>

    <Card flush title="Archived memos" icon="mic" :sub="count ? `${count}` : undefined">
      <div v-if="loading" class="vm-note faint">Loading…</div>

      <EmptyState
        v-else-if="count === 0"
        icon="mic"
        title="No voice memos yet"
        sub="Voice notes sent over WhatsApp and memos uploaded through the /upload endpoint (iOS Shortcut) will appear here."
      />

      <div v-else class="vm-scroll">
        <table class="vm-table">
          <thead>
            <tr>
              <th class="col-date">Date</th>
              <th class="col-file">File</th>
              <th class="col-source">Source</th>
              <th class="col-note">Result</th>
              <th class="col-play">Audio</th>
              <th class="col-del"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="m in memos" :key="m.id">
              <td class="col-date mono">{{ fmtDate(m.createdAtUtc) }}</td>
              <td class="col-file">
                <span class="vm-file">{{ m.fileName }}</span>
                <span class="vm-size faint">{{ fmtBytes(m.audioBytes) }}</span>
              </td>
              <td class="col-source">
                <span class="badge sq" :class="sourceClass(m.source)">
                  <span class="dot" />{{ sourceLabel(m.source) }}
                </span>
              </td>
              <td class="col-note">
                <!-- filed/raw memos link the note they produced; an answered agent turn has only its
                     transcript (no note is written), so preview that instead. -->
                <span v-if="noteName(m.notePath)" class="vm-noteline">
                  <Icon name="note" :size="13" />
                  <span class="vm-notename" :title="m.notePath ?? undefined">{{ noteName(m.notePath) }}</span>
                </span>
                <span
                  v-else-if="m.status === 'answered'"
                  class="vm-transcript"
                  :title="m.transcript ?? 'answered — no note'"
                >{{ transcriptPreview(m.transcript) }}</span>
                <span
                  v-else
                  class="vm-status"
                  :style="{ color: statusColor(m.status) }"
                  :title="`status: ${m.status}`"
                >{{ m.status }}</span>
              </td>
              <td class="col-play">
                <audio
                  v-if="m.hasAudio"
                  class="vm-audio"
                  controls
                  preload="none"
                  :src="voiceMemoAudioUrl(m.id)"
                />
                <span v-else class="faint">no audio</span>
              </td>
              <td class="col-del">
                <button
                  class="btn btn-ghost btn-icon"
                  title="Delete entry + audio (keeps the note)"
                  :disabled="deleting.has(m.id)"
                  @click="remove(m)"
                >
                  <Icon name="trash" :size="14" />
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </Card>
  </div>
</template>

<style scoped>
.vm-scroll {
  overflow-x: auto;
}
.vm-table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--fs-sm);
}
.vm-table th {
  text-align: left;
  font-weight: 500;
  color: var(--text-faint);
  font-size: var(--fs-xs);
  text-transform: uppercase;
  letter-spacing: 0.04em;
  padding: 10px 14px;
  border-bottom: 1px solid var(--border);
  white-space: nowrap;
}
.vm-table td {
  padding: 10px 14px;
  border-bottom: 1px solid var(--border);
  vertical-align: middle;
}
.vm-table tr:last-child td {
  border-bottom: none;
}
.col-date {
  white-space: nowrap;
  color: var(--text-muted, var(--text));
}
.vm-file {
  display: block;
  word-break: break-word;
}
.vm-size {
  font-size: var(--fs-xs);
}
.vm-noteline {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text);
}
.vm-notename {
  word-break: break-word;
}
.col-source {
  white-space: nowrap;
}
/* one-line transcript preview; the full text is in the title attribute */
.vm-transcript {
  display: block;
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-muted);
}
.vm-status {
  font-size: var(--fs-xs);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.vm-audio {
  height: 34px;
  max-width: 240px;
  vertical-align: middle;
}
.faint {
  color: var(--text-faint);
}
.banner-error {
  padding: 10px 14px;
  margin-bottom: 14px;
  border: 1px solid var(--red);
  border-radius: 8px;
  color: var(--red);
  font-size: var(--fs-sm);
}
.vm-note {
  padding: 14px;
}
</style>
