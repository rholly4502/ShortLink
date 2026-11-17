# ShortLink API — multi-stage build (.NET 10)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore bertahap (cache layer)
COPY ["src/ShortLink.Api/ShortLink.Api.csproj", "ShortLink.Api/"]
COPY ["src/ShortLink.Core/ShortLink.Core.csproj", "ShortLink.Core/"]
COPY ["src/ShortLink.Infrastructure/ShortLink.Infrastructure.csproj", "ShortLink.Infrastructure/"]
RUN dotnet restore "ShortLink.Api/ShortLink.Api.csproj"

COPY . .
WORKDIR /src/ShortLink.Api
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShortLink.Api.dll"]
