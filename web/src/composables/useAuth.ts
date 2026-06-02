import { reactive } from 'vue'
import { getAuthMe, login as apiLogin, logout as apiLogout, onUnauthorized } from '../api/client'

interface AuthState {
  authRequired: boolean
  authenticated: boolean
  loaded: boolean
}

const state = reactive<AuthState>({
  authRequired: false,
  authenticated: false,
  loaded: false,
})

let loadPromise: Promise<void> | null = null

async function loadOnce(): Promise<void> {
  const me = await getAuthMe()
  state.authRequired = me.authRequired
  state.authenticated = me.authenticated
  state.loaded = true
}

function ensureLoaded(): Promise<void> {
  if (!loadPromise) {
    loadPromise = loadOnce().catch((err) => {
      // Reset so next call retries
      loadPromise = null
      throw err
    })
  }
  return loadPromise
}

function markUnauthenticated(): void {
  state.authenticated = false
}

// Register the 401 hook so any request in any view triggers the redirect logic
onUnauthorized(() => {
  markUnauthenticated()
})

async function login(username: string, password: string): Promise<void> {
  await apiLogin({ username: username || undefined, password })
  // Refresh me after login
  const me = await getAuthMe()
  state.authRequired = me.authRequired
  state.authenticated = me.authenticated
}

async function logout(): Promise<void> {
  await apiLogout()
  state.authenticated = false
}

export function useAuth() {
  return { state, ensureLoaded, login, logout }
}
