import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, createApiClient } from './client'
import type { AuthResponse, LinkResponse } from './types'

const baseUrl = '/shortlink/api'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('api client', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('login posts credentials to /auth/login and stores tokens', async () => {
    const auth: AuthResponse = {
      accessToken: 'at',
      refreshToken: 'rt',
      expiresAt: '2026-01-01T00:00:00Z',
      userId: 'u1',
      username: 'alice',
      email: 'a@x.io',
      role: 'User',
    }
    fetchMock.mockResolvedValue(jsonResponse(auth))
    const setTokens = vi.fn()
    const client = createApiClient({
      storage: { getAccessToken: () => null, setTokens, clear: vi.fn() },
    })

    const result = await client.auth.login({ identifier: 'alice', password: 'pw12345678' })

    expect(result.accessToken).toBe('at')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe(`${baseUrl}/auth/login`)
    expect(init.method).toBe('POST')
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
    expect(JSON.parse(init.body as string)).toEqual({ identifier: 'alice', password: 'pw12345678' })
    expect(setTokens).toHaveBeenCalledWith({ accessToken: 'at', refreshToken: 'rt' })
  })

  it('attaches Bearer token when a token is present', async () => {
    fetchMock.mockResolvedValue(jsonResponse([]))
    const client = createApiClient({
      storage: { getAccessToken: () => 'token123', setTokens: vi.fn(), clear: vi.fn() },
    })

    await client.links.list()

    const [, init] = fetchMock.mock.calls[0]
    expect((init.headers as Record<string, string>)['Authorization']).toBe('Bearer token123')
  })

  it('throws ApiError with the server message on non-2xx', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ message: 'Kredensial salah.' }, 401))
    const client = createApiClient()

    const err = await client.auth.login({ identifier: 'x', password: 'y' }).catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err).toMatchObject({ message: 'Kredensial salah.', status: 401 })
  })

  it('links.create posts payload to /api/links', async () => {
    const link: LinkResponse = {
      id: 'l1', slug: 'abc', url: 'https://x.io', ownerId: 'u1',
      createdAt: '2026-01-01T00:00:00Z', expiresAt: null, clickCount: 0,
    }
    fetchMock.mockResolvedValue(jsonResponse(link))
    const client = createApiClient({
      storage: { getAccessToken: () => 't', setTokens: vi.fn(), clear: vi.fn() },
    })

    const r = await client.links.create({ url: 'https://x.io' })

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe(`${baseUrl}/api/links`)
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ url: 'https://x.io' })
    expect(r.slug).toBe('abc')
  })

  it('links.create sends trimmed customSlug when provided', async () => {
    const link: LinkResponse = {
      id: 'l1', slug: 'promo2026', url: 'https://x.io', ownerId: 'u1',
      createdAt: '2026-01-01T00:00:00Z', expiresAt: null, clickCount: 0,
    }
    fetchMock.mockResolvedValue(jsonResponse(link))
    const client = createApiClient({
      storage: { getAccessToken: () => 't', setTokens: vi.fn(), clear: vi.fn() },
    })

    const r = await client.links.create({ url: 'https://x.io', customSlug: '  promo2026  ' })

    const [, init] = fetchMock.mock.calls[0]
    expect(JSON.parse(init.body as string)).toEqual({ url: 'https://x.io', customSlug: 'promo2026' })
    expect(r.slug).toBe('promo2026')
  })

  it('links.create omits customSlug when blank or whitespace', async () => {
    const link: LinkResponse = {
      id: 'l1', slug: 'abc', url: 'https://x.io', ownerId: 'u1',
      createdAt: '2026-01-01T00:00:00Z', expiresAt: null, clickCount: 0,
    }
    fetchMock.mockResolvedValue(jsonResponse(link))
    const client = createApiClient({
      storage: { getAccessToken: () => 't', setTokens: vi.fn(), clear: vi.fn() },
    })

    await client.links.create({ url: 'https://x.io', customSlug: '   ' })

    const [, init] = fetchMock.mock.calls[0]
    expect(JSON.parse(init.body as string)).toEqual({ url: 'https://x.io' })
  })

  it('delete sends DELETE and treats 204 as success', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }))
    const client = createApiClient({
      storage: { getAccessToken: () => null, setTokens: vi.fn(), clear: vi.fn() },
    })

    await expect(client.links.remove('l1')).resolves.toBeUndefined()

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe(`${baseUrl}/api/links/l1`)
    expect(init.method).toBe('DELETE')
  })

  it('links.analyticsCsv fetches /api/links/:id/analytics/csv with Bearer token and Accept text/csv', async () => {
    const csvPayload = 'section,label,clicks\nsummary,total,5\n'
    fetchMock.mockResolvedValue(
      new Response(csvPayload, {
        status: 200,
        headers: { 'Content-Type': 'text/csv; charset=utf-8' },
      }),
    )
    const client = createApiClient({
      storage: { getAccessToken: () => 'token123', setTokens: vi.fn(), clear: vi.fn() },
    })

    const csv = await client.links.analyticsCsv('l1')

    expect(csv).toBe(csvPayload)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe(`${baseUrl}/api/links/l1/analytics/csv`)
    expect((init.headers as Record<string, string>)['Authorization']).toBe('Bearer token123')
    expect((init.headers as Record<string, string>)['Accept']).toBe('text/csv')
  })

  it('links.analyticsCsv throws ApiError using server error field on non-2xx', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'Terlalu banyak permintaan.' }, 429))
    const client = createApiClient({
      storage: { getAccessToken: () => 'token123', setTokens: vi.fn(), clear: vi.fn() },
    })

    const err = await client.links.analyticsCsv('l1').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err).toMatchObject({ message: 'Terlalu banyak permintaan.', status: 429 })
  })
})

describe('isAuthenticated', () => {
  it('returns false when no access token is stored', () => {
    const client = createApiClient({
      storage: { getAccessToken: () => null, setTokens: vi.fn(), clear: vi.fn() },
    })
    expect(client.isAuthenticated()).toBe(false)
  })

  it('returns true when an access token is stored', () => {
    const client = createApiClient({
      storage: { getAccessToken: () => 'token', setTokens: vi.fn(), clear: vi.fn() },
    })
    expect(client.isAuthenticated()).toBe(true)
  })
})
