# Erda (.NET 10) image for the homeserver (amd64) deployment.
# Arch defaults below target amd64; for an ARM64 host (e.g. the old Jetson) override the OP_ARCH build
# ARG (arm64).
#
# Three stages:
#   build   – restore + publish the Erda web app
#   web     – build the Vue control-panel SPA
#   runtime – ASP.NET runtime + the published app + the SPA
#
# Nothing here uses the GPU; every model call is cloud (the OpenAI-compatible chat endpoint reached
# over HTTP, plus OpenAI transcription).

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

# ---- browser (Playwright MCP) ----------------------------------------------
# Node + the pinned Playwright MCP server + a Chromium build, installed to a world-readable path so
# the container (running as uid 1000) can launch it. Pin must match BrowserOptions.McpArgs.
#
# CRITICAL: install Chromium with the SAME playwright-core the MCP bundles (its own cli.js), NOT
# `npx playwright install`. `npx --yes playwright` pulls the *latest stable* playwright, whose Chromium
# revision (e.g. 1223) can differ from the MCP's pinned (alpha) core (which wants e.g. 1224). The
# server then resolves `--browser chromium` to a build that isn't on disk and fails at runtime with
# "Chromium distribution 'chrome-for-testing' is not installed". Driving the install through the MCP's
# own playwright-core keeps the revision locked to whatever @playwright/mcp@${VERSION} expects.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ARG PLAYWRIGHT_MCP_VERSION=0.0.75
# xvfb: a virtual X display so Chromium can run HEADFUL in this display-less container. Cloudflare
# (e.g. on cardmarket.com) hard-blocks headless Chromium with an "Attention Required" challenge; the
# same automated browser run headful passes. The app is launched under `xvfb-run` (see ENTRYPOINT), so
# the MCP's Chromium — a grandchild process — inherits the virtual DISPLAY when ShowWindow=true.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg unzip xvfb \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g "@playwright/mcp@${PLAYWRIGHT_MCP_VERSION}" \
 && node "$(npm root -g)/@playwright/mcp/node_modules/playwright-core/cli.js" install --with-deps chromium \
 && chmod -R a+rx /ms-playwright \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

# ---- 1Password CLI (op) ----------------------------------------------------
# The op binary resolves op://… secret references and lists the scoped Erda vault for the browser
# sub-agent. Authenticated by OP_SERVICE_ACCOUNT_TOKEN (read-only, one vault) from compose. Default
# amd64 build for the homeserver; override OP_VERSION/OP_ARCH=arm64 for an ARM64 host.
ARG OP_VERSION=2.31.1
ARG OP_ARCH=amd64
RUN curl -fsSL -o /tmp/op.zip \
      "https://cache.agilebits.com/dist/1P/op2/pkg/v${OP_VERSION}/op_linux_${OP_ARCH}_v${OP_VERSION}.zip" \
 && (cd /tmp && unzip -o op.zip op) \
 && mv /tmp/op /usr/local/bin/op \
 && chmod +x /usr/local/bin/op \
 && rm -f /tmp/op.zip \
 && /usr/local/bin/op --version

# Image identity, baked in by CI (see .github/workflows/build.yml) and reported in the WhatsApp
# boot notice (StartupNotifier). Both empty on local builds -> "Version dev (local build)".
ARG GIT_SHA=""
ARG BUILD_TIME=""
ENV ERDA_GIT_SHA=${GIT_SHA} \
    ERDA_BUILD_TIME=${BUILD_TIME}

COPY --from=build /app/publish ./
# The control-panel SPA: served from wwwroot by UseStaticFiles + MapFallbackToFile("index.html").
COPY --from=web /web/dist ./wwwroot

# The control panel (Vue SPA + JSON API) is served here; docker-compose publishes it to the LAN.
EXPOSE 5167

# Run under xvfb-run so a headful Chromium (Erda__Browser__ShowWindow=true) has a display. `-a` picks a
# free server number; the screen is sized generously. Harmless when the browser is headless or disabled
# — the virtual display just goes unused. Without this, headful Chromium fails to launch (no DISPLAY).
ENTRYPOINT ["xvfb-run", "-a", "-s", "-screen 0 1920x1080x24", "dotnet", "Erda.Server.dll"]
