<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAuth } from '../composables/useAuth'
import { ApiError } from '../api/client'

const router = useRouter()
const { state, login } = useAuth()

const username = ref('')
const password = ref('')
const errorMsg = ref<string | null>(null)
const submitting = ref(false)

onMounted(() => {
  if (!state.authRequired) {
    router.replace('/')
  }
})

async function handleSubmit() {
  errorMsg.value = null
  submitting.value = true
  try {
    await login(username.value, password.value)
    router.push('/')
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) {
      errorMsg.value = 'Incorrect credentials.'
    } else {
      errorMsg.value = 'Login failed.'
    }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <h1>Log in</h1>
  <form @submit.prevent="handleSubmit">
    <div>
      <label for="username">Username (optional)</label><br />
      <input id="username" v-model="username" type="text" autocomplete="username" />
    </div>
    <div>
      <label for="password">Password</label><br />
      <input id="password" v-model="password" type="password" autocomplete="current-password" />
    </div>
    <button type="submit" :disabled="submitting">Log in</button>
    <p v-if="errorMsg" style="color: red;">{{ errorMsg }}</p>
  </form>
</template>
