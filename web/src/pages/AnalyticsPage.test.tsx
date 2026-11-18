// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ApiClient } from '../api/client'
import type { AnalyticsSummary } from '../api/types'
import AnalyticsPage from './AnalyticsPage'

afterEach(cleanup)

const summary: AnalyticsSummary = {
  linkId: 'l1',
  totalClicks: 12,
  byDevice: [
    { device: 'desktop', count: 8 },
    { device: 'mobile', count: 4 },
  ],
  byCountry: [
    { country: 'ID', count: 10 },
    { country: 'US', count: 2 },
  ],
  byDay: [
    { day: '2026-11-16T00:00:00+00:00', count: 5 },
    { day: '2026-11-17T00:00:00+00:00', count: 7 },
  ],
}

function makeClient(over: Partial<{ analytics: ReturnType<typeof vi.fn> }> = {}) {
  const linksApi = {
    list: vi.fn(),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
    analytics: vi.fn(async () => summary),
    ...over,
  }
  const client = {
    auth: { login: vi.fn(), register: vi.fn(), refresh: vi.fn() },
    links: linksApi,
    isAuthenticated: vi.fn(() => true),
  } as unknown as ApiClient
  return { client, linksApi }
}

function renderPage(client: ApiClient, authenticated = true) {
  ;(client.isAuthenticated as ReturnType<typeof vi.fn>).mockReturnValue(authenticated)
  return render(
    <MemoryRouter initialEntries={['/links/l1/analytics']}>
      <Routes>
        <Route path="/links/:id/analytics" element={<AnalyticsPage client={client} />} />
        <Route path="/login" element={<div>Login Page</div>} />
        <Route path="/links" element={<div>Links Page</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('AnalyticsPage', () => {
  it('redireksi ke /login saat belum login', () => {
    const { client } = makeClient()
    renderPage(client, false)
    expect(screen.getByText('Login Page')).toBeTruthy()
  })

  it('memuat dan menampilkan ringkasan klik', async () => {
    const { client, linksApi } = makeClient()
    renderPage(client)
    expect(await screen.findByText('12')).toBeTruthy()
    expect(linksApi.analytics).toHaveBeenCalledWith('l1')
    expect(await screen.findByText('desktop')).toBeTruthy()
    expect(await screen.findByText('mobile')).toBeTruthy()
    expect(await screen.findByText('ID')).toBeTruthy()
    expect(await screen.findByText('US')).toBeTruthy()
  })

  it('menampilkan bar chart harian dengan lebar proporsional', async () => {
    const { client } = makeClient()
    renderPage(client)
    const bars = await screen.findAllByTestId(/day-bar/)
    expect(bars).toHaveLength(2)
    // max count = 7; hari pertama 5 -> 5/7 ~71%
    expect(bars[0].getAttribute('style')).toContain('71%')
    expect(bars[1].getAttribute('style')).toContain('100%')
  })

  it('menampilkan pesan error saat gagal memuat', async () => {
    const { client } = makeClient({
      analytics: vi.fn(async () => {
        throw new Error('Network error')
      }),
    })
    renderPage(client)
    expect(await screen.findByText('Gagal memuat analytics.')).toBeTruthy()
  })

  it('menyediakan tautan kembali ke daftar tautan', async () => {
    const { client } = makeClient()
    renderPage(client)
    expect(await screen.findByRole('link', { name: /kembali/i })).toBeTruthy()
  })
})
