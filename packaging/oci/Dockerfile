# syntax=docker/dockerfile:1
#
# Bazaar.Server.Host container image (doc 37, OCI row; doc 03 host design).
# Build from the repository root so the build context contains src/:
#   docker buildx build -f packaging/oci/Dockerfile --platform linux/amd64,linux/arm64 .
#
# No secrets are baked in. The database connection is a secret reference (file:) supplied at run time.

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:11.0

# ---- build: runs on the build machine's platform and cross-publishes for TARGETARCH ----
FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
ARG TARGETARCH
WORKDIR /src

# Only what the Host's project graph needs: the build props, central package versions, NuGet source and sources.
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props ./
COPY src/ ./src/

RUN set -eux; \
    case "${TARGETARCH}" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    dotnet restore src/Bazaar.Server.Host/Bazaar.Server.Host.csproj -r "${rid}"; \
    dotnet publish src/Bazaar.Server.Host/Bazaar.Server.Host.csproj \
      -c Release -r "${rid}" --self-contained false --no-restore -o /out

# ---- runtime: ASP.NET runtime only, non-root ----
FROM ${RUNTIME_IMAGE} AS runtime

# UID 1654 is the non-root "app" user in the Microsoft .NET images. Numeric so the check does not depend on names.
ARG APP_UID=1654

WORKDIR /app
COPY --from=build --chown=${APP_UID}:${APP_UID} /out ./

# Data root (doc 03 Bazaar:Host:DataRoot). The volume in docker-compose.yml takes its place at run time.
RUN mkdir -p /var/lib/bazaar && chown ${APP_UID}:${APP_UID} /var/lib/bazaar

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_ENVIRONMENT=Production \
    Bazaar__Host__DataRoot=/var/lib/bazaar \
    ASPNETCORE_URLS= \
    ASPNETCORE_HTTP_PORTS=

# The status and wire listener binds to loopback only (doc 03 floor; Program.cs). It is not published
# from the container. The health check reads /health/live from inside the container over loopback.
# Bash /dev/tcp is used because the base image has no curl or wget.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/7390 && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200'"]

VOLUME ["/var/lib/bazaar"]
USER ${APP_UID}:${APP_UID}

# Run with no arguments to serve. Run with "migrate" to apply schema migrations (doc 03: migrations are explicit).
ENTRYPOINT ["dotnet", "Bazaar.Server.Host.dll"]
