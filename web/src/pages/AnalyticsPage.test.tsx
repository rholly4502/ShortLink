// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ApiError } from '../api/client'
import type { ApiClient } from '../api/client'
import type { AnalyticsSummary } from '../api/types'
import AnalyticsPage from './AnalyticsPage'

afterEach(() => {
  vi.restoreAllMocks()
  cleanup()
})

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

function makeClient(
  over: Partial<{ analytics: ReturnType<typeof vi.fn>; analyticsCsv: ReturnType<typeof vi.fn> }> = {},
) {
  const linksApi = {
    list: vi.fn(),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
    analytics: vi.fn(async () => summary),
    analyticsCsv: vi.fn(async () => 'section,label,clicks\nsummary,total,12\n'),
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

  it('mengekspor CSV dan memicu unduhan blob saat tombol Export CSV diklik', async () => {
    const { client, linksApi } = makeClient()
    const origCreateObjectUrl = URL.createObjectURL
    const origRevokeObjectUrl = URL.revokeObjectURL
    const createdBlobs: Blob[] = []
    const createObjectUrlMock = vi.fn((obj: Blob | MediaSource) => {
      createdBlobs.push(obj as Blob)
      return 'blob:analytics-l1'
    })
    const revokeObjectUrlMock = vi.fn()
    URL.createObjectURL = createObjectUrlMock
    URL.revokeObjectURL = revokeObjectUrlMock

    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})

    try {
      renderPage(client)
      const exportBtn = await screen.findByRole('button', { name: /export csv/i })
      await userEvent.click(exportBtn)

      expect(linksApi.analyticsCsv).toHaveBeenCalledWith('l1')
      expect(createObjectUrlMock).toHaveBeenCalledTimes(1)
      expect(createdBlobs[0]).toBeInstanceOf(Blob)
      expect(createdBlobs[0].type).toContain('text/csv')
      expect(await createdBlobs[0].text()).toBe('section,label,clicks\nsummary,total,12\n')
      expect(clickSpy).toHaveBeenCalledTimes(1)
      const anchor = clickSpy.mock.instances[0] as HTMLAnchorElement
      expect(anchor.download).toBe('analytics-l1.csv')
      expect(anchor.getAttribute('href')).toBe('blob:analytics-l1')
      expect(revokeObjectUrlMock).toHaveBeenCalledWith('blob:analytics-l1')
    } finally {
      URL.createObjectURL = origCreateObjectUrl
      URL.revokeObjectURL = origRevokeObjectUrl
    }
  })

  it('menampilkan error alert saat ekspor CSV gagal tanpa menghilangkan ringkasan analytics', async () => {
    const { client } = makeClient({
      analyticsCsv: vi.fn(async () => {
        throw new ApiError('Gagal ekspor dari server.', 500)
      }),
    })
    renderPage(client)

    const exportBtn = await screen.findByRole('button', { name: /export csv/i })
    await userEvent.click(exportBtn)

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('Gagal ekspor dari server.')
    expect(screen.getByText('12')).toBeTruthy()
  })
})
