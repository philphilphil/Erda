import { createRouter, createWebHistory } from 'vue-router'
import { useAuth } from './composables/useAuth'

import RemindersView from './views/RemindersView.vue'
import PromptView from './views/PromptView.vue'
import ActivityView from './views/ActivityView.vue'
import ConfigView from './views/ConfigView.vue'
import LoginView from './views/LoginView.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: RemindersView },
    { path: '/prompt', component: PromptView },
    { path: '/activity', component: ActivityView },
    { path: '/config', component: ConfigView },
    { path: '/login', component: LoginView },
  ],
})

router.beforeEach(async (to) => {
  const { state, ensureLoaded } = useAuth()
  await ensureLoaded()

  if (state.authRequired && !state.authenticated && to.path !== '/login') {
    return '/login'
  }

  if ((!state.authRequired || state.authenticated) && to.path === '/login') {
    return '/'
  }

  return true
})

export default router
