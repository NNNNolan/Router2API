# Router2API

.NET 10 模型路由宿主，支持 C# DLL 和 Jint JavaScript 插件，附 Vue 3 管理后台。

**本仓库只发布宿主，不捆绑提供方插件。** 账号、代理池、重试、公共协议、SSE 生命周期、任务和插件加载由宿主管理；具体上游业务从独立插件仓库安装。

| 仓库 | 职责 |
| --- | --- |
| `Router2API` | 本仓库：Contracts、Infrastructure、Host、管理前端和宿主测试 |
| `Rouer-Plugins-js` | js-forwardapi、JS SDK/教程和 Node 模拟测试 |
| `Rouer-Plugins-Csharp` | 仅 ForwardAPI、C# SDK 教程和对应测试 |

仓库之间不需要 Git submodule。配置统一使用 `ROUTER2API_`，页面桥为 `window.Router2API`，不再读取旧配置前缀。升级已有环境需同步修改环境变量/页面桥；现有 Config 文件与数据路径不会自动搬迁。

## 阅读入口

- [SDK 与插件边界](sdk/README.md)
- [宿主完整运行流程](sdk/HOST-LIFECYCLE.md)
- [AI 开发工作单](sdk/AI-DEVELOPMENT.md)
- [公开前与部署安全检查](SECURITY.md)

## 本地开发

需要 .NET 10 SDK、PowerShell 7、Node 22/24 LTS 和项目声明版本的 pnpm。
运行 jsdom 30 页面测试时，Node 需满足 `^22.22.2 || ^24.15.0 || >=26.0.0`；建议直接使用对应 LTS 的最新补丁版。

```powershell
# 仓库根目录。corepack 使用 web/package.json 指定的 pnpm 版本。
corepack enable
pnpm --dir web install --frozen-lockfile
pnpm --dir web build

dotnet build Router2API.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
```

前端源代码在 `web`，构建产物不提交。宿主构建会把 `web/dist` 复制到 `wwwroot`，包括首次 clone 的构建；Docker 也从源码构建前端。不安装任何插件时，管理后台仍可启动，但没有提供方模型。

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

在对应插件仓库构建完整发行包，再复制到上述目录并从管理页重载：

```text
plugins/
  forwardapi/           C#：Plugins.ForwardAPI.dll + 对应 deps.json
  js-forwardapi/        JS：plugin.json + server/plugin.mjs + ui/index.html
```

不要复制源码、node_modules、账号数据或多个插件混合的 bin 目录。初次安装复制新目录；升级先备份完整旧包，再安排维护窗口替换/重载。失败重载是否保留旧版本需检查实际状态和日志，不能只看 HTTP 200。

## 发布与 Docker

推送形如 `v2.0.0` 的 tag 时，GitHub Actions 会将 `src/Router.Contracts` 打成包含 DLL 的 NuGet 包 `Router.Contracts.2.0.0.nupkg`，发布到本仓库所有者的 GitHub Packages。包版本取 tag 去掉开头 `v` 后的部分；tag 应使用合法的 NuGet 版本号。工作流使用 GitHub 自动提供的 `GITHUB_TOKEN`，无需另建发布密钥。发布只打包 Contracts，不发布宿主或插件。

```powershell
pnpm --dir web build
dotnet publish src/Router.Host/Router.Host.csproj -c Release -o artifacts/host

# Docker：复制示例后编辑自己的 .env，Compose 会拒绝空密码/密钥。
Copy-Item -LiteralPath .env.example -Destination .env
docker compose up -d --build
```

不要覆盖已有 `.env`。容器启动不会从其他路径自动覆盖已安装插件，也不会在线下载插件。默认只绑定本机 `127.0.0.1:5242`，对外服务应使用经过审阅的反向代理与 HTTPS。

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
