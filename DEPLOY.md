# Deploy ShortLink (rhol-ai.tech/shortlink)

API-only dulu (frontend belakangan). Route Caddy: `/shortlink/api/*` -> `shortlink-api:8080`.

## Prasyarat (di VPS)
1. Buat shared Docker network sekali: `docker network create shared_bridge`
2. `cp .env.example .env` lalu isi: POSTGRES_*, REDIS_PASSWORD, RABBITMQ_*, JWT_SIGNING_KEY (>=32 byte), ADMIN_SEED_*.
3. `cp .env` berisi juga `SHARED_NETWORK=shared_bridge`.

## Build & run
```bash
docker compose up -d --build
docker compose ps
curl -s http://localhost:8080/api/health   # kalau ada endpoint health
```

## Catatan
- Koneksi antar-service memakai nama service (`postgres`, `redis`, `rabbitmq`) di default network.
- Service `api` join `shared_bridge` agar Caddy (OmniChat) bisa reach `shortlink-api:8080`.
- Redis butuh password (requirepass) -> connection string `redis:6379,password=...`.
