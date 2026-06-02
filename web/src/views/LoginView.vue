<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAuth } from '../composables/useAuth'
import { ApiError } from '../api/client'
import BrandMark from '../components/BrandMark.vue'
import Banner from '../components/Banner.vue'

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
  <div class="login-card">
    <section class="card">
      <div class="card-body">
        <div class="login-brand">
          <div class="sb-mark"><BrandMark /></div>
          <div class="sb-wordmark">
            <div class="name">Erda</div>
            <div class="sub mono faint">agent control</div>
          </div>
        </div>

        <div class="login-heading">
          <div class="login-title">Sign in</div>
          <div class="login-sub faint">Control panel access</div>
        </div>

        <form class="login-form" @submit.prevent="handleSubmit">
          <div class="field">
            <label for="username">Username</label>
            <input
              id="username"
              v-model="username"
              class="input"
              type="text"
              autocomplete="username"
            />
            <div class="hint">optional</div>
          </div>

          <div class="field">
            <label for="password">Password</label>
            <input
              id="password"
              v-model="password"
              class="input"
              type="password"
              autocomplete="current-password"
            />
          </div>

          <Banner v-if="errorMsg" tone="warn" icon="alert">{{ errorMsg }}</Banner>

          <button
            type="submit"
            class="btn btn-primary"
            style="width: 100%"
            :disabled="submitting"
          >
            Log in
          </button>
        </form>
      </div>
    </section>
  </div>
</template>

<style scoped>
.login-card {
  width: min(380px, 92vw);
}

.login-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding-bottom: 18px;
  border-bottom: 1px solid var(--border);
  margin-bottom: 18px;
}
.login-brand .sb-wordmark .name {
  font-weight: 600;
  letter-spacing: -0.01em;
  font-size: var(--fs-md);
  line-height: 1.1;
}
.login-brand .sb-wordmark .sub {
  font-size: 10px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.login-heading {
  margin-bottom: 18px;
}
.login-title {
  font-size: var(--fs-lg);
  font-weight: 600;
  letter-spacing: -0.01em;
}
.login-sub {
  font-size: var(--fs-sm);
  margin-top: 2px;
}

.login-form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.login-form .banner {
  margin-bottom: 0;
}
</style>
