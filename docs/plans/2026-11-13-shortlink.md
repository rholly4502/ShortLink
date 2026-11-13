# ShortLink — URL Shortener + Click Analytics

Rencana implementasi (source of truth untuk cron). Setiap subtask = satu unit kerja kecil dengan hasil yang bisa dicek & di-commit.

## Stack & Konvensi
- .NET 10, Clean Architecture (Core ` Api + Worker).
- PostgreSQL (data utama), Redis (cache slug + rate limit), RabbitMQ (event klik).
- JWT auth; EF Core; Docker Compose untuk dev.
- TDD: tulis test gagal → implementasi → hijau → commit.
- Semua file di `src/` & `tests/`; commit topik, jangan satu commit gede.

## Alur fitur (MVP)
1. User daftar/login (JWT + refresh).
2. User bikin short link (slug base62 unik, validasi URL).
3. `GET /{slug}` → 302 ke URL asli, lookup via Redis cache (fallback PG).
4. Redirect emit event klik ke RabbitMQ → worker persist ke `clicks`.
5. `GET /api/links/{id}/analytics` → agregasi per jam/hari/device/country.

## Struktur
```
src/ShortLink.Core/          # domain, interfaces, service logic murni (tanpa dependensi infra)
src/ShortLink.Api/           # Web API: controllers, auth, middleware rate-limit, DI
src/ShortLink.Worker/        # BackgroundService: consume RabbitMQ, persist clicks
src/ShortLink.Core.Tests/    # unit test (Core + murni Api)
docker-compose.yml           # postgres, redis, rabbitmq (dev)
docs/plans/2026-11-13-shortlink.md
TASKS.json                   # manifest progress (dibaca cron)
```

## Task (urutan eksekusi)
### P0 — Foundation (sudah dikerjakan manual)
- [x] P0.1 Scaffold solusi + referensi + build hijau
- [x] P0.2 Tulis rencana ini + TASKS.json
- [ ] P0.3 `docker-compose.yml` (postgres/redis/rabbitmq) + `.env` (git-ignored)
- [ ] P0.4 `appsettings` + `appsettings.Development.json` berisi connection string
- [ ] P0.5 Commit awal + push repo baru (publik? tanya user) 

### P1 — Core (domain)
- [ ] P1.1 Entities: `User`, `ShortLink`, `Click`, `ClickAggregate` + enum role
- [ ] P1.2 Abstraksi: `IShortLinkRepository`, `IClickRepository`, `IUserRepository`, `ISlugGenerator`, `IUrlValidator`, `IClock`
- [ ] P1.3 `SlugGenerator` (base62) + unit test
- [ ] P1.4 `UrlValidator` (hanya http/https, cegah open-redirect) + unit test
- [ ] P1.5 `ShortLinkService` (create/resolve/validate/analytics domain logic) + unit test

### P2 — Infrastructure (EF Core + Redis + RabbitMQ)
- [ ] P2.1 Tambah package (Npgsql.EntityFrameworkCore, StackExchange.Redis, RabbitMQ.Client, BCrypt) ke Core/Api/Worker
- [ ] P2.2 `AppDbContext` + entity config + migration awal + seed admin
- [ ] P2.3 Implementasi `IShortLinkRepository`/`IClickRepository` (EF Core)
- [ ] P2.4 `RedisSlugCache` (GET/SET dengan TTL) + fallback PG
- [ ] P2.5 `ClickEventPublisher` (RabbitMQ publish) + `ClickEventConsumer` (worker)
- [ ] P2.6 Auto-migrate saat startup (dev) + compose siap

### P3 — Api
- [ ] P3.1 JWT auth: register/login/refresh endpoint + role
- [ ] P3.2 CRUD short link endpoint (create/list/get/update/delete) + owner check
- [ ] P3.3 Endpoint publik `GET /{slug}` + hit Redis + emisi event klik
- [ ] P3.4 Endpoint analytics `GET /api/links/{id}/analytics`
- [ ] P3.5 Middleware rate-limit berbasis Redis (API & redirect)
- [ ] P3.6 Validasi input + error handling konsisten

### P4 — Worker
- [ ] P4.1 Konsumsi event klik → persist `clicks`
- [ ] P4.2 Agregasi (per jam/hari/device/country) → `click_aggregates`

### P5 — Hardening & polishing
- [ ] P5.1 Uji keamanan: open-redirect, rate-limit, auth bypass, input injection
- [ ] P5.2 README (cara jalan, arsitektur, screenshot/curl)
- [ ] P5.3 Deploy notes (VPS / Docker Compose prod)
- [ ] P5.4 Uji end-to-end manual (curl flow lengkap)

## Aturan untuk cron
- Baca `TASKS.json`; kerjakan SATU task pertama yang `pending` (urutan P0→P5).
- Ikuti TDD: test dulu, implementasi, pastikan hijau, commit.
- Jangan ubah scope/urutan tanpa persetujuan user. Kalau task butuh keputusan (mis. nama repo publik/privat), STOP & lapor, jangan menebak.
- Setelah selesai 1 task: update TASKS.json ke `done`, commit seluruh perubahan, push.
- Wajib: edit file di `/root/ShortLink` HANYA via heredoc (`cat > f << 'EOF'`) atau `python3 - << 'PY'` — DILARANG pakai write_file/patch.

## Rulings (ledger)
- **P1.1 — Entity naming**: kelas entity `ShortLink` diubah menjadi `ShortenedUrl`. Sebab: nama type sama dengan root namespace `ShortLink` → konflik C# (CS0118) di semua kode di bawah `ShortLink.*`. Perbaikan permanen (type dipakai di DbSet/DI/service). Konsep tetap sama (one short link). FK/nav di `Click`/`ClickAggregate`: `ShortenedUrlId`/`ShortenedUrl`.
