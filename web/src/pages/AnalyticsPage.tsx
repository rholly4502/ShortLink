import { useEffect, useState } from 'react'
import { Link, Navigate, useParams } from 'react-router-dom'
import { ApiError, createApiClient } from '../api/client'
import type { ApiClient } from '../api/client'
import type { AnalyticsSummary } from '../api/types'

const defaultClient = createApiClient()

export default function AnalyticsPage({ client = defaultClient }: { client?: ApiClient }) {
  const { id } = useParams<{ id: string }>()
  const [summary, setSummary] = useState<AnalyticsSummary | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)

  useEffect(() => {
    if (!id) return
    const linkId = id
    let cancelled = false
    async function load() {
      try {
        const data = await client.links.analytics(linkId)
        if (!cancelled) setSummary(data)
      } catch (err) {
        if (!cancelled) setError(err instanceof ApiError ? err.message : 'Gagal memuat analytics.')
      } finally {
        if (!cancelled) setLoading(false)
      }
    }
    void load()
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, id])

  if (!client.isAuthenticated()) return <Navigate to="/login" replace />

  async function onExportCsv() {
    if (!id) return
    setExportError(null)
    setExporting(true)
    try {
      const csv = await client.links.analyticsCsv(id)
      const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
      const url = URL.createObjectURL(blob)
      try {
        const anchor = document.createElement('a')
        anchor.href = url
        anchor.download = `analytics-${id}.csv`
        document.body.appendChild(anchor)
        anchor.click()
        anchor.remove()
      } finally {
        URL.revokeObjectURL(url)
      }
    } catch (err) {
      setExportError(err instanceof ApiError ? err.message : 'Gagal mengekspor CSV analytics.')
    } finally {
      setExporting(false)
    }
  }

  const maxDay = summary && summary.byDay.length > 0
    ? Math.max(...summary.byDay.map((d) => d.count))
    : 0

  return (
    <main className="analytics-page">
      <h2>Analytics</h2>
      <Link to="/links" className="back-link">← Kembali ke daftar</Link>

      {loading && <p>Memuat…</p>}
      {error && <p role="alert" className="auth-error">{error}</p>}

      {summary && (
        <>
          <div className="analytics-header">
            <div className="analytics-total">
              <span className="analytics-total-label">Total klik</span>
              <span className="analytics-total-value">{summary.totalClicks}</span>
            </div>
            <button
              type="button"
              className="export-csv-btn"
              onClick={onExportCsv}
              disabled={exporting}
            >
              {exporting ? 'Mengekspor…' : 'Export CSV'}
            </button>
          </div>
          {exportError && <p role="alert" className="auth-error">{exportError}</p>}

          <section className="analytics-section" aria-label="Klik per hari">
            <h3>Klik per hari</h3>
            {summary.byDay.length === 0 ? (
              <p>Belum ada data.</p>
            ) : (
              <ul className="day-chart">
                {summary.byDay.map((d, i) => (
                  <li key={d.day} className="day-row">
                    <span className="day-label">{d.day.slice(0, 10)}</span>
                    <span className="day-track">
                      <span
                        className="day-bar"
                        data-testid={`day-bar-${i}`}
                        style={{ width: `${maxDay > 0 ? Math.round((d.count / maxDay) * 100) : 0}%` }}
                      />
                    </span>
                    <span className="day-count">{d.count}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="analytics-section" aria-label="Berdasarkan perangkat">
            <h3>Perangkat</h3>
            {summary.byDevice.length === 0 ? (
              <p>Belum ada data.</p>
            ) : (
              <ul className="analytics-list">
                {summary.byDevice.map((s) => (
                  <li key={s.device} className="analytics-row">
                    <span>{s.device}</span>
                    <span data-testid={`device-${s.device}`}>{s.count}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="analytics-section" aria-label="Berdasarkan negara">
            <h3>Negara</h3>
            {summary.byCountry.length === 0 ? (
              <p>Belum ada data.</p>
            ) : (
              <ul className="analytics-list">
                {summary.byCountry.map((s) => (
                  <li key={s.country} className="analytics-row">
                    <span>{s.country}</span>
                    <span data-testid={`country-${s.country}`}>{s.count}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </main>
  )
}
