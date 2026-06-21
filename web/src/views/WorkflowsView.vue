<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getWorkflows, runWorkflow, ApiError } from '../api/client'
import type { WorkflowGraph, WorkflowNode } from '../api/types'
import Card from '../components/Card.vue'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'
import Banner from '../components/Banner.vue'

const workflows = ref<WorkflowGraph[]>([])
const loading = ref(true)
const error = ref<string | null>(null)

// Per-workflow run state (keyed by workflow id).
const inputs = ref<Record<string, string>>({})
const outputs = ref<Record<string, string>>({})
const running = ref<Record<string, boolean>>({})
const runErrors = ref<Record<string, string | null>>({})
const copied = ref<Record<string, boolean>>({})

// The reflected data-flow line for a node: "in → out". The entry adapter has no typed output,
// so it just reads "entry"; a terminal with no inputs would read "→ out".
function ioLine(n: WorkflowNode): string {
  const ins = n.inputs.join(', ')
  const outs = n.outputs.join(', ')
  if (outs) return `${ins || '—'} → ${outs}`
  if (n.isStart) return 'entry'
  return ins || '—'
}

async function runIt(wf: WorkflowGraph) {
  const input = (inputs.value[wf.id] ?? '').trim()
  if (!input || running.value[wf.id]) return
  running.value[wf.id] = true
  runErrors.value[wf.id] = null
  outputs.value[wf.id] = ''
  try {
    outputs.value[wf.id] = (await runWorkflow(wf.id, input)).output
  } catch (e) {
    runErrors.value[wf.id] = e instanceof ApiError ? e.message : 'Run failed.'
  } finally {
    running.value[wf.id] = false
  }
}

async function copyOut(wf: WorkflowGraph) {
  await navigator.clipboard.writeText(outputs.value[wf.id] ?? '')
  copied.value[wf.id] = true
  window.setTimeout(() => (copied.value[wf.id] = false), 1500)
}

onMounted(async () => {
  try {
    workflows.value = (await getWorkflows()).workflows
    for (const wf of workflows.value) if (wf.runnable) inputs.value[wf.id] = ''
  } catch {
    error.value = 'Could not load workflows.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="page">
    <header class="page-header">
      <div>
        <div class="h-title">Workflows</div>
        <div class="h-sub">
          Multi-step pipelines Erda runs. Auto-discovered from the agent's workflow definitions; each
          step and connection is reflected straight from the Microsoft Agent Framework graph.
        </div>
      </div>
    </header>

    <Banner v-if="error" tone="warn" icon="alert">{{ error }}</Banner>

    <Card v-for="wf in workflows" :key="wf.id" :title="wf.title" icon="workflow" :sub="wf.id">
      <p class="wf-desc">{{ wf.description }}</p>
      <div v-if="wf.tags.length" class="wf-tags">
        <span v-for="t in wf.tags" :key="t" class="badge sq b-muted">{{ t }}</span>
      </div>

      <div class="flow">
        <template v-for="(n, i) in wf.nodes" :key="n.id">
          <div class="wf-node" :class="{ start: n.isStart }" :title="`id: ${n.id}`">
            <div class="n-type">
              <span class="n-name">{{ n.type }}</span>
              <span v-if="n.isStart" class="badge sq b-blue">start</span>
            </div>
            <div class="n-io">{{ ioLine(n) }}</div>
          </div>
          <div v-if="i < wf.nodes.length - 1" class="wf-arrow">
            <Icon name="arrowright" :size="18" />
          </div>
        </template>
      </div>

      <div v-if="wf.runnable" class="wf-run">
        <div class="run-row">
          <input
            v-model="inputs[wf.id]"
            class="input"
            :placeholder="wf.inputLabel"
            :disabled="running[wf.id]"
            @keyup.enter="runIt(wf)"
          />
          <button
            class="btn btn-primary"
            :disabled="running[wf.id] || !(inputs[wf.id] ?? '').trim()"
            @click="runIt(wf)"
          >
            <Icon name="play" :size="14" />
            {{ running[wf.id] ? 'Running…' : 'Run' }}
          </button>
        </div>

        <Banner v-if="runErrors[wf.id]" tone="warn" icon="alert" style="margin-top: 10px">
          {{ runErrors[wf.id] }}
        </Banner>

        <div v-if="outputs[wf.id]" class="run-out">
          <div class="run-out-head">
            <span class="faint mono" style="font-size: var(--fs-xs)">result.md</span>
            <button class="btn btn-ghost btn-sm" @click="copyOut(wf)">
              <Icon :name="copied[wf.id] ? 'check' : 'note'" :size="13" />
              {{ copied[wf.id] ? 'Copied' : 'Copy' }}
            </button>
          </div>
          <pre class="run-md">{{ outputs[wf.id] }}</pre>
        </div>
      </div>
    </Card>

    <Card v-if="!loading && !workflows.length && !error">
      <EmptyState icon="workflow" title="No workflows" sub="None are defined in the app yet." />
    </Card>
  </div>
</template>

<style scoped>
.wf-desc {
  color: var(--text-muted);
  font-size: var(--fs-sm);
  line-height: 1.5;
  margin: 0 0 10px;
}
.wf-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 16px;
}
.flow {
  display: flex;
  align-items: stretch;
  overflow-x: auto;
  padding: 4px 2px 10px;
}
.wf-node {
  flex: 0 0 auto;
  min-width: 150px;
  max-width: 240px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 11px 13px;
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: var(--r-md);
}
.wf-node.start {
  border-color: color-mix(in oklch, var(--blue) 45%, var(--border));
}
.n-type {
  display: flex;
  align-items: center;
  gap: 7px;
}
.n-name {
  font-weight: 600;
  font-size: var(--fs-sm);
  color: var(--text);
}
.n-io {
  font-family: var(--mono);
  font-size: var(--fs-xs);
  color: var(--text-muted);
  white-space: nowrap;
}
.wf-arrow {
  display: flex;
  align-items: center;
  padding: 0 8px;
  color: var(--text-faint);
  flex: 0 0 auto;
}
.wf-run {
  margin-top: 16px;
  border-top: 1px solid var(--border);
  padding-top: 14px;
}
.run-row {
  display: flex;
  gap: 8px;
}
.run-row .input {
  flex: 1;
}
.run-out {
  margin-top: 12px;
  border: 1px solid var(--border);
  border-radius: var(--r-md);
  overflow: hidden;
}
.run-out-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 7px 12px;
  border-bottom: 1px solid var(--border);
  background: var(--surface-2);
}
.run-md {
  margin: 0;
  padding: 14px;
  font-family: var(--mono);
  font-size: var(--fs-sm);
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 480px;
  overflow: auto;
  color: var(--text);
}

/* phones: a left-to-right node chain doesn't fit, so stack the pipeline
   vertically with the connector arrows pointing down */
@media (max-width: 768px) {
  .flow {
    flex-direction: column;
    align-items: stretch;
    overflow-x: visible;
  }
  .wf-node {
    max-width: none;
    width: 100%;
  }
  .wf-arrow {
    justify-content: center;
    padding: 4px 0;
    transform: rotate(90deg);
  }
}
</style>
