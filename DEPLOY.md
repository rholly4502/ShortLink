# Deploy ShortLink (rhol-ai.tech/shortlink)

Frontend SPA (Vite, base=`/shortlink/`) di-build ke dalam image API lewat Dockerfile multi-stage
(stage `fe-build` → `dist` ditaruh di `src/ShortLink.Api/wwwroot/` → ikut publish).
Jadi **satu container** `shortlink-api` melayani API **dan** static SPA.

## Prasyarat (di VPS)
1. Buat shared Docker network sekali: `docker network create shared_bridge`
2. `cp .env.example .env` lalu isi: POSTGRES_*, REDIS_PASSWORD, RABBITMQ_*, JWT_SIGNING_KEY (>=32 byte), ADMIN_SEED_*.
3. `.env` berisi juga `SHARED_NETWORK=shared_bridge`.

## Build & run
```bash
docker compose up -d --build
docker compose ps
# API (butuh auth -> 401) & SPA index
curl -s http://localhost:8080/api/links
curl -s http://localhost:8080/ | head
```

## Routing Caddy (domain rhol-ai.tech, subpath /shortlink/)
Caddy mem-strip prefix; backend melihat path bersih tanpa `/shortlink`.
**Urutan penting:** blok API dulu (prefix lebih panjang) sebelum static.

```caddy
rhol-ai.tech {
    # 1) API — strip "/shortlink/api" -> backend lihat /auth/*, /api/links/*
    handle_path /shortlink/api/* {
        reverse_proxy shortlink-api:8080
    }

    # 2) Static SPA — strip "/shortlink" -> backend lihat /assets/*, /index.html
    handle_path /shortlink/* {
        reverse_proxy shortlink-api:8080
    }
}
```

Catatan:
- Frontend memanggil `${baseUrl}/api/links` & `${baseUrl}/auth/login` dengan `baseUrl=/shortlink/api`
  → setelah strip `/shortlink/api`, controller `/api/links` dan `/auth` cocok.
- Asset SPA dirujuk sbg `/shortlink/assets/*` (base Vite) → strip `/shortlink` jadi `/assets/*`.
- Jangan balik urutan: kalau `/shortlink/*` lebih dulu, `/shortlink/api/...` hanya ter-strip
  `/shortlink` → `/api/...` (tdk cocok controller).

## Catatan
- Koneksi antar-service pakai nama service (`postgres`, `redis`, `rabbitmq`) di network default.
- Service `api` join `shared_bridge` agar Caddy (OmniChat) bisa reach `shortlink-api:8080`.
- Redis butuh password (requirepass) → connection string `redis:6379,password=...`.
