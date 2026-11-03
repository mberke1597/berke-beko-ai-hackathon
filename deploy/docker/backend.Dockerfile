# syntax=docker/dockerfile:1
# Backend image (ASP.NET Core, .NET 10). Build context = repo root.
#   docker build -f deploy/docker/backend.Dockerfile -t beko-backend .
# Multi-arch: the SDK stage runs on the build platform and cross-publishes for TARGETARCH.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
ARG TARGETARCH
WORKDIR /src

# Only backend/src is needed for publishing (tests run in CI, not in the image).
COPY backend/*.slnx backend/Directory.*.props ./backend/
COPY backend/src/ ./backend/src/
RUN dotnet restore backend/src/Beko.Agents.Api/Beko.Agents.Api.csproj -a $TARGETARCH

RUN dotnet publish backend/src/Beko.Agents.Api/Beko.Agents.Api.csproj \
      -c Release -a $TARGETARCH --no-restore \
      -o /app/publish /p:UseAppHost=false

# Chiseled = distroless Ubuntu, non-root (uid 1654 "app") by default, no shell.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble-chiseled AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_GCHeapHardLimitPercent=0x4B \
    DOTNET_gcServer=0
COPY --from=build /app/publish ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Beko.Agents.Api.dll"]
