# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MatCMS.csproj ./
RUN dotnet restore MatCMS.csproj

# Version passed in by CI (see .github/workflows). Local builds default to "local".
# We only override InformationalVersion (a free-form string) so the build never
# breaks even when APP_VERSION has no numeric prefix (e.g. "nightly-...", "local-...").
ARG APP_VERSION=local

COPY . .
RUN dotnet publish MatCMS.csproj -c Release -o /app/publish /p:UseAppHost=false /p:InformationalVersion=${APP_VERSION}

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Writable folders for the SQLite DB, data-protection keys and uploads.
USER root
RUN mkdir -p /app/appdata /app/wwwroot/uploads && chown -R app:app /app
USER app

ENV ASPNETCORE_HTTP_PORTS=9101 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 9101

ENTRYPOINT ["dotnet", "MatCMS.dll"]
