# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MatCMS.csproj ./
RUN dotnet restore MatCMS.csproj

COPY . .
RUN dotnet publish MatCMS.csproj -c Release -o /app/publish /p:UseAppHost=false

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
