import type { AnalyticsSummary, AuthResponse, LinkResponse, UserResponse } from './types'

/** Error thrown for any non-2xx API response. */
export class ApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/** Persists auth tokens (defaults to localStorage). */
export interface TokenStorage {
  getAccessToken(): string | null
  setTokens(tokens: { accessToken: string; refreshToken: string }): void
  clear(): void
}

const ACCESS_KEY = 'sl_access_token'
const REFRESH_KEY = 'sl_refresh_token'

function localStorageStorage(store: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>): TokenStorage {
  return {
    getAccessToken: () => store.getItem(ACCESS_KEY),
    setTokens: ({ accessToken, refreshToken }) => {
      store.setItem(ACCESS_KEY, accessToken)
      store.setItem(REFRESH_KEY, refreshToken)
    },
    clear: () => {
      store.removeItem(ACCESS_KEY)
      store.removeItem(REFRESH_KEY)
    },
  }
}

/** Fallback in-memory storage used when localStorage is unavailable (tests, SSR). */
function memoryStorage(): TokenStorage {
  const mem = new Map<string, string>()
  return {
    getAccessToken: () => mem.get(ACCESS_KEY) ?? null,
    setTokens: ({ accessToken, refreshToken }) => {
      mem.set(ACCESS_KEY, accessToken)
      mem.set(REFRESH_KEY, refreshToken)
    },
    clear: () => {
      mem.delete(ACCESS_KEY)
      mem.delete(REFRESH_KEY)
    },
  }
}

export interface ApiClient {
  auth: {
    register(payload: { username: string; email: string; password: string }): Promise<UserResponse>
    login(payload: { identifier: string; password: string }): Promise<AuthResponse>
    refresh(refreshToken: string): Promise<AuthResponse>
  }
  links: {
    list(): Promise<LinkResponse[]>
    create(input: { url: string; expiresAt?: string | null }): Promise<LinkResponse>
    get(id: string): Promise<LinkResponse>
    update(id: string, input: { url: string; expiresAt?: string | null }): Promise<LinkResponse>
    remove(id: string): Promise<void>
    analytics(id: string): Promise<AnalyticsSummary>
  }
}

export interface CreateApiClientOptions {
  /** Base URL of the API (default: '/shortlink/api'). */
  baseUrl?: string
  storage?: TokenStorage
}

export function createApiClient(options: CreateApiClientOptions = {}): ApiClient {
  const baseUrl = options.baseUrl ?? '/shortlink/api'
  const storage =
    options.storage ?? (typeof globalThis !== 'undefined' && globalThis.localStorage
      ? localStorageStorage(globalThis.localStorage)
      : memoryStorage())

  async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers: Record<string, string> = {
      ...((init.headers as Record<string, string> | undefined) ?? {}),
    }
    if (init.body !== undefined && !headers['Content-Type']) {
      headers['Content-Type'] = 'application/json'
    }
    const token = storage.getAccessToken()
    if (token) headers['Authorization'] = `Bearer ${token}`

    const res = await fetch(`${baseUrl}${path}`, { ...init, headers })

    if (!res.ok) {
      let message = `Request failed (${res.status})`
      try {
        const payload = (await res.json()) as { message?: string }
        if (payload?.message) message = payload.message
      } catch {
        // non-JSON error body
      }
      throw new ApiError(message, res.status)
    }

    if (res.status === 204) return undefined as T
    return (await res.json()) as T
  }

  function json(method: string, body?: unknown): RequestInit {
    return {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    }
  }

  return {
    auth: {
      register: (payload) => request('/auth/register', json('POST', payload)),
      login: async (payload) => {
        const res = await request<AuthResponse>('/auth/login', json('POST', payload))
        storage.setTokens({ accessToken: res.accessToken, refreshToken: res.refreshToken })
        return res
      },
      refresh: async (refreshToken) => {
        const res = await request<AuthResponse>('/auth/refresh', json('POST', { refreshToken }))
        storage.setTokens({ accessToken: res.accessToken, refreshToken: res.refreshToken })
        return res
      },
    },
    links: {
      list: () => request('/api/links'),
      create: (input) =>
        request('/api/links', json('POST', { url: input.url, expiresAt: input.expiresAt ?? undefined })),
      get: (id) => request(`/api/links/${id}`),
      update: (id, input) =>
        request(`/api/links/${id}`, json('PUT', { url: input.url, expiresAt: input.expiresAt ?? undefined })),
      remove: (id) => request(`/api/links/${id}`, { method: 'DELETE' }),
      analytics: (id) => request(`/api/links/${id}/analytics`),
    },
  }
}
