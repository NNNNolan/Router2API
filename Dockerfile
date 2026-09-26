FROM node:22-alpine AS frontend-build
WORKDIR /src/web
RUN corepack enable
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY Directory.Build.props Router2API.slnx ./
COPY src/Router.Contracts/Router.Contracts.csproj src/Router.Contracts/
COPY src/Router.Infrastructure/Router.Infrastructure.csproj src/Router.Infrastructure/
COPY src/Router.Host/Router.Host.csproj src/Router.Host/
RUN dotnet restore src/Router.Host/Router.Host.csproj
COPY src/ ./src/
COPY --from=frontend-build /src/web/dist ./web/dist
RUN dotnet publish src/Router.Host/Router.Host.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV TZ=Asia/Shanghai
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && ln -snf "/usr/share/zoneinfo/${TZ}" /etc/localtime \
    && echo "${TZ}" > /etc/timezone \
    && rm -rf /var/lib/apt/lists/*
COPY --from=backend-build /app/publish ./
COPY docker-entrypoint.sh /usr/local/bin/router2api-entrypoint
RUN chmod +x /usr/local/bin/router2api-entrypoint
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/router2api-entrypoint"]
