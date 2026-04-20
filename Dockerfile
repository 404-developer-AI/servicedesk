# syntax=docker/dockerfile:1.7
#
# Multi-stage build for Servicedesk (ASP.NET Core 8 + React 19 bundled into one
# image). The React SPA is built in a Node stage and copied into the .NET
# publish output's wwwroot, where Program.cs serves it via UseStaticFiles +
# MapFallbackToFile. Backend-only devs never touch Node — the frontend bundle
# happens here, not via an MSBuild target.

# -----------------------------------------------------------------------------
# Stage 1 — build the React SPA.
# -----------------------------------------------------------------------------
FROM node:20-alpine AS node-build
WORKDIR /web

# Leverage Docker layer cache: only the lockfile changes dependencies.
COPY src/Servicedesk.Web/package.json src/Servicedesk.Web/package-lock.json* ./
RUN npm ci

COPY src/Servicedesk.Web/ ./
RUN npm run build

# -----------------------------------------------------------------------------
# Stage 2 — restore + publish the .NET app.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet-build
WORKDIR /src

# Restore first with just csproj files so dependency restore is cacheable.
COPY Servicedesk.slnx ./
COPY src/Servicedesk.Domain/Servicedesk.Domain.csproj src/Servicedesk.Domain/
COPY src/Servicedesk.Infrastructure/Servicedesk.Infrastructure.csproj src/Servicedesk.Infrastructure/
COPY src/Servicedesk.Api/Servicedesk.Api.csproj src/Servicedesk.Api/
RUN dotnet restore src/Servicedesk.Api/Servicedesk.Api.csproj

# Now bring in source (excluding Servicedesk.Web — that's handled via --from).
COPY src/Servicedesk.Domain/ src/Servicedesk.Domain/
COPY src/Servicedesk.Infrastructure/ src/Servicedesk.Infrastructure/
COPY src/Servicedesk.Api/ src/Servicedesk.Api/

# MinVer reads git tags — without the .git folder the version falls back to
# 0.0.0-alpha.0.1. Pass APP_VERSION at build time to override cleanly.
ARG APP_VERSION
RUN dotnet publish src/Servicedesk.Api/Servicedesk.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    ${APP_VERSION:+/p:MinVerVersionOverride=${APP_VERSION}}

# Drop the built SPA into the publish wwwroot so static-file middleware finds it.
COPY --from=node-build /web/dist/ /app/publish/wwwroot/

# -----------------------------------------------------------------------------
# Stage 3 — slim runtime.
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

# Non-root runtime user. uid 10001 avoids colliding with host uids < 1000.
# icu-libs is required because Servicedesk.Api.csproj sets
# InvariantGlobalization=false (we rely on culture-aware sorting + date
# formatting for audit timestamps and ticket displays). Alpine-aspnet ships
# without ICU; without this the app FailFasts on the first CultureInfo call.
RUN addgroup -S sd && adduser -S -G sd -u 10001 sd \
    && apk add --no-cache wget icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app
COPY --from=dotnet-build --chown=sd:sd /app/publish/ ./

USER sd
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

HEALTHCHECK --interval=20s --timeout=3s --start-period=30s --retries=5 \
    CMD wget -qO- http://localhost:8080/api/system/health || exit 1

ENTRYPOINT ["dotnet", "Servicedesk.Api.dll"]
