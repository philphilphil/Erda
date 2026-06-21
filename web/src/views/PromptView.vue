<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  getPrompt,
  savePrompt,
  activateVersion,
  getVoicePrompt,
  saveVoicePrompt,
} from '../api/client'
import type { PromptVersionDto } from '../api/types'
import Card from '../components/Card.vue'
import Banner from '../components/Banner.vue'
import Icon from '../components/Icon.vue'

// ── date formatting (ported from design data.jsx) ──────────────────────────────
const pad = (n: number): string => String(n).padStart(2, '0')
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
function fmtDateTime(iso: string): string {
  const d = new Date(iso)
  return `${MONTHS[d.getMonth()]} ${pad(d.getDate())}, ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

// ── state ───────────────────────────────────────────────────────────────────
type Tab = 'system' | 'voice'
const tab = ref<Tab>('system')

const systemActive = ref('')
const voiceActive = ref('')
const versions = ref<PromptVersionDto[]>([])

const sysDraft = ref('')
const voiceDraft = ref('')
const note = ref('')

const sysDirty = computed(() => sysDraft.value !== systemActive.value)
const voiceDirty = computed(() => voiceDraft.value !== voiceActive.value)
const isSystem = computed(() => tab.value === 'system')
const dirty = computed(() => (isSystem.value ? sysDirty.value : voiceDirty.value))

// active draft for counts / editor meta
const activeDraft = computed(() => (isSystem.value ? sysDraft.value : voiceDraft.value))
const chars = computed(() => activeDraft.value.length)
const tokens = computed(() => Math.max(1, Math.round(activeDraft.value.length / 4)))
const lines = computed(() => activeDraft.value.split('\n').length)

// ── loading ───────────────────────────────────────────────────────────────────
async function loadSystem() {
  const data = await getPrompt()
  systemActive.value = data.activeContent
  versions.value = data.versions
  sysDraft.value = data.activeContent
}

async function loadVoice() {
  const data = await getVoicePrompt()
  voiceActive.value = data.content
  voiceDraft.value = data.content
}

onMounted(() => {
  loadSystem()
  loadVoice()
})

// ── actions ─────────────────────────────────────────────────────────────────
async function saveSystem() {
  if (!sysDirty.value) return
  await savePrompt({ content: sysDraft.value, note: note.value.trim() || null })
  note.value = ''
  await loadSystem()
}

async function saveVoice() {
  if (!voiceDirty.value) return
  await saveVoicePrompt({ content: voiceDraft.value })
  await loadVoice()
}

function save() {
  return isSystem.value ? saveSystem() : saveVoice()
}

function revert() {
  if (isSystem.value) sysDraft.value = systemActive.value
  else voiceDraft.value = voiceActive.value
}

async function restore(id: number) {
  await activateVersion(id)
  await loadSystem()
}
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Prompts</div>
        <div class="h-sub">
          {{
            isSystem
              ? 'The instructions Erda runs under. Saving creates a new version; you can roll back at any time.'
              : 'How Erda handles voice memos dropped into the inbox — transcription, filing, and follow-up. Saved in place, no version history.'
          }}
        </div>
      </div>
      <div class="h-actions">
        <button class="btn btn-ghost" :disabled="!dirty" @click="revert">
          <Icon name="rotate" :size="14" />
          Revert
        </button>
        <button class="btn btn-primary" :disabled="!dirty" @click="save">
          <Icon name="save" :size="14" />
          {{ isSystem ? 'Save version' : 'Save' }}
        </button>
      </div>
    </header>

    <div class="row" style="margin-bottom: var(--gap)">
      <div class="seg">
        <button
          :class="{ on: isSystem }"
          style="display: inline-flex; align-items: center; gap: 7px; padding: 0 13px; height: 28px"
          @click="tab = 'system'"
        >
          <Icon name="code" :size="13" />
          System prompt
        </button>
        <button
          :class="{ on: !isSystem }"
          style="display: inline-flex; align-items: center; gap: 7px; padding: 0 13px; height: 28px"
          @click="tab = 'voice'"
        >
          <Icon name="mic" :size="13" />
          Voice-memo prompt
        </button>
      </div>
      <span v-if="!isSystem" class="badge b-muted">no history</span>
    </div>

    <Banner v-if="dirty" tone="warn" icon="alert" strong="Unsaved changes.">
      {{
        isSystem
          ? "These instructions take effect on the agent's next run after you save."
          : 'The voice-memo prompt applies to the next memo processed after you save.'
      }}
    </Banner>

    <!-- SYSTEM TAB -->
    <div v-if="isSystem" class="prompt-cols">
      <Card title="erda.system.md" icon="code" :sub="`${lines} lines`" flush>
        <template #actions>
          <span class="faint mono" style="font-size: var(--fs-xs)">{{ chars.toLocaleString() }} chars</span>
          <span class="mono muted" style="font-size: var(--fs-xs)">~{{ tokens.toLocaleString() }} tok</span>
        </template>

        <textarea
          v-model="sysDraft"
          class="textarea mono"
          spellcheck="false"
          style="
            border: none;
            border-radius: 0;
            background: transparent;
            min-height: 460px;
            font-size: var(--fs-sm);
            line-height: 1.65;
            padding: 16px var(--pad-card);
            resize: vertical;
            display: block;
            width: 100%;
          "
        ></textarea>

        <div
          style="
            border-top: 1px solid var(--border);
            padding: 10px var(--pad-card);
            display: flex;
            gap: 10px;
            align-items: center;
          "
        >
          <input
            v-model="note"
            class="input"
            placeholder="Version note (what changed?)"
            style="flex: 1"
          />
          <button class="btn btn-primary" :disabled="!sysDirty" @click="saveSystem">
            <Icon name="save" :size="14" />
            Save version
          </button>
        </div>
      </Card>

      <Card title="Version history" icon="rotate" :sub="`${versions.length}`" flush>
        <div style="max-height: 560px; overflow-y: auto">
          <div
            v-for="v in versions"
            :key="v.id"
            style="
              display: flex;
              gap: 10px;
              padding: 10px 14px;
              border-bottom: 1px solid var(--border);
              align-items: flex-start;
            "
          >
            <div style="display: flex; flex-direction: column; align-items: center; padding-top: 2px">
              <span
                class="mono"
                :style="{
                  fontSize: '10px',
                  color: v.isActive ? 'var(--green)' : 'var(--text-faint)',
                }"
              >
                v{{ v.id }}
              </span>
            </div>
            <div style="min-width: 0; flex: 1">
              <div style="font-size: var(--fs-sm); color: var(--text); margin-bottom: 2px">
                {{ v.note || '—' }}
              </div>
              <div class="faint mono" style="font-size: var(--fs-xs)">
                {{ fmtDateTime(v.createdAtUtc) }}
              </div>
            </div>
            <span v-if="v.isActive" class="badge b-green">current</span>
            <button
              v-else
              class="btn btn-ghost btn-sm"
              title="Restore this version"
              @click="restore(v.id)"
            >
              <Icon name="rotate" :size="13" />
              Restore
            </button>
          </div>
        </div>
      </Card>
    </div>

    <!-- VOICE TAB -->
    <Card v-else title="voice-memo.md" icon="mic" :sub="`${lines} lines`" flush>
      <template #actions>
        <span class="faint mono" style="font-size: var(--fs-xs)">{{ chars.toLocaleString() }} chars</span>
        <span class="mono muted" style="font-size: var(--fs-xs)">~{{ tokens.toLocaleString() }} tok</span>
      </template>

      <textarea
        v-model="voiceDraft"
        class="textarea mono"
        spellcheck="false"
        style="
          border: none;
          border-radius: 0;
          background: transparent;
          min-height: 460px;
          font-size: var(--fs-sm);
          line-height: 1.65;
          padding: 16px var(--pad-card);
          resize: vertical;
          display: block;
          width: 100%;
        "
      ></textarea>

      <div
        style="
          border-top: 1px solid var(--border);
          padding: 10px var(--pad-card);
          display: flex;
          gap: 10px;
          align-items: center;
        "
      >
        <span class="faint" style="flex: 1; font-size: var(--fs-xs)">
          Saved in place — this prompt has no version history.
        </span>
        <button class="btn btn-primary" :disabled="!voiceDirty" @click="saveVoice">
          <Icon name="save" :size="14" />
          Save
        </button>
      </div>
    </Card>
  </div>
</template>

<style scoped>
/* editor + version history side by side on desktop, stacked on phones */
.prompt-cols {
  display: grid;
  grid-template-columns: 1fr 320px;
  gap: var(--gap);
  align-items: start;
}
@media (max-width: 768px) {
  .prompt-cols {
    grid-template-columns: 1fr;
  }
}
</style>
