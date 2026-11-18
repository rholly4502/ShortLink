// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ApiClient } from '../api/client'
import type { LinkResponse } from '../api/types'
import LinksPage from './LinksPage'

afterEach(cleanup)

function makeLink(over: Partial<LinkResponse>): LinkResponse {
  return {
    id: 'l1', slug: 'abc', url: 'https://example.io', ownerId: 'u1',
    createdAt: '2026-11-01T00:00:00Z', expiresAt: null, clickCount: 0, ...over,
  }
}

/** Fake client with an in-memory array backing list/create/update/remove. */
function makeClient(initial: LinkResponse[] = []) {
  const links = [...initial]
  const auth = { login: vi.fn(), register: vi.fn(), refresh: vi.fn() }
  const linksApi = {
    list: vi.fn(async () => [...links]),
    get: vi.fn(),
    create: vi.fn(async (input: { url: string; expiresAt?: string | null }) => {
      const link = makeLink({
        id: `l${links.length + 1}`,
        slug: `s${links.length + 1}`,
        url: input.url,
        expiresAt: input.expiresAt ?? null,
      })
      links.unshift(link)
      return link
    }),
    update: vi.fn(async (id: string, input: { url: string; expiresAt?: string | null }) => {
      const i = links.findIndex((l) => l.id === id)
      links[i] = { ...links[i], url: input.url, expiresAt: input.expiresAt ?? null }
      return links[i]
    }),
    remove: vi.fn(async (id: string) => {
      const i = links.findIndex((l) => l.id === id)
      if (i >= 0) links.splice(i, 1)
    }),
    analytics: vi.fn(),
  }
  const client = { auth, links: linksApi, isAuthenticated: vi.fn(() => true) } as unknown as ApiClient
  return { client, links, linksApi, auth }
}

function renderPage(client: ApiClient, authenticated = true) {
  ;(client.isAuthenticated as ReturnType<typeof vi.fn>).mockReturnValue(authenticated)
  return render(
    <MemoryRouter initialEntries={['/links']}>
      <Routes>
        <Route path="/links" element={<LinksPage client={client} />} />
        <Route path="/login" element={<div>Login Page</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('LinksPage', () => {
  it('redireksi ke /login saat belum login', () => {
    const { client } = makeClient()
    renderPage(client, false)
    expect(screen.getByText('Login Page')).toBeTruthy()
  })

  it('memuat dan menampilkan daftar tautan', async () => {
    const { client } = makeClient([makeLink({ id: 'l1', slug: 'abc', url: 'https://a.io', clickCount: 7 })])
    renderPage(client)
    expect(await screen.findByText('https://a.io')).toBeTruthy()
    expect(await screen.findByText('abc')).toBeTruthy()
    expect(await screen.findByText(/7 klik/)).toBeTruthy()
  })

  it('membuat tautan baru lewat form dan memperbarui daftar', async () => {
    const { client, linksApi } = makeClient()
    renderPage(client)

    await userEvent.type(screen.getByLabelText('URL Baru'), 'https://baru.io')
    await userEvent.click(screen.getByRole('button', { name: /buat/i }))

    expect(linksApi.create).toHaveBeenCalledWith({ url: 'https://baru.io', expiresAt: null })
    expect(await screen.findByText('https://baru.io')).toBeTruthy()
  })

  it('menghapus tautan setelah konfirmasi', async () => {
    const { client, linksApi } = makeClient([makeLink({ id: 'l1', slug: 'abc' })])
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderPage(client)

    await screen.findByText('https://example.io')
    await userEvent.click(screen.getByRole('button', { name: /hapus/i }))

    expect(linksApi.remove).toHaveBeenCalledWith('l1')
    expect(screen.queryByText('https://example.io')).toBeNull()
  })

  it('memperbarui URL melalui mode edit', async () => {
    const { client, linksApi } = makeClient([makeLink({ id: 'l1', slug: 'abc', url: 'https://lama.io' })])
    renderPage(client)

    await screen.findByText('https://lama.io')
    await userEvent.click(screen.getByRole('button', { name: /edit/i }))
    const urlInput = screen.getByLabelText('URL')
    await userEvent.clear(urlInput)
    await userEvent.type(urlInput, 'https://baru.io')
    await userEvent.click(screen.getByRole('button', { name: /simpan/i }))

    expect(linksApi.update).toHaveBeenCalledWith('l1', { url: 'https://baru.io', expiresAt: null })
    expect(await screen.findByText('https://baru.io')).toBeTruthy()
  })
})
