import { useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError, createApiClient } from '../api/client'
import type { ApiClient } from '../api/client'

const defaultClient = createApiClient()

export default function LoginPage({ client = defaultClient }: { client?: ApiClient }) {
  const [identifier, setIdentifier] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const navigate = useNavigate()

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setPending(true)
    try {
      await client.auth.login({ identifier, password })
      navigate('/')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Login gagal, coba lagi.')
    } finally {
      setPending(false)
    }
  }

  return (
    <main className="auth-page">
      <form className="auth-card" onSubmit={onSubmit}>
        <h2>Masuk</h2>
        <label htmlFor="identifier">Username / Email</label>
        <input
          id="identifier"
          value={identifier}
          onChange={(e) => setIdentifier(e.target.value)}
          autoComplete="username"
        />
        <label htmlFor="password">Password</label>
        <input
          id="password"
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="current-password"
        />
        {error && (
          <p role="alert" className="auth-error">
            {error}
          </p>
        )}
        <button type="submit" disabled={pending}>
          {pending ? 'Memproses…' : 'Masuk'}
        </button>
        <p className="auth-switch">
          Belum punya akun? <Link to="/register">Daftar</Link>
        </p>
      </form>
    </main>
  )
}
