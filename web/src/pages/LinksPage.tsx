import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { ApiError, createApiClient } from '../api/client'
import type { ApiClient } from '../api/client'
import type { LinkResponse } from '../api/types'

const defaultClient = createApiClient()

function toIso(value: string): string | null {
  return value ? new Date(value).toISOString() : null
}

function toLocalInput(iso: string | null): string {
  if (!iso) return ''
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export default function LinksPage({ client = defaultClient }: { client?: ApiClient }) {
  const navigate = useNavigate()
  const [links, setLinks] = useState<LinkResponse[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)

  const [newUrl, setNewUrl] = useState('')
  const [newCustomSlug, setNewCustomSlug] = useState('')
  const [newExpires, setNewExpires] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)

  const [editingId, setEditingId] = useState<string | null>(null)
  const [editUrl, setEditUrl] = useState('')
  const [editExpires, setEditExpires] = useState('')
  const [saving, setSaving] = useState(false)

  async function load() {
    try {
      setLinks(await client.links.list())
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.message : 'Gagal memuat daftar tautan.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client])

  if (!client.isAuthenticated()) return <Navigate to="/login" replace />

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    setSubmitting(true)
    try {
      const trimmedSlug = newCustomSlug.trim()
      const payload = trimmedSlug
        ? { url: newUrl, expiresAt: toIso(newExpires), customSlug: trimmedSlug }
        : { url: newUrl, expiresAt: toIso(newExpires) }
      await client.links.create(payload)
      setNewUrl('')
      setNewCustomSlug('')
      setNewExpires('')
      await load()
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : 'Gagal membuat tautan.')
    } finally {
      setSubmitting(false)
    }
  }

  function startEdit(link: LinkResponse) {
    setEditingId(link.id)
    setEditUrl(link.url)
    setEditExpires(toLocalInput(link.expiresAt))
  }

  async function onSave(e: FormEvent) {
    e.preventDefault()
    if (!editingId) return
    setSaving(true)
    try {
      await client.links.update(editingId, { url: editUrl, expiresAt: toIso(editExpires) })
      setEditingId(null)
      await load()
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : 'Gagal menyimpan perubahan.')
      setSaving(false)
    }
  }

  async function onDelete(id: string) {
    if (!window.confirm('Hapus tautan ini?')) return
    try {
      await client.links.remove(id)
      await load()
    } catch {
      // biarkan daftar tetap seperti sebelumnya
    }
  }

  return (
    <main className="links-page">
      <h2>Kelola Tautan</h2>

      {loadError && (
        <p role="alert" className="auth-error">{loadError}</p>
      )}

      <form className="auth-card" onSubmit={onCreate}>
        <label htmlFor="new-url">URL Baru</label>
        <input id="new-url" value={newUrl} onChange={(e) => setNewUrl(e.target.value)} placeholder="https://…" />
        <label htmlFor="new-custom-slug">Alias Kustom (opsional)</label>
        <input
          id="new-custom-slug"
          value={newCustomSlug}
          onChange={(e) => setNewCustomSlug(e.target.value)}
          placeholder="mis. promo2026 (3–32 huruf/angka)"
          maxLength={32}
        />
        <label htmlFor="new-expires">Kadaluarsa (opsional)</label>
        <input id="new-expires" type="datetime-local" value={newExpires} onChange={(e) => setNewExpires(e.target.value)} />
        {formError && <p role="alert" className="auth-error">{formError}</p>}
        <button type="submit" disabled={submitting}>
          {submitting ? 'Membuat…' : 'Buat'}
        </button>
      </form>

      {loading ? (
        <p>Memuat…</p>
      ) : (
        <ul className="link-list">
          {links.map((link) => (
            <li key={link.id} className="link-row" data-testid={`link-row-${link.id}`}>
              {editingId === link.id ? (
                <form className="edit-form" onSubmit={onSave}>
                  <label htmlFor="edit-url">URL</label>
                  <input id="edit-url" value={editUrl} onChange={(e) => setEditUrl(e.target.value)} />
                  <label htmlFor="edit-expires">Kadaluarsa</label>
                  <input id="edit-expires" type="datetime-local" value={editExpires} onChange={(e) => setEditExpires(e.target.value)} />
                  <div className="row-actions">
                    <button type="submit" disabled={saving}>
                      {saving ? 'Menyimpan…' : 'Simpan'}
                    </button>
                    <button type="button" onClick={() => setEditingId(null)}>Batal</button>
                  </div>
                </form>
              ) : (
                <>
                  <div className="link-main">
                    <span className="link-slug">{link.slug}</span>
                    <a className="link-url" href={link.url} target="_blank" rel="noreferrer">{link.url}</a>
                    <span className="link-meta">
                      {link.clickCount} klik · {link.createdAt.slice(0, 10)}
                    </span>
                  </div>
                  <div className="row-actions">
                    <button type="button" onClick={() => navigate(`/links/${link.id}/analytics`)}>Analytics</button>
                    <button type="button" onClick={() => startEdit(link)}>Edit</button>
                    <button type="button" onClick={() => onDelete(link.id)}>Hapus</button>
                  </div>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
    </main>
  )
}
