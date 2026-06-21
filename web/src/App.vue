<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import Sidebar from './components/Sidebar.vue'
import MobileNav from './components/MobileNav.vue'
import { getStatus, getReminders } from './api/client'

const route = useRoute()

// ── Theme (persisted) ──────────────────────────────────────────────────────
type Theme = 'dark' | 'light'
const stored = localStorage.getItem('erda.theme')
const theme = ref<Theme>(stored === 'light' ? 'light' : 'dark')

watch(
  theme,
  (t) => {
    document.documentElement.dataset.theme = t
    localStorage.setItem('erda.theme', t)
  },
  { immediate: true },
)

function toggleTheme() {
  theme.value = theme.value === 'dark' ? 'light' : 'dark'
}

// ── Agent status + nav badge ───────────────────────────────────────────────
const online = ref(false)
const startedAtUtc = ref<string | null>(null)
const activeReminderCount = ref<number | undefined>(undefined)

async function loadStatus() {
  try {
    const status = await getStatus()
    online.value = status.online
    startedAtUtc.value = status.startedAtUtc
  } catch {
    online.value = false
  }
}

async function loadReminderCount() {
  try {
    const res = await getReminders()
    const active = (r: { status: string }) => r.status === 'Active'
    activeReminderCount.value =
      res.reminders.filter(active).length + res.scheduledPrompts.filter(active).length
  } catch {
    // decorative; ignore failures
  }
}

// Re-poll status so a backend restart shows up (online flips, uptime resets) without a manual reload.
let statusTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  if (route.path !== '/login') {
    loadStatus()
    loadReminderCount()
  }
  statusTimer = setInterval(() => {
    if (route.path !== '/login') loadStatus()
  }, 30_000)
})
onUnmounted(() => {
  if (statusTimer) clearInterval(statusTimer)
})
</script>

<template>
  <main v-if="route.path === '/login'" class="login-main">
    <RouterView />
  </main>
  <div v-else class="app">
    <Sidebar
      :online="online"
      :started-at-utc="startedAtUtc"
      :active-reminder-count="activeReminderCount"
      :theme="theme"
      @toggle-theme="toggleTheme"
    />
    <MobileNav
      :online="online"
      :started-at-utc="startedAtUtc"
      :active-reminder-count="activeReminderCount"
      :theme="theme"
      @toggle-theme="toggleTheme"
    />
    <main class="main">
      <RouterView />
    </main>
  </div>
</template>
