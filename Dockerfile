# Erda (.NET 10) image for the Jetson (ARM64) deployment.
#
# Three stages:
#   build   – restore + publish the Erda web app
#   codex   – fetch the `codex` CLI binary for the target arch
#   runtime – ASP.NET runtime + the published app + the codex binary
#
# Nothing here uses the GPU; every model call is cloud. `CODEX_HOME=/codex` points the
# codex CLI at the mounted, logged-in ChatGPT-subscription session (see docker-compose.yml).

# ---- build ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless a project file changes). Copy just the shared props + the three
# project files the server needs, so the restore layer is reused when only source changes. No
# custom NuGet feed: the MAF preview/alpha packages resolve from nuget.org.
COPY Directory.Build.props ./
COPY Erda.Core/Erda.Core.csproj     Erda.Core/
COPY Erda.Agents/Erda.Agents.csproj Erda.Agents/
COPY Erda.Server/Erda.Server.csproj Erda.Server/
RUN dotnet restore Erda.Server/Erda.Server.csproj

COPY . .
# UseAppHost=false: we launch via `dotnet Erda.Server.dll`, so no native apphost is needed.
RUN dotnet publish Erda.Server/Erda.Server.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- codex ------------------------------------------------------------------
# Fetch the prebuilt codex CLI. Bump CODEX_VERSION to upgrade. The musl build is a static
# binary and runs fine on the Debian-based runtime image. Building natively on the Jetson,
# the target is aarch64; override CODEX_TARGET for an x86_64 host.
FROM alpine:3.21 AS codex
ARG CODEX_VERSION=0.135.0
ARG CODEX_TARGET=aarch64-unknown-linux-musl
RUN apk add --no-cache curl tar \
 && curl -fsSL -o /tmp/codex.tar.gz \
      "https://github.com/openai/codex/releases/download/rust-v${CODEX_VERSION}/codex-${CODEX_TARGET}.tar.gz" \
 && tar -xzf /tmp/codex.tar.gz -C /tmp \
 && mv "/tmp/codex-${CODEX_TARGET}" /usr/local/bin/codex \
 && chmod +x /usr/local/bin/codex \
 && /usr/local/bin/codex --version

# ---- web (Vue control-panel SPA) -------------------------------------------
# Build the Vite SPA. `npm ci` uses the committed package-lock.json for a reproducible install.
# Its dist/ is copied into the runtime image's wwwroot and served as static files with an
# index.html SPA fallback (see Program.cs). Pure Node build — no .NET, no GPU.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=codex /usr/local/bin/codex /usr/local/bin/codex

# ---- browser (Playwright MCP) ----------------------------------------------
# Node + the pinned Playwright MCP server + a Chromium build, installed to a world-readable path so
# the container (running as uid 1000) can launch it. Pin must match BrowserOptions.McpArgs.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ARG PLAYWRIGHT_MCP_VERSION=0.0.75
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg unzip \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g "@playwright/mcp@${PLAYWRIGHT_MCP_VERSION}" \
 && npx --yes playwright install --with-deps chromium \
 && chmod -R a+rx /ms-playwright \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

# ---- 1Password CLI (op) ----------------------------------------------------
# The op binary resolves op://… secret references and lists the scoped Erda vault for the browser
# sub-agent. Authenticated by OP_SERVICE_ACCOUNT_TOKEN (read-only, one vault) from compose. ARM64
# build for the Jetson; override OP_VERSION/OP_ARCH for another host.
ARG OP_VERSION=2.31.1
ARG OP_ARCH=arm64
RUN curl -fsSL -o /tmp/op.zip \
      "https://cache.agilebits.com/dist/1P/op2/pkg/v${OP_VERSION}/op_linux_${OP_ARCH}_v${OP_VERSION}.zip" \
 && (cd /tmp && unzip -o op.zip op) \
 && mv /tmp/op /usr/local/bin/op \
 && chmod +x /usr/local/bin/op \
 && rm -f /tmp/op.zip \
 && /usr/local/bin/op --version

COPY --from=build /app/publish ./
# The control-panel SPA: served from wwwroot by UseStaticFiles + MapFallbackToFile("index.html").
COPY --from=web /web/dist ./wwwroot

# Codex reads its logged-in session from here; mounted from the host (RW for token refresh).
ENV CODEX_HOME=/codex

# The control panel (Vue SPA + JSON API) is served here; docker-compose publishes it to the LAN.
EXPOSE 5167

ENTRYPOINT ["dotnet", "Erda.Server.dll"]
