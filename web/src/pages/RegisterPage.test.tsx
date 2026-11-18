// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ApiError } from '../api/client'
import type { ApiClient } from '../api/client'
import RegisterPage from './RegisterPage'

afterEach(cleanup)

function renderRegister(client: ApiClient) {
  return render(
    <MemoryRouter initialEntries={['/register']}>
      <Routes>
        <Route path="/register" element={<RegisterPage client={client} />} />
        <Route path="/login" element={<div data-testid="login-page">Login</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function makeClient() {
  const register = vi.fn()
  const client = { auth: { register, login: vi.fn(), refresh: vi.fn() } } as unknown as ApiClient
  return { client, register }
}

describe('RegisterPage', () => {
  it('menampilkan field username, email, dan password', () => {
    const { client } = makeClient()
    renderRegister(client)
    expect(screen.getByLabelText('Username')).toBeTruthy()
    expect(screen.getByLabelText('Email')).toBeTruthy()
    expect(screen.getByLabelText('Password')).toBeTruthy()
    expect(screen.getByRole('button', { name: /daftar/i })).toBeTruthy()
  })

  it('submit register memanggil api dan navigasi ke /login', async () => {
    const { client, register } = makeClient()
    register.mockResolvedValue({ id: 'u1', username: 'alice', email: 'a@x.io', role: 'User' })
    renderRegister(client)

    await userEvent.type(screen.getByLabelText('Username'), 'alice')
    await userEvent.type(screen.getByLabelText('Email'), 'a@x.io')
    await userEvent.type(screen.getByLabelText('Password'), 'pw12345678')
    await userEvent.click(screen.getByRole('button', { name: /daftar/i }))

    expect(register).toHaveBeenCalledWith({ username: 'alice', email: 'a@x.io', password: 'pw12345678' })
    expect(await screen.findByTestId('login-page')).toBeTruthy()
  })

  it('menampilkan pesan error dari server saat register gagal', async () => {
    const { client, register } = makeClient()
    register.mockRejectedValue(new ApiError('Username sudah dipakai.', 409))
    renderRegister(client)

    await userEvent.type(screen.getByLabelText('Username'), 'alice')
    await userEvent.type(screen.getByLabelText('Email'), 'a@x.io')
    await userEvent.type(screen.getByLabelText('Password'), 'pw12345678')
    await userEvent.click(screen.getByRole('button', { name: /daftar/i }))

    expect(await screen.findByText('Username sudah dipakai.')).toBeTruthy()
  })
})
