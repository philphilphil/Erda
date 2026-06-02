import { ref, watch } from 'vue'
import { getChatSession } from '../api/client'
import type { ChatMessage } from '../api/types'

// Module-level state so the conversation survives switching between sidebar sections
// (the ChatView component unmounts on navigation) and a full page refresh (localStorage).
//
// Persisting the *display* is not the same as the agent remembering: the server keeps its
// conversation in an in-memory session that is lost on restart. We mint a session id per
// server session and store it alongside the messages so that, on load, we can tell whether
// the persisted history still belongs to a session the agent actually remembers — and warn
// the user (via `stale`) when it doesn't.

const STORAGE_KEY = 'erda.chat.v1'

interface Persisted {
  messages: ChatMessage[]
  sessionId: string | null
}

function load(): Persisted {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<Persisted>
      if (Array.isArray(parsed.messages)) {
        return { messages: parsed.messages, sessionId: parsed.sessionId ?? null }
      }
    }
  } catch {
    // corrupt or unavailable storage — start clean
  }
  return { messages: [], sessionId: null }
}

const initial = load()
const messages = ref<ChatMessage[]>(initial.messages)
const sessionId = ref<string | null>(initial.sessionId)
// True when the persisted history predates the agent's current (or absent) session — the agent
// no longer remembers the messages shown. Cleared the moment the user starts a new turn.
const stale = ref(false)

let reconciled = false

// Persist on change, debounced so a long streaming reply doesn't write on every token.
let persistTimer: ReturnType<typeof setTimeout> | null = null
watch(
  [messages, sessionId],
  () => {
    if (persistTimer) clearTimeout(persistTimer)
    persistTimer = setTimeout(() => {
      try {
        localStorage.setItem(
          STORAGE_KEY,
          JSON.stringify({ messages: messages.value, sessionId: sessionId.value }),
        )
      } catch {
        // quota / availability — nothing we can do, drop it
      }
    }, 150)
  },
  { deep: true },
)

// One-time liveness check: compare our stored session id with the server's live one. If the
// server has no session (null) or a different one, the agent has forgotten our history.
async function reconcile(): Promise<void> {
  if (reconciled) return
  reconciled = true
  if (messages.value.length === 0) return
  try {
    const { sessionId: serverId } = await getChatSession()
    if (serverId === null || serverId !== sessionId.value) {
      stale.value = true
    }
  } catch {
    // network/auth issues: leave the history as-is, no banner
  }
}

function clear(): void {
  messages.value = []
  sessionId.value = null
  stale.value = false
}

export function useChat() {
  return { messages, sessionId, stale, reconcile, clear }
}
