# ═══════════════════════════════════════════════════════════════
# ShortLink — multi-stage build (frontend Vite + .NET 10 API)
# Result image (runtime) melayani SPA static DAN API di :8080.
# ═══════════════════════════════════════════════════════════════

# ── Stage 1: Frontend (Vite) build ─────────────────────────────
FROM node:22-alpine AS fe-build
WORKDIR /fe
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build        # -> /fe/dist (base=/shortlink/)

# ── Stage 2: .NET API publish ──────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore bertahap (cache layer)
COPY ["Directory.Build.props", "./"]
COPY ["src/ShortLink.Api/ShortLink.Api.csproj", "ShortLink.Api/"]
COPY ["src/ShortLink.Core/ShortLink.Core.csproj", "ShortLink.Core/"]
COPY ["src/ShortLink.Infrastructure/ShortLink.Infrastructure.csproj", "ShortLink.Infrastructure/"]
RUN dotnet restore "ShortLink.Api/ShortLink.Api.csproj"

COPY . .
# Tempel hasil build frontend ke wwwroot agar ikut ter-publish.
COPY --from=fe-build /fe/dist src/ShortLink.Api/wwwroot/
WORKDIR /src/ShortLink.Api
RUN dotnet publish -c Release -o /app --no-restore

# ── Stage 3: Runtime ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShortLink.Api.dll"]
