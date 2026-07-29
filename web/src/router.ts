import { createRouter, createWebHistory } from 'vue-router'
import { useAuth } from './composables/useAuth'

import RemindersView from './views/RemindersView.vue'
import PromptView from './views/PromptView.vue'
import ActivityView from './views/ActivityView.vue'
import ChatView from './views/ChatView.vue'
import ConfigView from './views/ConfigView.vue'
import CapabilitiesView from './views/CapabilitiesView.vue'
import WorkflowsView from './views/WorkflowsView.vue'
import VoiceMemosView from './views/VoiceMemosView.vue'
import LoginView from './views/LoginView.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: RemindersView },
    { path: '/prompt', component: PromptView },
    { path: '/chat', component: ChatView },
    { path: '/activity', component: ActivityView },
    { path: '/voice-memos', component: VoiceMemosView },
    { path: '/workflows', component: WorkflowsView },
    { path: '/config', component: ConfigView },
    { path: '/capabilities', component: CapabilitiesView },
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
