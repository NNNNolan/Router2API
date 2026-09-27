# syntax=docker/dockerfile:1

FROM --platform=$BUILDPLATFORM node:22-alpine AS frontend-build
WORKDIR /src/web
RUN corepack enable
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props Router2API.slnx ./
COPY src/Router.Contracts/Router.Contracts.csproj src/Router.Contracts/
COPY src/Router.Infrastructure/Router.Infrastructure.csproj src/Router.Infrastructure/
COPY src/Router.Host/Router.Host.csproj src/Router.Host/
RUN dotnet restore src/Router.Host/Router.Host.csproj -a "$TARGETARCH"
COPY src/ ./src/
COPY --from=frontend-build /src/web/dist ./web/dist
ARG HOST_VERSION
RUN dotnet publish src/Router.Host/Router.Host.csproj -c Release -a "$TARGETARCH" --self-contained false -o /app/publish --no-restore /p:UseAppHost=false -p:HostVersion="$HOST_VERSION"

# 仅复制目标架构的 ICU 和时区文件，不运行目标架构程序。
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine-extra AS runtime-deps

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV TZ=Asia/Shanghai
COPY --from=runtime-deps /usr/lib/libicu* /usr/lib/
COPY --from=runtime-deps /usr/share/icu /usr/share/icu
COPY --from=runtime-deps /usr/share/zoneinfo /usr/share/zoneinfo
COPY --from=backend-build /app/publish ./
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/router2api-entrypoint
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/router2api-entrypoint"]
