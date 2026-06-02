import type {
  AuthStatus,
  LoginBody,
  RemindersResponse,
  ReminderDto,
  CreateReminderBody,
  PromptResponse,
  PromptVersionDto,
  SavePromptBody,
  ActivityDto,
  ConfigItemDto,
  PutConfigBody,
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

export function pauseReminder(id: string): Promise<void> {
  return post<void>(`/api/reminders/${id}/pause`)
}

export function resumeReminder(id: string): Promise<void> {
  return post<void>(`/api/reminders/${id}/resume`)
}

export function deleteReminder(id: string): Promise<void> {
  return del<void>(`/api/reminders/${id}`)
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
