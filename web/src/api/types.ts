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
