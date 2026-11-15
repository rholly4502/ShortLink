# ShortLink — Deployment Notes (VPS / Docker Compose Prod)

Rencana produksi untuk `https://rhol-ai.tech/shortlink/`. Stack ini API (.NET 10) + Worker,
dengan PostgreSQL/Redis/RabbitMQ. Satu domain, satu VPS, subpath per app — ShortLink di `/shortlink/`,
dibalik **Caddy** (TLS termination).

## 1. Topologi

```
Internet ─▶ Caddy (443/TLS) ─▶ /shortlink/* ─▶ shortlink-api:8080
                                        └─ (worker container, internal, tidak diexpose)
VPS internal network (compose): api, worker, postgres, redis, rabbitmq
```

- Hanya **Caddy yang diexpose ke internet** (443). Postgres/Redis/RabbitMQ **tidak** di-map ke host di prod.
- API & Worker memakai jaringan compose internal; saling hubungi via nama service.

## 2. Prasyarat VPS

- Ubuntu 22.04/24.04, minimal 2 vCPU / 2–4 GB RAM.
- Docker Engine + Docker Compose plugin.
- Caddy (cara paling mudah: jalankan sebagai container di compose yang sama, path 443).
- DNS: `A` record `rhol-ai.tech` → IP VPS (dan `*.rhol-ai.tech` bila perlu).

## 3. Environment (`.env`)

Salin template: `cp .env.example .env` lalu isi. Variabel yang wajib kuat di produksi:

| Variabel | Catatan |
|---|---|
| `POSTGRES_PASSWORD` | kuat, acak |
| `REDIS_PASSWORD` | dipakai `redis-server --requirepass` |
| `RABBITMQ_PASSWORD` | vhost user |
| `Jwt__SigningKey` | **≥32 byte acak** untuk HS256 (jangan pakai default dev) |
| `AdminSeed__Password` | ganti; setelah seed pertama bisa dihapus |
| `ConnectionStrings__Postgres` | tuju ke service `postgres` di network compose |

> Jangan commit `.env` (sudah di `.gitignore`). `appsettings.json` hanya berisi nilai dev.

## 4. Docker image

Repo **belum punya Dockerfile**. Tambahkan dua file sebelum deploy (multi-stage):

`Dockerfile.api`:
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ShortLink.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShortLink.Api.dll"]
```

`Dockerfile.worker` (sama, ganti project → `src/ShortLink.Worker`, `ShortLink.Worker.dll`).

## 5. `docker-compose.prod.yml`

```yaml
services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: ${POSTGRES_DB}
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER} -d ${POSTGRES_DB}"]
      interval: 5s
      timeout: 5s
      retries: 10

  redis:
    image: redis:7-alpine
    command: ["redis-server", "--requirepass", "${REDIS_PASSWORD}", "--appendonly", "yes"]
    volumes:
      - redisdata:/data
    healthcheck:
      test: ["CMD", "redis-cli", "-a", "${REDIS_PASSWORD}", "ping"]
      interval: 5s
      timeout: 5s
      retries: 10

  rabbitmq:
    image: rabbitmq:3-management-alpine
    environment:
      RABBITMQ_DEFAULT_USER: ${RABBITMQ_USER}
      RABBITMQ_DEFAULT_PASS: ${RABBITMQ_PASSWORD}
    volumes:
      - rabbitmqdata:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 10s
      timeout: 10s
      retries: 12

  api:
    build:
      context: .
      dockerfile: Dockerfile.api
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
      Redis__ConnectionString: "redis:6379,password=${REDIS_PASSWORD}"
      RabbitMQ__ConnectionString: "amqp://${RABBITMQ_USER}:${RABBITMQ_PASSWORD}@rabbitmq:5672/"
      Jwt__SigningKey: "${JWT_SIGNING_KEY}"
      Jwt__Issuer: "shortlink"
      Jwt__Audience: "shortlink-api"
      AdminSeed__Password: "${ADMIN_SEED_PASSWORD}"
    depends_on:
      postgres: { condition: service_healthy }
      redis: { condition: service_healthy }
      rabbitmq: { condition: service_healthy }
    restart: unless-stopped

  worker:
    build:
      context: .
      dockerfile: Dockerfile.worker
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
      RabbitMQ__ConnectionString: "amqp://${RABBITMQ_USER}:${RABBITMQ_PASSWORD}@rabbitmq:5672/"
    depends_on:
      postgres: { condition: service_healthy }
      rabbitmq: { condition: service_healthy }
    restart: unless-stopped

  caddy:
    image: caddy:2-alpine
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data
      - caddy_config:/config
    restart: unless-stopped

volumes:
  pgdata:
  redisdata:
  rabbitmqdata:
  caddy_data:
  caddy_config:
```

> Tidak ada `ports:` untuk postgres/redis/rabbitmq/api di prod — hanya internal network.

## 6. `Caddyfile`

```caddyfile
rhol-ai.tech {
    handle_path /shortlink/* {
        reverse_proxy api:8080
    }
}
```

- `handle_path` otomatis memotong prefix `/shortlink` sebelum diteruskan, jadi controller tetap
  terlihat di root (`/api/links`, `/auth/login`, `/<slug>`). Klien memanggil `https://rhol-ai.tech/shortlink/api/links` dst.
- Caddy menerbitkan TLS otomatis (Let's Encrypt). Pastikan port `80` terbuka untuk challenge.

### Gotcha: `UseHttpsRedirection`
`Program.cs` memakai `app.UseHttpsRedirection()`. Saat Caddy memutus TLS lalu meneruskan **HTTP biasa**
ke container, app melihat request sebagai http dan mungkin me-redirect 307 — bisa menyebabkan loop.
Solusi yang direkomendasikan (perubahan kode kecil, di luar task ini): tambahkan `ForwardedHeaders`
middleware sebelum `UseHttpsRedirection`:

```csharp
using Microsoft.AspNetCore.HttpOverrides;
// setelah builder.Build():
app.UseForwardedHeaders(new ForwardedHeadersOptions {
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
```

Alternatif tanpa ubah kode: nonaktifkan `UseHttpsRedirection` di Production (mis. bungkus `if (!app.Environment.IsProduction())`).
Dokumentasikan pilihanmana yang dipakai di catatan deploy server.

## 7. Migrasi database di produksi

**Penting:** migrasi **hanya auto-berjalan saat `Environment=Development`** (lihat `Program.cs`).
Di produksi, apply schema **sebelum** start API:

```bash
# build script SQL dari mesin pengembang (butuh dotnet-ef + akses ke kredensial DB)
dotnet ef migrations script -p src/ShortLink.Infrastructure -s src/ShortLink.Api -o migrate.sql

# lalu terapkan di VPS (via container postgres)
docker compose -f docker-compose.prod.yml exec -T postgres \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f - < migrate.sql
```

Untuk update berikutnya: generate script increment (`--idempotent` dari migrasi terakhir), backup dulu, baru apply.
Seed admin (`AdminSeed`) dijalankan oleh `DbInitializer` saat API start; pastikan env `AdminSeed__*` terisi saat
pertama kali start, lalu boleh dihapus.

## 8. Backup

Cron harian di VPS:

```bash
0 2 * * * docker compose -f /opt/shortlink/docker-compose.prod.yml exec -T postgres \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" | gzip > /backups/shortlink-$(date +%F).sql.gz
```

Retensi: simpan 14 hari, salin off-site minimal 1x/minggu.

## 9. Update & rollback

```bash
# update
git pull --rebase origin main
docker compose -f docker-compose.prod.yml build api worker
docker compose -f docker-compose.prod.yml up -d
# sebelum update: backup DB (lihat bagian 8)
```

Rollback: restore DB dari backup + `git checkout` commit sebelumnya + rebuild image lama.

## 10. Keamanan produksi checklist

- [x] Firewall: hanya buka `80`/`443` (Caddy). Postgres/Redis/RabbitMQ **tidak** diexpose.
- [x] `Jwt__SigningKey` acak ≥32 byte; jangan pakai nilai dev.
- [x] `AdminSeed__Password` kuat; hapus setelah seed.
- [ ] Cek `redis` pakai `--requirepass` (sudah di compose).
- [ ] `dotnet test` hijau sebelum build image.
- [ ] Jadwalkan backup + uji restore.
