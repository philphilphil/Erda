<script setup lang="ts">
import { ref, computed, nextTick, onMounted } from 'vue'
import { streamChat, resetChat } from '../api/client'
import { useChat } from '../composables/useChat'
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'

// ── state ─────────────────────────────────────────────────────────────────────
// messages + sessionId + stale live in a shared composable so the conversation survives
// switching sidebar sections (the view unmounts) and a page refresh (persisted to localStorage).
const { messages, sessionId, stale, reconcile, clear } = useChat()
const streaming = ref(false)
const inputText = ref('')
const inputEl = ref<HTMLTextAreaElement | null>(null)
const feedEl = ref<HTMLDivElement | null>(null)

// The last assistant message while streaming — before done/error we know it's the tail.
const pendingIdx = computed(() => {
  if (!streaming.value) return -1
  return messages.value.length - 1
})

// ── scroll helpers ────────────────────────────────────────────────────────────
function scrollToBottom() {
  nextTick(() => {
    if (feedEl.value) {
      feedEl.value.scrollTop = feedEl.value.scrollHeight
    }
  })
}

// ── send ──────────────────────────────────────────────────────────────────────
async function send() {
  const text = inputText.value.trim()
  if (!text || streaming.value) return

  inputText.value = ''
  // The user is starting a new turn — any "agent forgot the history above" notice no longer applies.
  stale.value = false

  // Push user message and an empty pending assistant message.
  messages.value.push({ role: 'user', text })
  messages.value.push({ role: 'assistant', text: '' })
  streaming.value = true
  scrollToBottom()

  try {
    await streamChat(
      text,
      (delta) => {
        // Append delta to the last assistant message.
        const last = messages.value[messages.value.length - 1]
        if (last && last.role === 'assistant') {
          last.text += delta
          scrollToBottom()
        }
      },
      (sid) => {
        // done — record the (possibly new) live session id so a later reload can verify the
        // persisted history still belongs to a session the agent remembers.
        if (sid) sessionId.value = sid
        streaming.value = false
        nextTick(() => inputEl.value?.focus())
      },
      (msg) => {
        // error: mark the pending bubble
        const last = messages.value[messages.value.length - 1]
        if (last && last.role === 'assistant') {
          last.text = msg || 'Something went wrong.'
          last.error = true
        }
        streaming.value = false
        nextTick(() => inputEl.value?.focus())
      },
    )
  } catch {
    // Network/auth errors surface here; the assistant bubble stays empty.
    const last = messages.value[messages.value.length - 1]
    if (last && last.role === 'assistant' && !last.text) {
      last.text = 'Failed to connect.'
      last.error = true
    }
    streaming.value = false
    nextTick(() => inputEl.value?.focus())
  }
}

function handleKeydown(e: KeyboardEvent) {
  // Enter sends; Shift+Enter inserts newline.
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault()
    send()
  }
}

// ── new chat ──────────────────────────────────────────────────────────────────
async function newChat() {
  try {
    await resetChat()
    clear()
    nextTick(() => inputEl.value?.focus())
  } catch (err) {
    // Don't clear messages if the reset failed; leave UI usable.
    console.warn('Failed to reset chat session:', err)
  }
}

onMounted(() => {
  inputEl.value?.focus()
  // One-time liveness check: does the agent still remember our persisted history?
  reconcile()
})
</script>

<template>
  <div class="page chat-page">
    <header class="page-header">
      <div>
        <div class="h-title">Chat</div>
        <div class="h-sub">Talk to the Erda agent over the browser — isolated from WhatsApp.</div>
      </div>
      <div class="h-actions">
        <button class="btn btn-ghost" :disabled="messages.length === 0 && !streaming" @click="newChat">
          <Icon name="rotate" :size="14" />
          New chat
        </button>
      </div>
    </header>

    <Card flush class="chat-card">
      <!-- agent-restarted notice: the persisted history is older than the agent's memory -->
      <div v-if="stale" class="chat-stale-banner">
        <Icon name="info" :size="15" />
        <span
          >Erda restarted and no longer remembers the messages above. Your next message starts a
          fresh conversation.</span
        >
      </div>

      <!-- message list -->
      <div ref="feedEl" class="chat-feed">
        <EmptyState
          v-if="messages.length === 0"
          icon="chat"
          title="No messages yet"
          sub="Type a message below to start a conversation."
        />
        <template v-else>
          <div
            v-for="(msg, i) in messages"
            :key="i"
            class="chat-msg"
            :class="[`chat-msg--${msg.role}`, { 'chat-msg--error': msg.error }]"
          >
            <div class="chat-bubble">
              <!-- thinking indicator: empty assistant bubble while streaming -->
              <span
                v-if="msg.role === 'assistant' && i === pendingIdx && msg.text === ''"
                class="thinking"
              >
                <span class="thinking-dot" />
                <span class="thinking-dot" />
                <span class="thinking-dot" />
              </span>
              <span v-else>{{ msg.text }}</span>
            </div>
          </div>
        </template>
      </div>

      <!-- input bar -->
      <div class="chat-input-bar">
        <textarea
          ref="inputEl"
          v-model="inputText"
          class="textarea chat-textarea"
          placeholder="Message Erda… (Enter to send, Shift+Enter for newline)"
          rows="1"
          :disabled="streaming"
          @keydown="handleKeydown"
        />
        <button
          class="btn btn-primary chat-send-btn"
          :disabled="!inputText.trim() || streaming"
          @click="send"
        >
          <Icon name="zap" :size="14" />
          Send
        </button>
      </div>
    </Card>
  </div>
</template>

<style scoped>
/* Chat page fills remaining vertical space so the feed scrolls, not the page. */
.chat-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  padding-bottom: 24px;
}

/* Push the card to fill remaining space below the header */
.chat-card {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  /* card-body needs to stretch too */
}

/* Override card-body to flex so feed + input stack vertically */
.chat-card :deep(.card-body) {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  padding: 0;
}

/* ── agent-restarted notice ───────────────────────────────────── */
.chat-stale-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 9px var(--pad-card);
  font-size: var(--fs-xs);
  color: var(--text-muted);
  background: var(--amber-bg);
  border-bottom: 1px solid var(--border);
}

.chat-stale-banner :deep(svg) {
  flex: 0 0 auto;
  color: var(--amber);
}

/* ── message feed ─────────────────────────────────────────────── */
.chat-feed {
  flex: 1;
  overflow-y: auto;
  padding: var(--pad-card);
  display: flex;
  flex-direction: column;
  gap: 12px;
}

/* ── message rows ─────────────────────────────────────────────── */
.chat-msg {
  display: flex;
}

.chat-msg--user {
  justify-content: flex-end;
}

.chat-msg--assistant {
  justify-content: flex-start;
}

.chat-bubble {
  max-width: 72%;
  padding: 9px 13px;
  border-radius: var(--r-lg);
  font-size: var(--fs-sm);
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}

.chat-msg--user .chat-bubble {
  background: var(--accent);
  color: var(--accent-fg);
  border-bottom-right-radius: var(--r-sm);
}

.chat-msg--assistant .chat-bubble {
  background: var(--surface-2);
  color: var(--text);
  border: 1px solid var(--border);
  border-bottom-left-radius: var(--r-sm);
}

.chat-msg--error .chat-bubble {
  background: var(--red-bg);
  color: var(--red);
  border-color: color-mix(in oklch, var(--red) 30%, transparent);
}

/* ── thinking indicator ───────────────────────────────────────── */
.thinking {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 0;
}

.thinking-dot {
  width: 6px;
  height: 6px;
  border-radius: 99px;
  background: var(--text-faint);
  animation: thinking-bounce 1.2s ease-in-out infinite;
}

.thinking-dot:nth-child(2) { animation-delay: .2s; }
.thinking-dot:nth-child(3) { animation-delay: .4s; }

@keyframes thinking-bounce {
  0%, 80%, 100% { opacity: .3; transform: scale(.85); }
  40%           { opacity: 1;  transform: scale(1.15); }
}

/* ── input bar ────────────────────────────────────────────────── */
.chat-input-bar {
  display: flex;
  align-items: flex-end;
  gap: 8px;
  padding: 12px var(--pad-card);
  border-top: 1px solid var(--border);
}

.chat-textarea {
  flex: 1;
  resize: none;
  min-height: var(--ctrl-h);
  max-height: 160px;
  overflow-y: auto;
  /* override .textarea height: auto to use min-height instead */
  height: auto;
}

.chat-send-btn {
  flex: 0 0 auto;
  align-self: flex-end;
}
</style>
