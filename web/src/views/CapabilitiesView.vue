<script setup lang="ts">
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'

interface Capability {
  icon: string
  title: string
  desc: string
  tags: string[]
}

// Tools & workflows Erda runs when you ask it to.
const onRequest: Capability[] = [
  {
    icon: 'note',
    title: 'Vault notes',
    desc: 'Reads, searches and writes notes in your Obsidian vault.',
    tags: ['read · write · search', 'vault-confined'],
  },
  {
    icon: 'globe',
    title: 'Deep reasoning',
    desc: 'Hands hard questions to a stronger model with live web search.',
    tags: ['gpt-5.5', 'web search'],
  },
  {
    icon: 'clock',
    title: 'Reminders & prompts',
    desc: 'Schedules one-off or recurring reminders, plus prompts that run live and reply.',
    tags: ['cron / one-off', 'Europe/Berlin'],
  },
  {
    icon: 'mic',
    title: 'Voice memos',
    desc: 'Turns an Apple Voice Memo into a clean, structured note.',
    tags: ['gpt-4o-transcribe → gpt-5.5'],
  },
]

// Things Erda does in the background, without being prompted.
const automatic: Capability[] = [
  {
    icon: 'alert',
    title: 'Error watch',
    desc: 'Watches Seq for errors and pings you on WhatsApp with a diagnosis.',
    tags: ['Seq', 'every 15 min'],
  },
  {
    icon: 'chat',
    title: 'WhatsApp',
    desc: 'Talk to Erda by text, voice or image — and it messages you proactively.',
    tags: ['text · voice · image'],
  },
]
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">What Erda can do</div>
        <div class="h-sub">
          A quick tour of Erda's capabilities — the things you can ask it to do, and the things it
          does on its own.
        </div>
      </div>
    </header>

    <Card flush title="Ask it to do" sub="tools you invoke">
      <div class="cap-list">
        <div v-for="cap in onRequest" :key="cap.title" class="cap-row">
          <div class="cap-name">
            <span class="ci"><Icon :name="cap.icon" /></span>
            <span>{{ cap.title }}</span>
          </div>
          <div class="cap-detail">
            <div class="cap-desc">{{ cap.desc }}</div>
            <div class="cap-tags">
              <span v-for="tag in cap.tags" :key="tag" class="badge sq b-muted">{{ tag }}</span>
            </div>
          </div>
        </div>
      </div>
    </Card>

    <Card flush title="Runs on its own" sub="background automation">
      <div class="cap-list">
        <div v-for="cap in automatic" :key="cap.title" class="cap-row">
          <div class="cap-name">
            <span class="ci"><Icon :name="cap.icon" /></span>
            <span>{{ cap.title }}</span>
          </div>
          <div class="cap-detail">
            <div class="cap-desc">{{ cap.desc }}</div>
            <div class="cap-tags">
              <span v-for="tag in cap.tags" :key="tag" class="badge sq b-muted">{{ tag }}</span>
            </div>
          </div>
        </div>
      </div>
    </Card>
  </div>
</template>

<style scoped>
.cap-list {
  display: flex;
  flex-direction: column;
}
.cap-row {
  display: grid;
  grid-template-columns: minmax(0, 200px) 1fr;
  gap: 16px;
  align-items: start;
  padding: 14px var(--pad-card);
  border-bottom: 1px solid var(--border);
  transition: background 0.1s;
}
.cap-row:last-child {
  border-bottom: none;
}
.cap-row:hover {
  background: color-mix(in oklch, var(--surface-2) 50%, transparent);
}
.cap-name {
  display: flex;
  align-items: center;
  gap: 9px;
  font-weight: 500;
  color: var(--text);
  font-size: var(--fs);
}
.cap-name .ci {
  display: grid;
  place-items: center;
  color: var(--text-faint);
  flex: 0 0 auto;
}
.cap-desc {
  color: var(--text-muted);
  font-size: var(--fs-sm);
  line-height: 1.5;
}
.cap-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

@media (max-width: 640px) {
  .cap-row {
    grid-template-columns: 1fr;
    gap: 6px;
  }
}
</style>
