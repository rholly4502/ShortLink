# ShortLink — URL Shortener + Click Analytics

URL shortener service dengan click analytics, dibangun dengan **.NET 10** (Clean Architecture) dan infrastruktur dari **PostgreSQL, Redis, RabbitMQ**, serta autentikasi **JWT**.

Produksi/demo: `https://rhol-ai.tech/shortlink/`

## Fitur

- Register/login/refresh (JWT access + refresh token, role `admin`/`user`).
- Buat short link slug base62 unik + validasi URL (hanya `http/https`, cegah open-redirect).
- `GET /{slug}` → 302 ke URL asli; lookup via Redis cache (TTL 12 jam) dengan fallback PostgreSQL.
- Setiap redirect memancarkan event klik ke RabbitMQ → worker mempersist `clicks` + agregasi (`click_aggregates` per jam/hari/device).
- Analytics endpoint: total klik + breakdown per device/hari.
- Rate-limit berbasis Redis (fixed-window) terpisah untuk API dan redirect.

## Arsitektur

```
┌───────────────────────┐     ┌───────────────────┐      ┌─────────────────────────┐
│ ShortLink.Api (WebAPI)│────▶│      Redis        │      │  ShortLink.Worker (BG)  │
│ auth · CRUD · redirect │────▶│ slug cache+ratelimit│     │ consume clicks → persist │
│ publish click event     │◀───│                   │      │ + agregasi               │
└───────────┬───────────┘     └───────────────────┘      └───────────┬─────────────┘
            │                       ▲                                 │
            ▼                       │                                 ▼
   ┌───────────────┐                │                        ┌──────────────────┐
   │   PostgreSQL   │◀──────────────┴────────────────────────│    RabbitMQ      │
   │ (data utama)   │                  (event klik)          │  exchange/queue   │
   └───────────────┘                                          │  "clicks"         │
                                                              └──────────────────┘
```

**Stack:** .NET 10 · EF Core (Npgsql) · StackExchange.Redis · RabbitMQ.Client · BCrypt · JWT Bearer.

**Struktur proyek:**

- `src/ShortLink.Core/` — domain: entitas, abstraksi (repository/service/clock), service murni tanpa dependensi infra.
- `src/ShortLink.Api/` — Web API: controllers, JWT, middleware rate-limit, DI.
- `src/ShortLink.Infrastructure/` — EF Core `AppDbContext` + migrasi, repository, Redis cache, RabbitMQ publisher, JWT/BCrypt.
- `src/ShortLink.Worker/` — BackgroundService: konsumsi event klik, persist + agregasi.
- `tests/ShortLink.Core.Tests/` — unit test (Core + Api murni), 209 test hijau.

## Menjalankan (dev)

### Prasyarat

- .NET SDK 10.0+
- Docker + Docker Compose

### Langkah

```bash
# 1. Infrastruktur (postgres, redis, rabbitmq)
cp .env.example .env      # isi password di .env
docker compose up -d

# 2. API (listening http://localhost:5203)
cd src/ShortLink.Api
dotnet run

# 3. Worker (konsumsi event klik) — terminal terpisah
cd src/ShortLink.Worker
dotnet run
```

Pada environment `Development`, API otomatis menjalankan migrasi database dan seed admin saat startup (`admin@shortlink.dev`, lihat `appsettings.Development.json` → `AdminSeed`).

### Konfigurasi

Semua kredensial & tuning via env/`appsettings.json` (jangan commit `.env`):

| Kunci | Keterangan |
|---|---|
| `ConnectionStrings:Postgres` | Connection string PostgreSQL |
| `Redis:ConnectionString` | Redis (host:port[,password=..]) |
| `RabbitMQ:ConnectionString` | AMQP URI |
| `Jwt:SigningKey` | ≥32 byte untuk HS256 (dev-only di settings) |
| `RateLimit` | `ApiLimit`/`ApiWindowSeconds` & `RedirectLimit`/`RedirectWindowSeconds` |
| `AdminSeed` | kredensial admin awal |

## Endpoint API

Base URL dev: `http://localhost:5203`. Semua endpoint `/api/*` butuh `Authorization: Bearer <accessToken>` (kecuali `auth/*` dan redirect publik).

### Autentikasi — `POST /auth/register`, `/auth/login`, `/auth/refresh`

```bash
# Register
curl -s -X POST http://localhost:5203/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice","email":"alice@example.com","password":"s3cret-pw"}'

# Login (kembalikan accessToken + refreshToken)
curl -s -X POST http://localhost:5203/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"identifier":"alice","password":"s3cret-pw"}'

# Refresh
curl -s -X POST http://localhost:5203/auth/refresh \
  -H 'Content-Type: application/json' \
  -d '{"refreshToken":"<refreshToken>"}'
```

### Short links — `POST/GET/PUT/DELETE /api/links`

```bash
TOKEN='<accessToken>'

# Buat short link
curl -s -X POST http://localhost:5203/api/links \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"url":"https://example.com/panjang/apa","expiresAt":null}'
# → {"id":"…","slug":"aB3x9","url":"…","ownerId":"…","createdAt":"…","expiresAt":null,"clickCount":0}

# List milik sendiri
curl -s http://localhost:5203/api/links -H "Authorization: Bearer $TOKEN"

# Ambil detail
curl -s http://localhost:5203/api/links/{id} -H "Authorization: Bearer $TOKEN"

# Update URL
curl -s -X PUT http://localhost:5203/api/links/{id} \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"url":"https://example.com/baru","expiresAt":null}'

# Hapus (204)
curl -s -o /dev/null -w '%{http_code}' -X DELETE \
  http://localhost:5203/api/links/{id} -H "Authorization: Bearer $TOKEN"
```

### Redirect publik — `GET /{slug}`

```bash
# 302 ke URL asli
curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' \
  http://localhost:5203/aB3x9
# → 302 https://example.com/panjang/apa
```

### Analytics — `GET /api/links/{id}/analytics`

```bash
curl -s http://localhost:5203/api/links/{id}/analytics \
  -H "Authorization: Bearer $TOKEN"
# → {"totalClicks":12,"perDay":[...],"perDevice":[...]}
```

## Keamanan

- **Open-redirect dicegah**: `UrlValidator` hanya menerima URL `http/https` absolute; skema aneh/relatif ditolak.
- **JWT**: claim `sub` dibaca langsung dari payload (`jwt.Payload["sub"]`) — `JwtSecurityTokenHandler` default memetakan `sub` → `ClaimTypes.NameIdentifier`; jangan andalkan `principal.FindFirst("sub")`. Role di-emit sebagai `ClaimTypes.Role` (URI penuh).
- **Rate-limit**: Redis fixed-window; default 60 req/mnt untuk API, 30 req/mnt untuk redirect.
- **Password**: di-hash dengan BCrypt.

## Testing

```bash
cd /root/ShortLink
dotnet test          # 209 test (slug, url validator, auth JWT, cache, repositori, rate-limit, agregasi, keamanan)
```

## Deploy

Lihat **Deploy notes** (`docs/plans/…` / task P5.3): satu domain `rhol-ai.tech` dengan subpath per app — ShortLink di `/shortlink/` (Caddy route per-subpath; build base-aware: Vite `base` + React `basename` + path relatif).

## Lisensi

Internal project — repo publik `rholly4502/ShortLink`.
