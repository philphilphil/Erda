<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from './Icon.vue'
import BrandMark from './BrandMark.vue'
import { useAuth } from '../composables/useAuth'

// Mobile-only navigation chrome: a slim top bar (brand + status + theme/logout),
// a fixed bottom tab bar (4 primary routes + More), and a bottom sheet holding the
// overflow routes. Hidden on desktop via CSS, where Sidebar takes over.
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

const route = useRoute()
const router = useRouter()
const { state, logout } = useAuth()

// Routes that don't get a primary tab live behind the More sheet.
const overflow = [
  { to: '/voice-memos', icon: 'mic', label: 'Voice memos' },
  { to: '/workflows', icon: 'workflow', label: 'Workflows' },
  { to: '/config', icon: 'sliders', label: 'Config' },
  { to: '/capabilities', icon: 'zap', label: 'Capabilities' },
]
const moreActive = computed(() => overflow.some((o) => route.path.startsWith(o.to)))

const moreOpen = ref(false)
// Tapping a sheet link navigates; closing on every route change dismisses the sheet.
watch(
  () => route.path,
  () => {
    moreOpen.value = false
  },
)

// Ticking clock so the uptime label recomputes on its own (mirrors Sidebar).
const now = ref(Date.now())
let tick: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  tick = setInterval(() => (now.value = Date.now()), 30_000)
})
onUnmounted(() => {
  if (tick) clearInterval(tick)
})

const uptime = computed(() => {
  if (!props.startedAtUtc) return ''
  const started = new Date(props.startedAtUtc).getTime()
  if (Number.isNaN(started)) return ''
  const s = Math.max(0, Math.floor((now.value - started) / 1000))
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
  <!-- top bar -->
  <header class="m-topbar">
    <div class="m-brand">
      <span class="m-mark"><BrandMark /></span>
      <span class="m-name">Erda</span>
    </div>
    <div class="m-actions">
      <span class="m-status" :title="online ? 'Agent online' : 'Agent offline'">
        <span class="pulse-dot" :style="online ? undefined : { background: 'var(--text-faint)' }" />
      </span>
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
  </header>

  <!-- bottom tab bar -->
  <nav class="m-tabbar" aria-label="Primary">
    <RouterLink to="/" class="m-tab" active-class="active" exact-active-class="active">
      <span class="m-tab-ico">
        <Icon name="clock" :size="20" />
        <span v-if="activeReminderCount" class="m-tab-badge">{{ activeReminderCount }}</span>
      </span>
      <span class="m-tab-label">Schedules</span>
    </RouterLink>
    <RouterLink to="/prompt" class="m-tab" active-class="active">
      <span class="m-tab-ico"><Icon name="code" :size="20" /></span>
      <span class="m-tab-label">Prompts</span>
    </RouterLink>
    <RouterLink to="/chat" class="m-tab" active-class="active">
      <span class="m-tab-ico"><Icon name="chat" :size="20" /></span>
      <span class="m-tab-label">Chat</span>
    </RouterLink>
    <RouterLink to="/activity" class="m-tab" active-class="active">
      <span class="m-tab-ico"><Icon name="activity" :size="20" /></span>
      <span class="m-tab-label">Activity</span>
    </RouterLink>
    <button
      class="m-tab"
      type="button"
      :class="{ active: moreActive || moreOpen }"
      :aria-expanded="moreOpen"
      @click="moreOpen = !moreOpen"
    >
      <span class="m-tab-ico"><Icon name="more" :size="20" /></span>
      <span class="m-tab-label">More</span>
    </button>
  </nav>

  <!-- More sheet -->
  <div class="m-sheet-backdrop" :class="{ open: moreOpen }" @click="moreOpen = false" />
  <div class="m-sheet" :class="{ open: moreOpen }" role="dialog" aria-label="More">
    <div class="m-sheet-grab" />
    <div class="m-sheet-label">More</div>
    <nav class="m-sheet-nav">
      <RouterLink
        v-for="o in overflow"
        :key="o.to"
        :to="o.to"
        class="m-sheet-item"
        active-class="active"
      >
        <Icon :name="o.icon" :size="18" />
        <span>{{ o.label }}</span>
      </RouterLink>
    </nav>
    <div class="m-sheet-foot">
      <span class="pulse-dot" :style="online ? undefined : { background: 'var(--text-faint)' }" />
      <span class="m-sheet-status">{{ online ? `Agent online · ${uptime}` : 'Agent offline' }}</span>
    </div>
  </div>
</template>
