# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Build context is the REPO ROOT, not the project folder: both applications reference
# src/MatCMS.Shared (the cloud<->instance contract), and a context scoped to one project
# cannot see it. Restore the project files first so the NuGet layer is cached independently
# of the source.
COPY src/MatCMS.Shared/MatCMS.Shared.csproj src/MatCMS.Shared/
COPY src/MatCMS.Shared.Web/MatCMS.Shared.Web.csproj src/MatCMS.Shared.Web/
COPY src/MatCMS.Cloud/MatCMS.Cloud.csproj src/MatCMS.Cloud/
RUN dotnet restore src/MatCMS.Cloud/MatCMS.Cloud.csproj

# Version passed in by CI (see .github/workflows). Local builds default to "local".
# We only override InformationalVersion (a free-form string) so the build never
# breaks even when APP_VERSION has no numeric prefix (e.g. "nightly-...", "local-...").
ARG APP_VERSION=local

COPY src/MatCMS.Shared/ src/MatCMS.Shared/
COPY src/MatCMS.Shared.Web/ src/MatCMS.Shared.Web/
COPY src/MatCMS.Cloud/ src/MatCMS.Cloud/
RUN dotnet publish src/MatCMS.Cloud/MatCMS.Cloud.csproj -c Release -o /app/publish /p:UseAppHost=false /p:InformationalVersion=${APP_VERSION}

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Writable folder for the SQLite DB and the data-protection keys.
RUN mkdir -p /app/appdata && chown -R app:app /app

# gosu lets the entrypoint start as root, grant the app process access to a mounted Docker
# socket (see docker-entrypoint.sh) and then drop back to the unprivileged "app" user — so a
# socket mount "just works" with no group_add / DOCKER_GID, exactly like Portainer & co.
RUN apt-get update \
 && apt-get install -y --no-install-recommends gosu \
 && rm -rf /var/lib/apt/lists/* \
 && gosu nobody true
COPY src/MatCMS.Cloud/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# The internal port stays the ASP.NET base-image default (8080).
# The externally reachable port is set only via the compose port mapping.
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# PID 1 starts as root ONLY so the entrypoint can adopt the socket's group; the app itself
# runs as the non-root "app" user (the entrypoint execs it via gosu). CMD is what it execs.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "MatCMS.Cloud.dll"]
