// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ApiError } from '../api/client'
import type { ApiClient } from '../api/client'
import LoginPage from './LoginPage'

afterEach(cleanup)

function renderLogin(client: ApiClient) {
  return render(
    <MemoryRouter initialEntries={['/login']}>
      <Routes>
        <Route path="/login" element={<LoginPage client={client} />} />
        <Route path="/" element={<div data-testid="home">Home</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function makeClient() {
  const login = vi.fn()
  const client = { auth: { login, register: vi.fn(), refresh: vi.fn() } } as unknown as ApiClient
  return { client, login }
}

describe('LoginPage', () => {
  it('menampilkan field identifier dan password', () => {
    const { client } = makeClient()
    renderLogin(client)
    expect(screen.getByLabelText('Username / Email')).toBeTruthy()
    expect(screen.getByLabelText('Password')).toBeTruthy()
    expect(screen.getByRole('button', { name: /masuk/i })).toBeTruthy()
  })

  it('submit login memanggil api dan navigasi ke home saat sukses', async () => {
    const { client, login } = makeClient()
    login.mockResolvedValue({
      accessToken: 'at', refreshToken: 'rt', expiresAt: 'x',
      userId: 'u1', username: 'alice', email: 'a@x.io', role: 'User',
    })
    renderLogin(client)

    await userEvent.type(screen.getByLabelText('Username / Email'), 'alice')
    await userEvent.type(screen.getByLabelText('Password'), 'pw12345678')
    await userEvent.click(screen.getByRole('button', { name: /masuk/i }))

    expect(login).toHaveBeenCalledWith({ identifier: 'alice', password: 'pw12345678' })
    expect(await screen.findByTestId('home')).toBeTruthy()
  })

  it('menampilkan pesan error dari server saat login gagal', async () => {
    const { client, login } = makeClient()
    login.mockRejectedValue(new ApiError('Kredensial salah.', 401))
    renderLogin(client)

    await userEvent.type(screen.getByLabelText('Username / Email'), 'alice')
    await userEvent.type(screen.getByLabelText('Password'), 'salah')
    await userEvent.click(screen.getByRole('button', { name: /masuk/i }))

    expect(await screen.findByText('Kredensial salah.')).toBeTruthy()
  })
})
