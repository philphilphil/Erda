// Auth
export interface AuthStatus {
  authRequired: boolean
  authenticated: boolean
}

export interface LoginBody {
  username?: string
  password: string
}

// Reminders
export type ReminderKind = 'Reminder' | 'Prompt'
export type ReminderStatus = 'Active' | 'Paused' | 'Done'

export interface ReminderDto {
  id: string
  kind: ReminderKind
  when: string
  text: string
  status: ReminderStatus
  nextFire: string
  // Scheduled prompts only: run straight through Codex (web search on) instead of the agent.
  directToCodex: boolean
  // Scheduled prompts only: optional pre-run shell command; its stdout is injected into the prompt.
  preScript: string | null
}

export interface RemindersResponse {
  reminders: ReminderDto[]
  scheduledPrompts: ReminderDto[]
  malformedCount: number
}

export interface CreateReminderBody {
  kind: ReminderKind
  when: string
  text: string
  // Applied only when kind is 'Prompt'.
  directToCodex?: boolean
  preScript?: string | null
}

export interface UpdateReminderBody {
  when: string
  text: string
  directToCodex?: boolean
  preScript?: string | null
}

// Prompt
export interface PromptVersionDto {
  id: number
  createdAtUtc: string
  isActive: boolean
  note: string | null
}

export interface PromptResponse {
  activeContent: string
  versions: PromptVersionDto[]
}

export interface SavePromptBody {
  content: string
  note?: string | null
}

// Voice-memo prompt
export interface VoicePromptResponse {
  content: string
}

export interface SaveVoicePromptBody {
  content: string
}

// Agent status
export interface StatusResponse {
  online: boolean
  startedAtUtc: string
}

// Activity
export interface ActivityDto {
  id: number
  timestampUtc: string
  kind: string
  summary: string
}

// Config
export interface ConfigItemDto {
  key: string
  label: string
  hint: string
  group: string
  value: string | null
  effective: string | null
  overridden: boolean
}

export interface PutConfigBody {
  values: Record<string, string | null>
}

// Workflows (read-only; MAF pipelines, reflected as a node/edge graph)
export interface WorkflowNode {
  id: string
  type: string
  inputs: string[]
  outputs: string[]
  isStart: boolean
}

export interface WorkflowEdge {
  from: string
  to: string
}

export interface WorkflowGraph {
  id: string
  title: string
  description: string
  tags: string[]
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  runnable: boolean
  inputLabel: string
}

export interface WorkflowsResponse {
  workflows: WorkflowGraph[]
}

export interface RunWorkflowResponse {
  output: string
}

// System schedules (read-only background jobs)
export interface SystemScheduleDto {
  key: string
  name: string
  icon: string
  description: string
  enabled: boolean
  status: string
  tags: string[]
}

export interface SystemSchedulesResponse {
  schedules: SystemScheduleDto[]
}

// MCP capabilities
export interface McpToolDto {
  name: string
  description: string | null
}

export interface McpServerDto {
  name: string
  transport: string
  connected: boolean
  tools: McpToolDto[]
}

export interface McpCapabilitiesResponse {
  servers: McpServerDto[]
}

export interface AccountDto {
  title: string
  sites: string[]
}
export interface AccountsResponse {
  accounts: AccountDto[]
}

// Chat
export interface ChatMessage {
  role: 'user' | 'assistant'
  text: string
  error?: boolean
}

export interface ChatSession {
  // Id of the live agent session, or null when the agent has no current session
  // (fresh start or after a restart).
  sessionId: string | null
}
