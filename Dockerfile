# ---- Stage 1: build the React app ----
# Full image name (docker.io/library/...): Podman does not assume Docker Hub
# for short names like "node:24-alpine". A full name works in Podman and Docker.
FROM docker.io/library/node:24-alpine AS web
WORKDIR /web
# Copy package files first: the build caches "npm ci" until they change.
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# ---- Stage 2: build the .NET API ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# We do NOT copy global.json, so the image's own .NET 10 SDK is used
# as is. Local builds and CI still follow global.json.
COPY Directory.Build.props .editorconfig ./
COPY src/RxRag.Core/RxRag.Core.csproj src/RxRag.Core/
COPY src/RxRag.Api/RxRag.Api.csproj src/RxRag.Api/
RUN dotnet restore src/RxRag.Api
COPY src/RxRag.Core/ src/RxRag.Core/
COPY src/RxRag.Api/ src/RxRag.Api/
RUN dotnet publish src/RxRag.Api -c Release -o /app --no-restore

# ---- Stage 3: small runtime image ----
# "Chiseled" = Ubuntu with only what .NET needs: no shell, no package
# manager, runs as a non-root user. Fewer parts, fewer CVEs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
# .NET images listen on 8080 by default.
EXPOSE 8080
ENTRYPOINT ["dotnet", "RxRag.Api.dll"]
