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
- [Docker 更新](#更新镜像)
- [SDK 与插件边界](sdk/README.md)
- [宿主完整运行流程](sdk/HOST-LIFECYCLE.md)
- [AI 开发工作单](sdk/AI-DEVELOPMENT.md)
- [公开前与部署安全检查](SECURITY.md)

模型映射的每个目标可选择「从 /v1/models 隐藏原模型」。仅启用的映射及可用目标生效，只隐藏该目标的原始模型 ID（如 `pluginA/ds4`），映射名仍显示，原始模型的直接调用不受影响；未设置时保持显示。

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

### 宿主版本

当前源码默认版本为 **2.0.8**，在 `src/Router.Host/Router.Host.csproj` 的 `Version` 中维护。管理后台通过需管理员会话的 `GET /api/admin/version` 读取实际运行的宿主版本，并显示在桌面侧栏和移动端菜单底部；前端不再写死版本号。

Docker Release 构建会将 tag 对应版本通过 `HOST_VERSION` 构建参数写入宿主。手动构建可用 `-p:HostVersion=2.0.8`，手动 Docker 构建可用 `--build-arg HOST_VERSION=2.0.8`；未指定时使用源码默认值。该版本独立于 Contracts 和插件版本，只有更换并启动新宿主后，页面才会显示新版本。

2.0.8 面向长时间推理与超大上下文的插件代理：JS 插件 `attemptTimeoutSeconds` 与 `ctx.http.open` 的 `timeoutMs` 上限放宽到 300 秒（单次尝试建立响应仍受总 setup 预算约束），桥接输入上限 16 MiB、单次外发请求体上限 4 MiB，超限返回 413 而不记为插件故障；JS 引擎内存预算提升到 128 MiB 以容纳大输入解析。请求日志新增 `Cancelled` 标记：流式响应确认送达完成标记（如 `data: [DONE]`）后的调用方断开不再误记为取消。

2.0.5 支持插件显式声明的 `Anonymous` 公开端点，以及 C# 插件的字符串 HTML 响应；现有管理端点仍要求管理员会话和 CSRF，不开放 `Internal`。匿名写请求限 JSON 并校验浏览器同源性，HTML 响应禁止缓存/嵌入。插件需自行提供限时业务授权和防滥用措施。更新宿主前请审查已安装插件的 `Anonymous` 声明：此前这些声明仍被管理认证拦截，新版将真正公开。该变化不升级 Contracts 2.0 或 JS Host API 1，已有插件无需重编；详见 [端点与页面边界](sdk/HOST-LIFECYCLE.md#8-管理页面与请求桥)。

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

在「系统 → 配置中心 → 界面主题」选择黑白极简主题（默认）或蓝色主题，保存后当前页面立即应用，其他浏览器刷新后同步，登录页也使用同一主题。设置持久化为 `Appearance.Theme`（`minimal` 为黑白，`default` 为蓝色）。未配置主题时默认黑白，已保存的主题选择保持不变。两套主题均支持右上角账户前的日间/黑夜切换，明暗模式保存为 `Appearance.ColorMode`（`light` / `dark`），刷新和重启后保留。公开接口 `GET /api/appearance` 仅返回主题和明暗模式，修改仍需管理员会话与 CSRF 校验。

插件管理页面会跟随宿主黑夜模式：宿主在 sandbox iframe 内注入样式，适配常见浅色底、深色字、表单与表格，保留状态色和图片；切回日间恢复原样式，切换不重载页面。Canvas、图片中的颜色、封闭 Shadow DOM 和特殊主题控件仍需插件适配。

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

### 上传本地插件 ZIP

插件管理页点击「上传插件 ZIP」，选择文件后点击「上传、覆盖并加载」。支持 C# 和 JS 的两种单插件包结构：

```text
任意名称.zip                  任意名称.zip
└─ 任意顶层目录/              ├─ plugin.json（JS 必需）
   ├─ plugin.json            ├─ *.dll / server/plugin.mjs
   └─ *.dll / server/...     └─ 其他插件文件
```

插件 ID 取自 JS 清单或 C# 程序集的 `PlatformAdapter.PluginKey`，不依赖 ZIP 文件名；C# 可不带 `plugin.json`，允许附带依赖 DLL。每包只能包含一个插件。
同 ID 包会**完整替换**（不会保留旧包残余文件），并且只重新加载该插件；原来禁用的插件也会启用。不触碰账号数据库或其他插件。校验/加载失败会恢复旧文件和旧状态；插件自身启动过程中对外部系统产生的副作用不能回滚。
本地覆盖订阅插件后会解除该插件的发行版关联，仓库订阅保留。如需恢复仓库更新，请从订阅仓库重新安装。

上传上限 100 MiB，解压上限 300 MiB、2000 个条目；拒绝路径穿越、链接和重复路径。管理接口为 `POST /api/admin/plugins/upload`，使用 multipart `file` 字段，沿用管理员会话、Origin 和 CSRF 校验。反向代理的请求体大小限制也需允许所上传的文件。
**上传即允许执行插件代码，只安装可信来源的插件。** 上传验证不等于安全沙箱。

插件管理页现可添加公开 GitHub 仓库（例如 `NNNNolan/Rouer-Plugins-js` 或 `NNNNolan/Rouer-Plugins-Csharp`），选择 Release 版本和其中的部分插件下载安装；“插件更新”页签可逐项选择要更新的已订阅插件。订阅记录保存于运行目录的 `plugins/.subscription/subscriptions.json`，不进数据库。插件卡片展示简介、运行状态，并提供启用、禁用和删除操作。发行索引格式见 [插件发行索引](sdk/PLUGIN-RELEASES.md)。手工安装方式仍可用于没有发行索引的插件；C# 手工包的描述优先读取包内 `plugin.json`，再读取 DLL 的程序集描述，最后尝试入口类型的 XML 文档摘要。JS 手工包读取 `plugin.json` 的 `description`。

禁用插件后，对应的桌面和移动端导航菜单同步隐藏，重新启用并加载成功后恢复；没有主页面的插件不生成菜单，仍可从“插件管理”中启用或禁用。

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

更新需要**拉取新镜像并重建宿主容器**，仅执行 `docker restart` / `docker compose restart` 不会切换到新镜像。更新会短暂中断服务，建议在没有进行中的模型请求时操作。以下命令使用 Bash。

更新前注意：

- 备份原部署的配置、账号数据库和插件目录；Compose 用户还应保留原 `docker-compose.yml`、`.env` 和项目名。复制 SQLite 数据文件前先停止宿主，避免备份到不一致的数据。
- 继续使用原来的 `volumes/data`、`volumes/plugins`、`volumes/Config` 和 Redis 数据卷。**不要删除 `volumes/`，不要执行 `docker compose down -v` 或清理 Redis 数据卷。**
- 不要用示例文件覆盖现有 `.env` 或 `volumes/Config/Config.json`。已保存的 Config 配置优先于环境变量，更新镜像不是重置密码或 API Key。

#### Docker Compose 更新

在原 `docker-compose.yml` 和 `.env` 所在目录执行；如果部署时指定了 `-p` 项目名或 `-f` 配置文件，更新时沿用相同参数：

```bash
# 只拉取宿主的新镜像
docker compose pull router2api

# 拉取成功后重建宿主，不重启 Redis
docker compose up -d --no-deps router2api

# 检查容器状态和启动日志
docker compose ps router2api
docker compose logs --tail=100 router2api
```

`up -d` 检测到镜像变化后会自动重建容器，无需先执行 `down`。如果拉取失败，先解决网络或镜像访问问题，不要停止当前可用的容器。

#### docker run 部署：使用 Watchtower 更新

对于前面通过 `docker run --name router2api` 启动的宿主，使用 Watchtower 一次性检查并更新，无需手动停止、删除容器或重新填写启动参数。请先确认 `router2api` 容器正在运行；如果部署时使用了其他容器名，将命令末尾的 `router2api` 改为实际名称。

```bash
docker run --rm \
  -v /var/run/docker.sock:/var/run/docker.sock \
  containrrr/watchtower:latest \
  --run-once router2api
```

- `--run-once` 只检查一次，发现同一镜像标签有更新时拉取镜像并重建宿主；没有更新则保持原容器。完成后临时 Watchtower 容器自动删除，不会常驻定时更新。
- 指定 `router2api` 后只更新宿主，不更新 Redis 或其他容器。**不要省略末尾的容器名，以免更新范围扩大到其他运行中的容器。**
- Watchtower 会复用原容器的环境变量、端口、网络和数据挂载；仍需保留并备份原来的配置、数据库和插件目录。
- **安全提示：挂载 Docker socket 会授予 Watchtower 控制宿主 Docker 的高权限，只使用可信的 Watchtower 镜像。** 上述 socket 路径适用于常见的 Linux Docker Engine 部署。

执行结束后检查宿主状态和启动日志：

```bash
docker ps -a --filter name=router2api
docker logs --tail=100 router2api
```

#### 更新后检查

- 打开管理后台，确认账号、插件及模型列表正常，并测试一次模型请求。
- 新镜像已包含前端，无需另行打包；若页面仍显示旧内容，使用 `Ctrl+F5` 强制刷新。
- 宿主镜像更新**不会自动更新已安装插件**。插件版本需在“插件管理 → 插件更新”中单独更新，并确认与宿主兼容。
- Watchtower 只更新容器当前使用的镜像标签，不会把固定版本自动切换到其他版本。若要更换固定版本，需修改 Compose 的 `image` 后更新，或按[启动宿主命令](#直接使用-docker-run)用目标已发布版本重建容器，沿用原配置和挂载。

## 代理订阅刷新周期

管理后台「代理」中的订阅刷新周期支持正整数加单位：`30S`（秒）、`30M`（分钟）、`2H`（小时），单位不区分大小写，最小 `1S`，默认 `1H`。
宿主每秒检查到期订阅，刷新串行执行；网络请求、排队和节点测速可能使实际刷新晚于设置周期，不保证精确定时。失败后自动刷新至少退避 1 分钟，仍可手动刷新。
升级启动时会新增秒数字段并将旧分钟值乘以 60，不改变已有订阅周期；旧分钟字段和 Contracts 属性保留兼容，读取秒级周期时向上取整，新前端使用 `refreshIntervalSeconds`。

## 模型映射

管理后台「模型 → 模型映射」可创建对外模型名（例如 `ds4`），添加插件 / 平台 / 插件内模型，并用上移、下移设置调用顺序。客户端在 `/v1/chat/completions`、`/v1/completions`、`/v1/responses` 或 `/v1/messages` 中直接传 `"model": "ds4"`；原有 `平台/模型` 调用方式继续可用。

映射编辑使用一屏内的限高弹窗，目标较多时内容区滚动，保存和取消按钮保持可见。对外模型名为空时，首次选择插件模型会自动填写最后一个 `/` 后的名称，例如 `cn/deepseek-v4-flash` 填为 `deepseek-v4-flash`；不含 `/` 则直接复制，已有名称不覆盖，目标的实际模型 ID 保持完整。

- 每次请求从第一个启用的目标开始，成功即返回。未加载、停用或插件归属不匹配的目标会跳过；没有可用账号时进入下一目标。
- 每个目标可独立设置「连续失败轮数」与「冷却时间」，默认 3 轮、5 分钟，时间支持小时、分钟、秒；轮数或时间任一为 `0` 就不自动冷却。API / 存储字段为 `failureThreshold` 与 `cooldownSeconds`，旧配置缺省时使用默认值。达到该目标设定的轮数后，在设定时长内跳过它并继续后续目标，到期自动恢复。每轮包含该目标配置的内部重试；成功清零，400/413/415/422 参数错误与调用方取消不计数。流式请求以完整响应结束结果为准，流中断只影响后续请求的冷却，不在输出后切换目标。所有目标均冷却时返回 503。更改目标冷却规则会清空其旧计数与冷却；冷却状态位于宿主内存，重启清空，其他映射与显式 `平台/模型` 调用不受影响。
- 每个目标的「尝试次数（含首次）」表示总调用上限，范围为 `1–35`；`1` 表示只调用一次，`3` 表示首次加最多两次重试，留空继承插件策略。沿用原 API / 存储字段名 `retryCount`，正数直接按总次数执行，旧值 `0` 读取或保存时转换为 `1`。映射值只覆盖本次调用，不修改插件全局配置，也不与插件默认次数相乘。是否重试仍由插件的显式决策决定；请求参数错误、调用方取消及不可重放的失败不会自动切换目标。
- 多个目标共用宿主 `Plugins:Execution:TotalSetupTimeout` 建立响应预算，同时保留各插件较短的尝试/总建立响应超时。返回标准流或原始流后不再切换，流中断会按现有协议报错；原始响应保持透传。
- 每个目标使用独立请求，原始 JSON 只替换顶层 `model`，其他字段及大整数保持原值。请求日志保留对外名称和最后调用的平台，各次尝试的原因带有插件 / 平台 / 实际模型，便于追踪切换。
- 映射保存在 SQLite 的 `model_mappings` 表，保存后新请求立即生效，进行中的请求继续使用原快照。`/v1/models` 增加已启用且存在可用目标的映射名称。停用映射后调用该名称返回 404，不回退到单平台默认路由。

对外模型名支持 1–128 个英文字母、数字及 `. _ - :`，不含 `/` 且不区分大小写。每条映射最多 32 个目标，同一插件 / 平台 / 模型不可重复。管理 API 为 `GET/POST /api/admin/model-mappings`、`PUT/DELETE /api/admin/model-mappings/{id}`，沿用管理员会话、Origin 和 CSRF 校验；不升级 Contracts 或 JS Host API。

## 模型图标与用量分析

管理后台使用顶部导航与分组菜单，移动端保留抽屉导航；插件启停会同步更新两端入口。「总览」下包含总览和用量分析，「日志」集中提供审计日志、插件日志和任务日志。模型广场支持搜索、平台筛选、分页与刷新，卡片随屏幕宽度按五、四、三、二、一列排列，1440px 及以上一行显示五个模型。卡片按内容紧凑排列，平台显示在右上角；底部分页默认每页 20 个，可切换为 40 或 80 个。

账号管理及日志表格页填满顶栏下方的实际剩余空间，表格内容在内部滚动，分页保持可见；手机端可折叠筛选，分页控件按宽度自动换行。

模型品牌按 ID 优先、显示名称补充识别，移除当前转发平台前缀后匹配模型族和组织名。图标使用 [LobeHub Icons](https://github.com/lobehub/lobe-icons) 的静态 SVG 包按需打包，保留品牌原色，不依赖运行时 CDN。未知品牌或图片加载失败时显示最多两字符缩写，DeepSeek 固定显示 `DS`。

总览、用量分析和请求日志的 Token 数据来自同一份持久化请求日志。采集优先级为插件显式用量、正文/SSE 用量、宿主估算；合法零值保留，累计快照覆盖而不相加，分阶段上报的缓存和推理用量独立合并。输入、输出和总量来源分别记录，合法上游总量不强制改写为输入与输出之和。

总览的今日请求、累计成功率、平均延迟和平台请求明细由已保留的请求日志聚合，宿主重启后仍可显示；进行中请求取实时进程状态，最近一分钟统计已完成请求。`GET /api/admin/dashboard` 接收浏览器时区确定“今日”，默认 `Asia/Shanghai`；原有 `/api/admin/metrics` 和指标流仍返回进程实时快照。

已知模型编码使用 `Microsoft.ML.Tokenizers` 与本地 `cl100k_base` / `o200k_base` 词表；未知模型和缺少用量的流式输出使用有状态 Unicode 估算，记录算法版本。估算不能代替上游账单；无法计量的多模态内容、取消和写出失败会标记不完整。旧日志缺少来源元数据时显示“来源未知”，启动会增量增加可空字段，不回填虚构来源。

## 测试窗口响应

测试窗口展示所选 `/v1` 接口的完整响应正文，包括非 2xx 错误、纯文本和空正文；不提取回答、不渲染 Markdown。合法 JSON 使用两空格缩进，并将 `\u` 转义的中文显示为文字，保留数字原文及字段顺序；非 JSON 保持原样。HTTP 状态码和耗时单独展示，HTML 作为文本显示，不执行。这里展示的是宿主 API 返回内容，不是绕过宿主协议转换后的上游原始数据。

## 验证

宿主 GitHub Release 由 Release Drafter 按合并 PR 的 Conventional Commit 类型归类，合并到 `main` 后自动更新发布草稿；推送 `v*` 标签并完成镜像构建后，release job 会发布该说明。说明包含新增功能、问题修复、性能与体验优化、维护文档、Docker 镜像平台和升级提示。PR 标题建议使用 `feat:`、`fix:`、`perf:`、`docs:` 等标准类型；分类及说明模板见 [Release Drafter 配置](.github/release-drafter.yml)。

```powershell
dotnet test tests/Router.Tests/Router.Tests.csproj --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
pnpm --dir web test
pnpm --dir web build
```

宿主测试中的 `Fixtures/js-proxy-demo` 是离线夹具，不随宿主安装。C# ForwardAPI 测试归对应仓库，JS 业务只做 Node 模拟测试。宿主不依赖提供方项目，也无默认 forwardapi 代理测试、专属日志脱敏例外或专属协议目录抓取；公共模型能力仅从 models.dev 获取。

### 分配与并发性能回归

保持 `Router.Contracts` 和 Host API 不变。分配预算覆盖预热后的指定热路径，不代表 SqlSugar、Jint、网络请求或整个宿主进程零分配。

```powershell
dotnet test tests/Router.Tests/Router.Tests.csproj -c Release --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false --filter 'TestCategory=Performance' --logger 'console;verbosity=detailed'
```

测试使用同步内存输入和 `GC.GetAllocatedBytesForCurrentThread()`，测量区间禁止跨线程；网络与 Redis 的异步分配需另用进程级 profiler 测量。预算与夹具见 `AllocationBudgetTests` 和 `JsStreamingTests`，修改夹具后需要重新测量。

| 路径 | 回归预算 | 当前本地 Release 样本 |
| --- | --- | --- |
| 字段名比较、推理等级选择、策略读取、结束标记扫描、指标计数、Chat JSON 编码 | 0 B/op | 0 B/op |
| raw 读取及结束标记扫描 | 分配不随 16 KiB 数据块数量增长 | 16 KiB / 1 MiB 均为 456 B/流 |
| SSE 小事件分帧 | 增量 ≤112 B/帧 | 96 B/帧 |
| Chat SSE 编码及写入 `Stream.Null` | 0 B/帧 | 0 B/帧 |
| OAuth 凭据解码 | ≤2048 B/次 | 904 B/次 |
| Jint SSE 映射，含完整标准 chunk 的 JSON 往返 | ≤36 KiB/帧 | 32504 B/帧 |

raw 内部块借用本流的池化缓冲区，有效期到下一次 `MoveNextAsync` 或枚举器释放；宿主必须等待下游写入完成再推进，不得缓存这些块。归还缓冲区时清除内容。SSE 帧、字符串和 JS JSON 仍有分配，JS state 大小、chunk 数量及执行预算继续强制检查。

并发回归覆盖八个 worker 的 raw 内容隔离、HTTP 客户端租约及账号 InFlight 平衡。模型发现把可能同步阻塞的插件调用放到受限 worker 中，同一终端最多一个真实查询在途；调用方超时不提前释放该查询的客户端，也不因重复刷新累积阻塞 worker。共享 HTTP 传输池仍按连接配置隔离，账号、客户端和 Jint Engine 不跨请求复用。

## 开源许可与边界

本仓库采用 [MIT License](LICENSE)，允许商业使用、修改和分发；复制或分发代码时须保留版权声明和许可声明。软件按“原样”提供，不提供任何担保，完整条款见许可证文件。

第三方依赖及参考代码仍遵循各自的许可证，相关版权和许可声明应予以保留。独立插件仓库的授权以各自仓库声明为准，不因本仓库采用 MIT 而变更。

Jint 不是 OS 沙箱，原生 DLL 更不是安全沙箱；只安装审阅过的可信插件。
