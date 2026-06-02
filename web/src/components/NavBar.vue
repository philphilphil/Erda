<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useAuth } from '../composables/useAuth'

const router = useRouter()
const { state, logout } = useAuth()

async function handleLogout() {
  await logout()
  router.push('/login')
}
</script>

<template>
  <nav>
    <RouterLink to="/">Reminders</RouterLink>
    <RouterLink to="/prompt">System Prompt</RouterLink>
    <RouterLink to="/activity">Activity</RouterLink>
    <RouterLink to="/config">Config</RouterLink>
    <button v-if="state.authRequired && state.authenticated" @click="handleLogout">
      Log out
    </button>
  </nav>
</template>
