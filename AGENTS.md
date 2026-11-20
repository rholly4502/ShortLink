# AGENTS.md — ShortLink

Panduan arsitektur, batas keamanan, dan aturan kerja wajib bagi seluruh AI coding agent (termasuk scheduled improvement worker) yang berkontribusi di repository `ShortLink`.

---

## 1. Struktur & Batas Arsitektur

Repository ini adalah layanan URL Shortener + Click Analytics berbasis .NET 10 (PostgreSQL, Redis, RabbitMQ) dan React/TypeScript/Vite:

- `src/ShortLink.Core` — Domain entities, abstractions (`Abstractions/`), events (`Events/`), dan business logic murni (`ShortLinkService`, `AuthService`, `ClickIngestionService`, `ClickAggregationService`, `SlugGenerator`, `UrlValidator`, `DeviceTypeDetector`). **Dilarang** bergantung pada `Infrastructure`, `Api`, atau `Worker`.
- `src/ShortLink.Infrastructure` — Implementasi persistensi (PostgreSQL/EF Core), cache (Redis), dan message broker (RabbitMQ).
- `src/ShortLink.Api` — ASP.NET Core Web API (`Controllers`, `Middleware`, `Errors`, `Models`) serta static file serving SPA (`wwwroot` yang dibangun di Dockerfile untuk non-Development).
- `src/ShortLink.Worker` — Background worker consumer RabbitMQ untuk memproses event klik secara asinkron.
- `web/` — Frontend React + TypeScript + Vite (base path `/shortlink/`, dilengkapi `oxlint` dan unit tests).
- `tests/ShortLink.Core.Tests` — Unit & integration tests (termasuk `StaticServingTests`).

### Invarian Teknis & Keamanan Mutlak
- **Hot-Path Redirect (`GET /{slug}`):** Harus tetap ringan dan cepat (cache-first via Redis, publikasi event klik asinkron ke RabbitMQ). Dilarang menambahkan query/write sinkron yang berat ke PostgreSQL di jalur redirect.
- **Keamanan URL & Auth:**
  - `UrlValidator` wajib menolak skema berbahaya (mis. `javascript:`, `data:`) dan input URL malformed.
  - Endpoint manajemen link & analytics wajib menegakkan otorisasi kepemilikan (user hanya dapat mengakses/mengubah/menghapus link miliknya sendiri).
- **Subpath `/shortlink/` & Static Serving:** Jangan merusak prioritas routing API (`/api/*`) terhadap SPA fallback (`MapFallbackToFile`) maupun konfigurasi base path `/shortlink/`.

---

## 2. Hard Guardrails (Dilarang Dilanggar)

1. **Anti-Over-Refactoring:** Dilarang merombak struktur project, mengganti kontrak API/event RabbitMQ secara breaking, mengubah skema migrasi lama, atau melakukan mass-renaming di luar scope 1 task.
2. **Satu Task = Satu PR Kecil:** Maksimal ~300 baris perubahan logic per PR (di luar unit test dan lockfile). Pecah fitur besar menjadi beberapa PR kecil bertahap.
3. **Zero Warning/Test Cheating:**
   - Dilarang menambahkan `#pragma warning disable`, `NoWarn`, atau mematikan linter/analyzer demi membuat build lolos.
   - Dilarang menghapus, men-`Skip`, atau melemahkan assertion test (backend maupun `web/`).
4. **Kontrol Antrean PR (Khusus Worker Otomatis):**
   - Sebelum membuat branch/PR baru, cek daftar PR yang masih `open`. Jika sudah ada **>= 3 PR terbuka**, hentikan eksekusi (no-op) agar antrean review tidak menumpuk.
   - Hindari menyentuh area/file yang sedang dimodifikasi oleh PR terbuka lain.
5. **Dilarang Push/Merge ke `main`:** Worker hanya boleh bekerja di branch terpisah (`agent/<tipe>-<slug-singkat>`) dan membuka Pull Request. Jangan force-push atau auto-merge.
6. **No-Op Lebih Baik dari PR Kosong:** Jika tidak ada perbaikan nyata yang terukur, keluar tanpa membuat PR.

---

## 3. Standar Kode & Verifikasi Wajib

- Terapkan **TDD**: tulis atau perbarui test sebelum/bersamaan dengan implementasi.
- Teruskan `CancellationToken` dengan konsisten pada seluruh operasi `async`.
- Jalankan seluruh verifikasi berikut sebelum membuka PR:
  - `dotnet build ShortLink.slnx` (0 error)
  - `dotnet test` (seluruh test di `ShortLink.Core.Tests` harus PASS)
  - Jika menyentuh folder `web/`: jalankan test (`npm test` / vitest) dan `npm run build` di dalam `web/` sampai hijau.
- Jika task yang diselesaikan tercatat di `TASKS.json`, perbarui statusnya di commit yang sama.

---

## 4. Prioritas Improvement (Urutan Kerja Worker)

Pilih **satu** task terkecil dengan dampak tertinggi dari prioritas teratas:

1. **P1 — Stabilitas, CI & Security Hardening:**
   - Perbaikan build/test gagal di `main`.
   - Penambahan CI test gate (`.github/workflows/ci.yml`: `dotnet test` + test & build `web/`) sebelum deploy.
   - Hardening keamanan: rate limiting endpoint auth/create/redirect, validasi URL anti-SSRF/open-redirect, dan audit kepemilikan link.
2. **P2 — Backlog Resmi:** Task pending di `TASKS.json` atau `docs/`.
3. **P3 — Reliabilitas Distributed System & Fitur Terarah:**
   - Resiliensi RabbitMQ/Redis (retry/backoff, dead-letter handling di `ShortLink.Worker`, graceful fallback bila Redis sementara tidak tersedia).
   - Fitur bernilai tambah kecil (mis. custom alias/slug dengan validasi reservasi path, expiration date link, export CSV analytics, atau cleanup `Class1.cs` sisa template).
   - Penambahan `Directory.Build.props` / analyzer standar atau `ArchitectureTests` untuk menjaga boundary `ShortLink.Core`.
4. **P4 — Test Coverage & UX Polish:** Penambahan unit/integration test untuk edge case nyata dan penyempurnaan terarah pada UI di `web/src/`.

---

## 5. Format Deskripsi Pull Request

Setiap PR yang dibuat agent wajib memuat:
- **Tujuan:** Masalah atau improvement yang diselesaikan (1–2 kalimat).
- **Perubahan Utama:** Daftar ringkas file dan perubahan logic.
- **Verifikasi:** Ringkasan hasil `dotnet test` (dan test + `npm run build` di `web/` bila relevan).
- **Cek Invarian:** Konfirmasi bahwa hot-path redirect, batas `ShortLink.Core`, dan otorisasi kepemilikan link tetap terjaga.
