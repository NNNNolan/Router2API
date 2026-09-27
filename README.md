# Router2API

.NET 10 模型路由宿主，支持 C# DLL 和 Jint JavaScript 插件，附 Vue 3 管理后台。

**本仓库只发布宿主，不捆绑提供方插件。** 账号、代理池、重试、公共协议、SSE 生命周期、任务和插件加载由宿主管理；具体上游业务从独立插件仓库安装。

| 仓库 | 职责 |
| --- | --- |
| [Router2API](https://github.com/NNNNolan/Router2API) | 本仓库：Contracts、Infrastructure、Host、管理前端和宿主测试 |
| [Rouer-Plugins-js](https://github.com/NNNNolan/Rouer-Plugins-js) | js-forwardapi、JS SDK/教程和 Node 模拟测试 |
| [Rouer-Plugins-Csharp](https://github.com/NNNNolan/Rouer-Plugins-Csharp) | 仅 ForwardAPI、C# SDK 教程和对应测试 |

仓库之间不需要 Git submodule。配置统一使用 `ROUTER2API_`，页面桥为 `window.Router2API`，不再读取旧配置前缀。升级已有环境需同步修改环境变量/页面桥；现有 Config 文件与数据路径不会自动搬迁。

## 阅读入口

- [Docker 安装](#docker-安装)
- [SDK 与插件边界](sdk/README.md)
- [宿主完整运行流程](sdk/HOST-LIFECYCLE.md)
- [AI 开发工作单](sdk/AI-DEVELOPMENT.md)
- [公开前与部署安全检查](SECURITY.md)

## 本地开发

需要 .NET 10 SDK、PowerShell 7、Node 22.18+/24 LTS 和项目声明版本的 pnpm。
运行 jsdom 30 页面测试时，Node 需满足 `^22.22.2 || ^24.15.0 || >=26.0.0`；建议直接使用对应 LTS 的最新补丁版。

```powershell
# 仓库根目录。corepack 使用 web/package.json 指定的 pnpm 版本。
corepack enable
pnpm --dir web install --frozen-lockfile
pnpm --dir web build

dotnet build Router2API.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
```

前端源代码在 `web`，构建产物不提交。`pnpm --dir web build` 会生成 `web/dist` 并同步覆盖 `src/Router.Host/wwwroot`；随后构建宿主会将静态文件复制到运行目录。Docker 也从源码构建前端。不安装任何插件时，管理后台仍可启动，但没有提供方模型。

### 第一次启动先设置新凭据

仓库不提供默认可用管理员密码/API Key，也不携带开发环境的 User Secrets：

```powershell
$env:ROUTER2API_Auth__Admin__Users__0__Username = 'admin'
$env:ROUTER2API_Auth__Admin__Users__0__Password = Read-Host '新的管理员密码' -MaskInput
$env:ROUTER2API_Auth__ApiKey__Key = Read-Host '新的下游 API Key' -MaskInput
$env:ROUTER2API_Redis__ConnectionString = 'localhost:6379'
dotnet run --project src/Router.Host/Router.Host.csproj --no-build --no-launch-profile --urls http://127.0.0.1:5242
```

访问 `http://127.0.0.1:5242`。模型接口使用 Bearer API Key；管理入口使用管理员 Cookie/CSRF，二者不能互换。

**配置文件 `Config/Config.json` 按进程工作目录读取，优先级高于 User Secrets 和环境变量。** 首次启动会生成配置，并迁移管理员密码为哈希；后续修改环境变量不会覆盖已有 Config。先确认实际配置路径，使用独立测试数据库/Redis，不要直接带入生产账号。

`UserSecretsId` 是 `Router2API-Public`，与原开发环境隔离。示例配置在 `src/Router.Host/Config/Config.example.json`；复制后必须自己填写密码和密钥。实际配置、数据库及运行日志均被 Git 忽略。

## 安装插件

插件目录固定为运行程序的 `AppContext.BaseDirectory/plugins/<pluginKey>`：

- 本地 Debug 通常是 `src/Router.Host/bin/Debug/net10.0/plugins`；
- 发布程序是发布目录下的 `plugins`；
- 容器是 `/app/plugins`，Compose 映射到 `volumes/plugins`。

宿主启动和手动重载时会检查并创建 `plugins/` 与 `plugins/.subscription/`；已有的 `plugins/subscription/` 订阅目录会在启动时迁移，订阅数据文件在首次添加仓库时写入。

在对应插件仓库构建完整发行包，再复制到上述目录并从管理页重载：

```text
plugins/
  forwardapi/           C#：Plugins.ForwardAPI.dll + 对应 deps.json
  js-forwardapi/        JS：plugin.json + server/plugin.mjs + ui/index.html
```

不要复制源码、node_modules、账号数据或多个插件混合的 bin 目录。初次安装复制新目录；升级先备份完整旧包，再安排维护窗口替换/重载。失败重载是否保留旧版本需检查实际状态和日志，不能只看 HTTP 200。

插件管理页现可添加公开 GitHub 仓库（例如 `NNNNolan/Rouer-Plugins-js` 或 `NNNNolan/Rouer-Plugins-Csharp`），选择 Release 版本和其中的部分插件下载安装；“插件更新”页签可逐项选择要更新的已订阅插件。订阅记录保存于运行目录的 `plugins/.subscription/subscriptions.json`，不进数据库。插件卡片展示简介、运行状态，并提供启用、禁用和删除操作。发行索引格式见 [插件发行索引](sdk/PLUGIN-RELEASES.md)。手工安装方式仍可用于没有发行索引的插件；C# 手工包的描述优先读取包内 `plugin.json`，再读取 DLL 的程序集描述，最后尝试入口类型的 XML 文档摘要。JS 手工包读取 `plugin.json` 的 `description`。

## Docker 安装

镜像地址：[hhhhzy/router2api:latest](https://hub.docker.com/r/hhhhzy/router2api)。支持 **Linux amd64 和 arm64**，Docker 会自动选择与宿主机匹配的架构。以下两种安装方式任选其一；安装需要 Docker，Compose 方式还需要 Docker Compose v2 或更新版本。下面的安装命令使用 Bash。

### 使用 Docker Compose

将 [docker-compose.yml](docker-compose.yml) 和 [.env.example](.env.example) 放在同一目录；已克隆仓库时可直接在仓库根目录操作。首次安装先创建 `.env`：

```bash
cp -n .env.example .env
```

编辑 `.env`，填写 `ROUTER_ADMIN_PASSWORD` 和 `ROUTER_API_KEY`，管理员用户名由 `ROUTER_ADMIN_USERNAME` 指定，默认 `admin`。已有 `.env` 请保留；密码和 API Key 为空时 Compose 会拒绝启动。

```bash
docker compose pull
docker compose up -d
docker compose logs -f router2api
```

Compose 会拉取 `hhhhzy/router2api:latest` 和 Redis 镜像，并创建网络与持久化存储。

### 直接使用 docker run

在准备存放数据的目录中创建网络、Redis 数据卷和宿主目录，然后启动 Redis：

```bash
mkdir -p volumes/data volumes/plugins volumes/Config
docker network create router2api
docker volume create router2api-redis

docker run -d \
  --name router2api-redis \
  --network router2api \
  --network-alias redis \
  --restart unless-stopped \
  -v router2api-redis:/data \
  redis:7-alpine redis-server --appendonly yes
```

输入管理员密码和下游 API Key，启动宿主。管理员用户名默认 `admin`，可以修改下面的 `Username` 参数；重建已有容器时沿用原来的凭据。

```bash
read -r -s -p '管理员密码: ' ROUTER_ADMIN_PASSWORD
echo
read -r -s -p '下游 API Key: ' ROUTER_API_KEY
echo

docker run -d \
  --name router2api \
  --network router2api \
  --restart unless-stopped \
  -p 127.0.0.1:5242:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e TZ=Asia/Shanghai \
  -e ROUTER2API_Database__Path=/app/data/router2api.db \
  -e ROUTER2API_Redis__ConnectionString=redis:6379 \
  -e ROUTER2API_Redis__InstanceName=router2api: \
  -e ROUTER2API_Auth__Admin__Users__0__Username=admin \
  -e "ROUTER2API_Auth__Admin__Users__0__Password=${ROUTER_ADMIN_PASSWORD:?请先输入管理员密码}" \
  -e "ROUTER2API_Auth__ApiKey__Key=${ROUTER_API_KEY:?请先输入下游 API Key}" \
  -v "$(pwd)/volumes/data:/app/data" \
  -v "$(pwd)/volumes/plugins:/app/plugins" \
  -v "$(pwd)/volumes/Config:/app/Config" \
  hhhhzy/router2api:latest

docker logs -f router2api
```

### 访问与持久化

启动后访问 `http://127.0.0.1:5242`，使用配置的管理员账号登录。端口默认只绑定主机回环地址；远程部署可通过 SSH 隧道或反向代理访问，对外服务应启用 HTTPS。

| 宿主机目录 / Docker 数据卷 | 容器目录 | 内容 |
| --- | --- | --- |
| `./volumes/data` | `/app/data` | SQLite 数据库 |
| `./volumes/plugins` | `/app/plugins` | 已安装插件及订阅记录 |
| `./volumes/Config` | `/app/Config` | 宿主配置 |
| Redis 命名卷 `router2api-redis`（Compose 会添加项目前缀） | `/data`（Redis 容器） | Redis 持久化数据 |

首次启动会生成 `volumes/Config/Config.json`，其中已有的同名配置项优先于环境变量。更新容器时保留上述目录和数据卷，直接使用 `docker run` 时也应沿用相同的工作目录。镜像只包含宿主；启动后可按[安装插件](#安装插件)添加提供方插件。

### 更新镜像

使用 Compose 安装时，在原目录执行：

```bash
docker compose pull
docker compose up -d
```

直接使用 `docker run` 安装时，先拉取镜像并停止、删除旧宿主容器，再执行上面的宿主 `docker run` 命令，沿用原凭据和挂载目录：

```bash
docker pull hhhhzy/router2api:latest
docker stop router2api
docker rm router2api
```

## 发布与多平台构建

推送形如 `v2.0.1` 的 tag 时，[宿主 Release 工作流](.github/workflows/release-host.yml)会构建并推送 **Linux amd64 和 arm64** 多平台镜像到 Docker Hub 的 `hhhhzy/router2api`，成功后创建同名 GitHub Release。标题为 `Router2API v2.0.1`，通过 `generate_release_notes: true` 自动生成发布说明。

首次发布前，在 GitHub 仓库 **Settings → Secrets and variables → Actions** 配置：

| 类型 | 名称 | 内容 |
| --- | --- | --- |
| Variables（也支持 Secrets） | `DOCKERHUB_USERNAME` | Docker Hub 用户名，本仓库为 `hhhhzy` |
| Secrets | `DOCKERHUB_TOKEN` | 对目标 Docker Hub 仓库具有读写权限的 Access Token |

正式 tag `v2.0.1` 会发布镜像标签 `2.0.1` 和 `latest`；预发布 tag（如 `v2.1.0-rc.1`）只发布对应版本镜像，并创建 GitHub 预发布版本。tag 必须是 `v` 开头的合法 SemVer 版本。

[Dockerfile](Dockerfile) 参考官方 [aspnetapp Alpine 多平台构建示例](https://github.com/dotnet/dotnet-docker/blob/nightly-UpdateDependencies-nightly-From-dotnet-dotnet-11.0/samples/aspnetapp/Dockerfile.alpine)，使用 `sdk:10.0-alpine` 和 `aspnet:10.0-alpine`。前端和 .NET SDK 在构建机架构上运行，`restore`、`publish` 都通过 `TARGETARCH` 选择目标架构（`linux-musl-x64` / `linux-musl-arm64`）。ICU 和时区数据从目标架构的官方 `runtime-deps:10.0-alpine-extra` 镜像复制，所有 `RUN` 都位于构建机架构的阶段，无需 QEMU。

也可以用具有仓库推送权限的账号执行 `docker login`，再用 Docker Buildx 手动构建并推送：

```powershell
docker buildx create --name router2api-builder --driver docker-container --use
docker buildx build --platform linux/amd64,linux/arm64 -t hhhhzy/router2api:2.0.1 --push .
```

独立的 Contracts 工作流会将 `src/Router.Contracts` 打成包含 DLL 的 NuGet 包并发布到 nuget.org。NuGet 发布使用 [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)：工作流通过 GitHub OIDC 获取临时发布凭据，无需保存长期 API key。

首次发布前，在 nuget.org 登录目标包所有者账号，确认 `Router.Contracts` 包 ID 可用，并在 **Trusted Publishing** 中新增 GitHub 策略：Policy Name 可填 `Router2API-Contracts`（仅用于识别策略），Repository Owner 填 `NNNNolan`，Repository 填 `Router2API`，Workflow File 只填 `publish-contracts.yml`，Environment 留空；Scopes 允许发布新包和新版本，**Glob Patterns and Packages** 单独一行填 `Router.Contracts`（包 ID，不带版本号或通配符）。在 GitHub 仓库 **Settings → Secrets and variables → Actions → Variables** 新增仓库变量 `NUGET_USER`，值为该 nuget.org 账号的用户名（不是邮箱）；若已把它放在同页面的 **Secrets** 中，工作流也可读取。不要只设置在未被此任务使用的 GitHub Environment 下。策略的所有者与 `NUGET_USER` 对应；此值不是发布密钥。

包版本取 tag 去掉开头 `v` 后的部分；tag 应使用合法的 NuGet 版本号。已有的 `v2.0.0` tag 不会因工作流修改自动重跑。请在 Actions 中从 `main` 点击 **Run workflow** 发起新运行，`version` 填 `2.0.0`，`nuget_user` 填 nuget.org 用户名；工作流会检出对应的 `v2.0.0` tag 并发布同版本到 nuget.org。不要在旧运行中点 **Re-run jobs**，因为重试仍使用旧运行关联的工作流提交。nuget.org 同一包 ID 和版本只能发布一次。此 NuGet 工作流只打包 Contracts，不发布宿主或插件。

从源码发布宿主到本机目录：

```powershell
pnpm --dir web build
dotnet publish src/Router.Host/Router.Host.csproj -c Release -o artifacts/host
```

## 验证

```powershell
dotnet test tests/Router.Tests/Router.Tests.csproj --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
pnpm --dir web test
pnpm --dir web build
```

宿主测试中的 `Fixtures/js-proxy-demo` 是离线夹具，不随宿主安装。C# ForwardAPI 测试归对应仓库，JS 业务只做 Node 模拟测试。宿主不依赖提供方项目，也无默认 forwardapi 代理测试、专属日志脱敏例外或专属协议目录抓取；公共模型能力仅从 models.dev 获取。

## 开源许可与边界

本仓库采用 [MIT License](LICENSE)，允许商业使用、修改和分发；复制或分发代码时须保留版权声明和许可声明。软件按“原样”提供，不提供任何担保，完整条款见许可证文件。

第三方依赖及参考代码仍遵循各自的许可证，相关版权和许可声明应予以保留。独立插件仓库的授权以各自仓库声明为准，不因本仓库采用 MIT 而变更。

Jint 不是 OS 沙箱，原生 DLL 更不是安全沙箱；只安装审阅过的可信插件。
