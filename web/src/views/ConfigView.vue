<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getConfig, restart } from '../api/client'
import type { ConfigItemDto } from '../api/types'
import Card from '../components/Card.vue'
import Banner from '../components/Banner.vue'
import Icon from '../components/Icon.vue'

const items = ref<ConfigItemDto[]>([])
const restarting = ref(false)

async function load() {
  items.value = await getConfig()
}

onMounted(load)

// Group items by item.group, preserving first-seen order.
const groups = computed<{ name: string; items: ConfigItemDto[] }[]>(() => {
  const order: string[] = []
  const byGroup = new Map<string, ConfigItemDto[]>()
  for (const item of items.value) {
    let bucket = byGroup.get(item.group)
    if (!bucket) {
      bucket = []
      byGroup.set(item.group, bucket)
      order.push(item.group)
    }
    bucket.push(item)
  }
  return order.map((name) => ({ name, items: byGroup.get(name)! }))
})

function groupIcon(name: string): string {
  switch (name) {
    case 'Model & reasoning':
      return 'cpu'
    case 'Error watch':
      return 'bell'
    case 'Reminders':
      return 'clock'
    case 'WhatsApp':
      return 'message'
    default:
      return 'sliders'
  }
}

async function handleRestart() {
  restarting.value = true
  try {
    await restart()
  } catch {
    // Server exits, so the request may error — that's expected.
  }
}
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Config</div>
        <div class="h-sub">
          The effective settings this process booted with. Read-only.
        </div>
      </div>
      <div class="h-actions">
        <button class="btn btn-danger" :disabled="restarting" @click="handleRestart">
          <Icon name="power" :size="14" />
          Restart agent
        </button>
      </div>
    </header>

    <Banner tone="info" icon="info" strong="Env-only config.">
      Configuration comes entirely from environment variables (<code>.env</code>) and is validated at
      startup. To change a setting, edit <code>.env</code> and restart the agent.
    </Banner>

    <div class="grid-2" style="align-items: start">
      <Card
        v-for="group in groups"
        :key="group.name"
        :title="group.name"
        :icon="groupIcon(group.name)"
      >
        <dl class="config-list">
          <div v-for="item in group.items" :key="item.label" class="config-row">
            <dt>{{ item.label }}</dt>
            <dd class="mono">{{ item.value }}</dd>
          </div>
        </dl>
      </Card>
    </div>

    <p v-if="restarting" class="hint" style="margin-top: 12px">Restarting…</p>
  </div>
</template>

<style scoped>
.config-list {
  margin: 0;
}
.config-row {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 12px;
  padding: 6px 0;
  border-bottom: 1px solid var(--border, rgba(255, 255, 255, 0.06));
}
.config-row:last-child {
  border-bottom: none;
}
.config-row dt {
  color: var(--text-muted, #9aa);
}
.config-row dd {
  margin: 0;
  text-align: right;
  word-break: break-all;
}

/* phones: stack the value under its label so long paths/values aren't crushed
   into a narrow right-aligned column that breaks mid-word */
@media (max-width: 768px) {
  .config-row {
    flex-direction: column;
    gap: 2px;
    align-items: stretch;
  }
  .config-row dd {
    text-align: left;
    word-break: break-word;
  }
}
</style>
