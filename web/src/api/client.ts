import type {
  AuthStatus,
  LoginBody,
  RemindersResponse,
  ReminderDto,
  CreateReminderBody,
  UpdateReminderBody,
  PromptResponse,
  PromptVersionDto,
  SavePromptBody,
  ActivityDto,
  ConfigItemDto,
  PutConfigBody,
  VoicePromptResponse,
  SaveVoicePromptBody,
  StatusResponse,
  SystemSchedulesResponse,
  WorkflowsResponse,
  RunWorkflowResponse,
  ChatSession,
  McpCapabilitiesResponse,
} from './types'

// ── Error type ────────────────────────────────────────────────────────────────

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

// ── Unauthorized hook ─────────────────────────────────────────────────────────

type UnauthorizedCallback = () => void
let _onUnauthorized: UnauthorizedCallback | null = null

export function onUnauthorized(cb: UnauthorizedCallback): void {
  _onUnauthorized = cb
}

// ── Core fetch helpers ────────────────────────────────────────────────────────

const MUTATING_METHODS = new Set(['POST', 'PUT', 'DELETE', 'PATCH'])

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const isMutating = MUTATING_METHODS.has(method.toUpperCase())

  const headers: Record<string, string> = {}
  if (isMutating) {
    headers['X-Requested-With'] = 'erda-panel'
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json'
    }
  }

  const res = await fetch(path, {
    method,
    credentials: 'include',
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })

  if (res.status === 401) {
    _onUnauthorized?.()
    throw new ApiError(401, 'Unauthorized')
  }

  if (!res.ok) {
    let message = res.statusText
    try {
      const data = (await res.json()) as { error?: string }
      if (data.error) message = data.error
    } catch {
      // ignore parse errors
    }
    throw new ApiError(res.status, message)
  }

  // Handle 204 / empty body
  const text = await res.text()
  if (!text) return undefined as T
  return JSON.parse(text) as T
}

function get<T>(path: string): Promise<T> {
  return request<T>('GET', path)
}

function post<T>(path: string, body?: unknown): Promise<T> {
  return request<T>('POST', path, body)
}

function put<T>(path: string, body?: unknown): Promise<T> {
  return request<T>('PUT', path, body)
}

function del<T>(path: string): Promise<T> {
  return request<T>('DELETE', path)
}

// ── Auth ──────────────────────────────────────────────────────────────────────

export function getAuthMe(): Promise<AuthStatus> {
  return get<AuthStatus>('/api/auth/me')
}

export function login(body: LoginBody): Promise<void> {
  return post<void>('/api/auth/login', body)
}

export function logout(): Promise<void> {
  return post<void>('/api/auth/logout')
}

// ── Reminders ─────────────────────────────────────────────────────────────────

export function getReminders(): Promise<RemindersResponse> {
  return get<RemindersResponse>('/api/reminders')
}

export function createReminder(body: CreateReminderBody): Promise<ReminderDto> {
  return post<ReminderDto>('/api/reminders', body)
}

export function updateReminder(id: string, body: UpdateReminderBody): Promise<ReminderDto> {
  return put<ReminderDto>(`/api/reminders/${id}`, body)
}

export function pauseReminder(id: string): Promise<void> {
  return post<void>(`/api/reminders/${id}/pause`)
}

export function resumeReminder(id: string): Promise<void> {
  return post<void>(`/api/reminders/${id}/resume`)
}

export function deleteReminder(id: string): Promise<void> {
  return del<void>(`/api/reminders/${id}`)
}

// ── Workflows ─────────────────────────────────────────────────────────────────

export function getWorkflows(): Promise<WorkflowsResponse> {
  return get<WorkflowsResponse>('/api/workflows')
}

export function runWorkflow(id: string, input: string): Promise<RunWorkflowResponse> {
  return post<RunWorkflowResponse>(`/api/workflows/${id}/run`, { input })
}

// ── System schedules ────────────────────────────────────────────────────────

export function getSystemSchedules(): Promise<SystemSchedulesResponse> {
  return get<SystemSchedulesResponse>('/api/system-schedules')
}

// ── Prompt ────────────────────────────────────────────────────────────────────

export function getPrompt(): Promise<PromptResponse> {
  return get<PromptResponse>('/api/prompt')
}

export function savePrompt(body: SavePromptBody): Promise<PromptVersionDto> {
  return post<PromptVersionDto>('/api/prompt', body)
}

export function activateVersion(id: number): Promise<void> {
  return post<void>(`/api/prompt/versions/${id}/activate`)
}

export function getVoicePrompt(): Promise<VoicePromptResponse> {
  return get<VoicePromptResponse>('/api/prompt/voice')
}

export function saveVoicePrompt(body: SaveVoicePromptBody): Promise<void> {
  return put<void>('/api/prompt/voice', body)
}

// ── Status ────────────────────────────────────────────────────────────────────

export function getStatus(): Promise<StatusResponse> {
  return get<StatusResponse>('/api/status')
}

// ── Activity ──────────────────────────────────────────────────────────────────

export function getActivity(max = 100): Promise<ActivityDto[]> {
  return get<ActivityDto[]>(`/api/activity?max=${max}`)
}

// ── Config ────────────────────────────────────────────────────────────────────

export function getConfig(): Promise<ConfigItemDto[]> {
  return get<ConfigItemDto[]>('/api/config')
}

export function putConfig(body: PutConfigBody): Promise<void> {
  return put<void>('/api/config', body)
}

export function restart(): Promise<void> {
  return post<void>('/api/config/restart')
}

// ── Capabilities ─────────────────────────────────────────────────────────────

export function getMcpCapabilities(): Promise<McpCapabilitiesResponse> {
  return get<McpCapabilitiesResponse>('/api/capabilities/mcp')
}

// ── Chat ──────────────────────────────────────────────────────────────────────

export async function streamChat(
  text: string,
  onDelta: (s: string) => void,
  onDone: (sessionId: string | null) => void,
  onError: (msg: string) => void,
): Promise<void> {
  const res = await fetch('/api/chat', {
    method: 'POST',
    credentials: 'include',
    headers: {
      'X-Requested-With': 'erda-panel',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ text }),
  })

  if (res.status === 401) {
    _onUnauthorized?.()
    throw new ApiError(401, 'Unauthorized')
  }

  if (!res.ok) {
    let message = res.statusText
    try {
      const data = (await res.json()) as { error?: string }
      if (data.error) message = data.error
    } catch {
      // ignore parse errors
    }
    throw new ApiError(res.status, message)
  }

  if (!res.body) {
    onError('No response body')
    return
  }

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let terminated = false

  function processFrame(frame: string): boolean {
    // Find the data: line within the frame
    const dataLine = frame.split('\n').find((l) => l.startsWith('data:'))
    if (!dataLine) return false
    const raw = dataLine.slice('data:'.length).trim()
    try {
      const parsed = JSON.parse(raw) as {
        delta?: string
        done?: boolean
        error?: string
        sessionId?: string | null
      }
      if (parsed.error !== undefined) {
        onError(parsed.error)
        return true
      }
      if (parsed.done) {
        onDone(parsed.sessionId ?? null)
        return true
      }
      if (parsed.delta !== undefined) {
        onDelta(parsed.delta)
      }
    } catch {
      // ignore malformed frames
    }
    return false
  }

  while (true) {
    const { done, value } = await reader.read()
    if (done) {
      // Flush any remaining bytes the decoder held in streaming mode.
      buffer += decoder.decode()
      break
    }

    buffer += decoder.decode(value, { stream: true })

    // SSE frames are separated by \n\n
    const frames = buffer.split('\n\n')
    buffer = frames.pop() ?? ''

    for (const frame of frames) {
      if (processFrame(frame)) {
        terminated = true
        return
      }
    }
  }

  // Process any leftover buffered frame after the connection closes.
  if (!terminated && buffer.trim()) {
    if (processFrame(buffer)) {
      terminated = true
    }
  }

  if (!terminated) {
    onError('Connection closed unexpectedly.')
  }
}

export function resetChat(): Promise<void> {
  return post<void>('/api/chat/reset')
}

export function getChatSession(): Promise<ChatSession> {
  return get<ChatSession>('/api/chat/session')
}
