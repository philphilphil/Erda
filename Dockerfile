# Erda (.NET 10) image for the homeserver (amd64) deployment.
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

ENTRYPOINT ["dotnet", "Erda.Server.dll"]
