<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import Icon from './Icon.vue'
import BrandMark from './BrandMark.vue'
import { useAuth } from '../composables/useAuth'

const props = withDefaults(
  defineProps<{
    online: boolean
    startedAtUtc?: string | null
    activeReminderCount?: number
    theme: 'dark' | 'light'
  }>(),
  {
    startedAtUtc: null,
  },
)

const emit = defineEmits<{ (e: 'toggle-theme'): void }>()

const router = useRouter()
const { state, logout } = useAuth()

// Format uptime as "<Xd Yh Zm>" from a start timestamp to now.
const uptime = computed(() => {
  if (!props.startedAtUtc) return ''
  const started = new Date(props.startedAtUtc).getTime()
  if (Number.isNaN(started)) return ''
  const s = Math.max(0, Math.floor((Date.now() - started) / 1000))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const mn = Math.floor((s % 3600) / 60)
  return `${d}d ${h}h ${mn}m`
})

const showLogout = computed(() => state.authRequired && state.authenticated)

async function handleLogout() {
  await logout()
  router.push('/login')
}
</script>

<template>
  <aside class="sidebar">
    <div class="sb-brand">
      <div class="sb-mark"><BrandMark /></div>
      <div class="sb-wordmark">
        <div class="name">Erda</div>
        <div class="sub">agent control</div>
      </div>
    </div>

    <div class="sb-section-label">Operate</div>
    <nav class="nav">
      <RouterLink to="/" class="nav-item" active-class="active" exact-active-class="active">
        <Icon name="clock" />
        <span class="label">Schedules</span>
        <span v-if="activeReminderCount != null" class="badge-count">{{ activeReminderCount }}</span>
      </RouterLink>
      <RouterLink to="/prompt" class="nav-item" active-class="active">
        <Icon name="code" />
        <span class="label">Prompts</span>
      </RouterLink>
      <RouterLink to="/chat" class="nav-item" active-class="active">
        <Icon name="chat" />
        <span class="label">Chat</span>
      </RouterLink>
      <RouterLink to="/activity" class="nav-item" active-class="active">
        <Icon name="activity" />
        <span class="label">Activity</span>
      </RouterLink>
      <RouterLink to="/config" class="nav-item" active-class="active">
        <Icon name="sliders" />
        <span class="label">Config</span>
      </RouterLink>
      <RouterLink to="/capabilities" class="nav-item" active-class="active">
        <Icon name="zap" />
        <span class="label">Capabilities</span>
      </RouterLink>
    </nav>

    <div class="sb-spacer" />

    <div class="sb-foot">
      <span class="pulse-dot" :style="online ? undefined : { background: 'var(--text-faint)' }" />
      <div class="meta">
        <div class="s1">{{ online ? 'Agent online' : 'Agent offline' }}</div>
        <div class="s2">{{ online ? `uptime ${uptime}` : 'stopped' }}</div>
      </div>
      <button
        v-if="showLogout"
        class="theme-toggle"
        title="Log out"
        aria-label="Log out"
        @click="handleLogout"
      >
        <Icon name="power" :size="15" />
      </button>
      <button
        class="theme-toggle"
        :title="theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'"
        aria-label="Toggle theme"
        @click="emit('toggle-theme')"
      >
        <Icon :name="theme === 'dark' ? 'sun' : 'moon'" :size="15" />
      </button>
    </div>
  </aside>
</template>
