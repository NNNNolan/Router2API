# 给 AI 的插件开发工作单

这份文档同时供人审查 AI 的产出。目标不是“生成看起来像插件的代码”，而是交付**符合当前契约、能构建/加载、资源正确回收、业务行为有证据**的插件。

入口：[SDK 总览](README.md) · JS 开发教程（参见 `Rouer-Plugins-js/sdk/js/DEVELOPMENT.md`） · C# 开发教程（参见 `Rouer-Plugins-Csharp/sdk/csharp/DEVELOPMENT.md`） · [宿主运行流程](HOST-LIFECYCLE.md)。

## 1. 接到任务后先确认范围

把需求写成下面这张工作单。能从现有代码确认的就查代码；只有会改变业务/安全边界的歧义才问操作者。

| 项目 | 必须确认 |
| --- | --- |
| 交付语言 | 新 Jint JS/TS、维护 C#，还是逐项迁移某个 C# 提供方？ |
| 身份 | 包目录/`pluginKey`、平台名、模型内部 ID，是否为多平台包 |
| 改动范围 | 只改插件，还是明确授权修改 Contracts/宿主？是否需要页面？ |
| 业务行为 | 凭证类型、刷新、模型发现、协议、选号、余额/积分、任务 |
| 外部访问 | 哪些真实 origin、何种方法、模型 attempt 还是独立 pool/direct |
| 重试安全性 | 上游是否幂等，失败后是否可能已扣费，允许重放哪一步 |
| 验收环境 | 独立数据库/Redis/插件目录、测试账号、模拟上游 |
| 发布权限 | 只交付代码，还是另行授权安装/重载/提交/推送？ |

如果用户只要求文档，就不要顺手修改运行时业务或连接真实上游。如果要求实现，不要停在设计稿；在授权范围内完成代码、必要测试和使用说明。

仓库可能已有其他工作的未提交修改。先 `git status --short`，保留这些改动；不要 reset、stash、覆盖整文件来“恢复干净”。本项目本地命令默认使用 PowerShell 7。

## 2. 固定阅读顺序与事实来源

1. 读本目录总览、对应语言教程，理解开发工具、后端插件、前端 iframe 是三个环境。
2. 读当前契约：
   - JS：带中文注释的 `index.d.ts`（参见 `Rouer-Plugins-js/sdk/js/index.d.ts`）、API 手册（参见 `Rouer-Plugins-js/sdk/js/API.md`）、能力对照（参见 `Rouer-Plugins-js/sdk/js/CAPABILITIES.md`）。
   - C#：[PipelineContracts](../src/Router.Contracts/Pipeline/PipelineContracts.cs)、[PluginContracts](../src/Router.Contracts/Plugins/PluginContracts.cs)、[PluginServices](../src/Router.Contracts/Host/PluginServices.cs)、[响应模型](../src/Router.Contracts/Domain/Models.cs)。
3. 读 [宿主运行流程](HOST-LIFECYCLE.md)，沿相关源码链接追到实际实现，而不只看接口名称。
4. 若迁移提供方，读现有插件**全部相关 partial 文件、调用者和测试**：
   - ForwardAPI（参见 `Rouer-Plugins-Csharp/src/Plugins.ForwardAPI/`）：动态站点、允许表、原始 JSON/响应字节。
5. 设计说明只作参考；以当前公开契约与测试为准。

**可信顺序：当前实现和契约 → 可运行测试 → 本 SDK 文档 → 历史方案。** 发现冲突要记录并修正，不要通过编造 `ctx.fetch`、`ctx.db`、`ctx.services` 等不存在的接口来补齐。

### 常见问题应该追到哪里

| 问题 | 实现入口 |
| --- | --- |
| 包为什么不加载/旧版本为什么还在 | [PluginCatalog](../src/Router.Host/Plugins/PluginCatalog.cs)、[JintPackageLoader](../src/Router.Host/Plugins/JintPackageLoader.cs)、[DotNetPackageLoader](../src/Router.Host/Plugins/DotNetPackageLoader.cs) |
| 某个 `ctx` 调用被拒绝 | [JsCapabilityDispatcher](../src/Router.Host/Plugins/JavaScript/JsCapabilityDispatcher.cs) 及同目录 accounts/http 实现 |
| 凭证读取/刷新或序列化形状 | [JsPluginCodec](../src/Router.Host/Plugins/JavaScript/JsPluginCodec.cs)、[AccountService](../src/Router.Infrastructure/Services/AccountService.cs) |
| 选号、业务重试、代理处罚 | [PluginProxyRuntime](../src/Router.Infrastructure/Services/PluginProxyRuntime.cs)、[PluginAttemptDecisions](../src/Router.Infrastructure/Services/PluginAttemptDecisions.cs) |
| pool 传输重试 | [ProxyPoolHttpClientFactory](../src/Router.Infrastructure/Services/ProxyPoolHttpClientFactory.cs)、[PluginResiliencePipelines](../src/Router.Infrastructure/Services/PluginResiliencePipelines.cs) |
| SSE 截断、usage 丢失、热更新卡住 | [JS Streams](../src/Router.Host/Plugins/JavaScript/JsPlatformTerminal.Streams.cs)、[PluginResponseLifetime](../src/Router.Infrastructure/Services/PluginResponseLifetime.cs)、[ProtocolResponseWriter](../src/Router.Host/Api/ProtocolResponseWriter.cs) |
| job 取消、重复运行、历史消失 | [PluginJobManager](../src/Router.Infrastructure/Services/PluginJobManager.cs)、[PluginTaskRunner](../src/Router.Host/Plugins/PluginTaskRunner.cs) |

## 3. 实现顺序：每一步都能验收

### 第一步：列行为对照表，不先造框架

为当前提供方逐项标记 `原实现 → 新入口 → 测试 → 状态`：

```text
凭证录入/校验 → 管理端点 + validateCredential → 成功/失败/越权
OAuth 刷新   → refreshCredential + accounts.refresh → 并发/CAS/保留冷却
模型发现     → getModels → TTL/刷新/版本切换
选号         → selectAccounts → 硬过滤/到期优先/稳定权重/未知明细
生成请求     → invoke → 文本/工具/图片/文件/原始字段
流转换       → event/end/completion → EOF/取消/usage/工具参数分片
后台工作     → tasks 或 jobs → 去重/锁/取消/副作用
管理页面     → page + endpoints → 移动端/错误态/权限
```

状态使用“已实现且已测”“已实现待测”“未实现”“明确不支持”。不要只写“支持全部功能”。

复用已存在的能力，不在插件里再造账号池、Polly 工厂、任务调度器或服务定位器；不为一个插件额外引入通用抽象层。

### 第二步：让最小插件真正通过宿主加载

- JS 主案例使用 js-forwardapi（参见 `Rouer-Plugins-js/plugins/js-forwardapi/src/plugin.ts`），测试只用 Node 模拟 ctx，不添加 C# 项目。
- C# 主案例使用 ForwardAPI（参见 `Rouer-Plugins-Csharp/src/Plugins.ForwardAPI/ForwardApiTerminal.cs`），仅引用 Contracts，源码测试依赖暂时保持现状。
- 先通过类型/编译、清单/导出和无副作用模拟，再连接测试账号和网络。
- 清单声明的所有 export 都必须实际存在；没有实现的 hook 不要先挂一个永远成功的空函数。
- 模拟测试不证明真实 Jint、浏览器、计费或上游 SSE 已通过。

### 第三步：先正确处理账号与上游，再扩展界面

1. 输入校验：ID、URL、凭证类型、金额/期限、JSON 字段；不把外部字符串直接用作代码或 HTML。
2. 凭证只进账号服务。需要刷新的地方使用凭证专用 CAS/刷新，不写回整个旧账号快照。
3. 模型 POST 用当前 attempt client；任务健康检查用独立 pool，是否直连必须显式。
4. 业务错误转为一次 `PluginAttemptDecision`；不先冷却账号再返回同一动作。
5. 普通完成、工具、reasoning、usage 和原始字节分别测试。不能把 `messages.map(m => m.content)` 当作全协议适配。
6. 状态写入包含有限 TTL；精确金额/Int64 用字符串，原始转发用原生 JSON 句柄。

### 第四步：补齐流和长任务

- `http.open` 后明确每个 source 是读取、关闭还是移交；任何异常分支都不能遗失所有权。
- mapper 同步、短时、有界；不要偷偷 `async`，不要在 mapper 中查询积分/刷新 token。
- 结束帧不等于已经写给下游；最终 marker 写失败、取消或 EOF 截断不得算成功。
- job 接收 JSON ID/配置，不捕获当前 Engine、HTTP 句柄或临时请求令牌。
- 可取消等待要使用 `ctx.delay` / C# 传入的 token；不靠 busy loop、`setInterval` 或脱管 `Task.Run`。
- 任务去重只是本版本内存去重，Cron 锁也有 TTL；上游副作用另行设计幂等/补偿。

### 第五步：界面、发行包与运维

- 页面调用当前插件相对路由，使用宿主桥，不自己拿管理员 Cookie 或生成 CSRF token。
- 用 loading/空态/错误态解释状态；展示外部数据用 `textContent`。后台结果只返回允许展示的字段。
- 生产 JS 仅复制 bundle、发行清单、自包含 HTML；C# 仅复制完整 DLL 发行包及所需依赖。
- 安装目录是运行程序目录下的 `plugins`；配置文件按工作目录读取，两者分开确认。
- 安装/重载是有副作用的运行操作，不因写了构建脚本就自动执行。

## 4. 不得破坏的架构与安全规则

| 禁止做法 | 正确做法 |
| --- | --- |
| JS 导入 Node/CLR/`fetch`/数据库驱动 | 纯 JS bundle + 声明过的 `ctx` 能力 |
| JS 用模块变量保存跨请求账号/缓存 | accounts / state.local / state.shared，选择正确生命周期 |
| 修改 `ctx.pluginKey/phase` 获得更多权限 | 身份由宿主原生绑定；按清单和阶段实现 |
| 选号时每个账号发一次余额 HTTP | 预热/异步刷新本地快照，一次返回整批偏好 |
| 把 token 有效期当作积分包到期 | 从可用积分明细计算，未知时不制造优先时间 |
| pool 无代理就偷偷直连 | fail-closed；确需回退时声明 direct 并显式启用 |
| 对所有 429/5xx/POST 叠加自动重试 | 区分业务 attempt 与安全传输重试，先确认重放安全性 |
| 返回流后重新发生成请求接着拼 | 终止当前流并正确报错，不能重复计费/拼接两个回答 |
| 原始 JSON 过一遍 Number 再转发 | `originalBodyRef` + `originalJson` 保留未修改字段 |
| 把本地锁 + CAS 说成跨实例只刷新一次 | 准确描述本机串行和数据库防覆盖，验证上游 token 轮换 |
| 返回整份凭据/账号/异常到页面或 job 结果 | 白名单投影、脱敏日志，不依赖自动脱敏兜底 |
| 自建后台线程使重载不用等待 | 用宿主 jobs/Cron，取消并 drain 后才释放版本资源 |
| 引用 `Router.Infrastructure/Host` 调内部服务 | C# 用 `host.Services`，缺失能力先报告契约差异 |
| 一边测一边自动重启生产宿主/提交推送 | 只执行当前任务明确授权的操作 |

必须保留前几轮已修复的 SSE、取消和最终写出语义。不要为了消除测试失败而缩短流寿命、吞掉取消、跳过权限或伪造成功结果。

## 5. 验收矩阵与执行命令

### 5.1 最小验收矩阵

| 层 | 至少要有的证据 |
| --- | --- |
| 静态 | TS/C# 编译、清单导出匹配、中文注释、相对文档链接 |
| 构建 | 完整安装包，无 Node 外部 imports/源码泄露；不执行用户 hook |
| 真实运行时 | Jint/DLL 加载、路由归属、凭证校验、重载失败保留旧版本 |
| 账号与状态 | 越权拒绝、CAS 冲突、并发冷却不被覆盖、TTL/共享状态故障 |
| HTTP | origin/route 拒绝、无代理行为、超时取消、二进制与重定向 |
| 协议 | 文本/工具/reasoning/usage、SSE 结束/EOF/下游断开/非流聚合 |
| 生命周期 | 未消费流释放、写尾失败、热更新期间旧流/任务退出 |
| 提供方 | 真实请求/刷新/计费/任务行为；没有测试账号时明确“未验证” |

按改动范围选择已有测试，新增最小能证明行为的用例。不要只验证 happy path；更不要 mock 掉正在修复的实现后宣称真实 Jint 已通过。

### 5.2 仓库内检查

从当前仓库根目录运行对应检查；跨仓库测试依赖及首次安装步骤见根目录 README：

```powershell
dotnet test tests/Router.Tests/Router.Tests.csproj --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
pnpm --dir web test
pnpm --dir web build
git -c core.safecrlf=false diff --check
```

首次运行需要正常恢复依赖；`check:docs` 只做本地文档/类型检查，不启动宿主，不代替构建、Jint 测试或真实上游验收。Windows 下可将 npm 替换为 `npm.cmd`。

执行环境拒绝子进程、网络或审批超时：报告具体未完成命令，按环境规则申请授权；**不得绕过限制，也不得把“预计能通过”写成“通过”**。测试结果只对应实际运行的仓库版本，不自动覆盖之后的修改。

## 6. 交付说明的格式

过程中按实质进展简短汇报，避免长时间无响应；不每读一个文件就刷屏。最后说明：

1. 改了哪些入口，开发者从哪篇教程/哪个样例开始。
2. 已执行的命令、通过/失败结果；受阻、未执行和真实上游未验证分开列。
3. 是否涉及兼容性、安装/重载、数据库或副作用；未授权时不要提交/推送。

不要把“文档写好”“框架能力存在”“某个提供方业务迁移完成”“线上验证成功”四件事混成一个结论。

## 7. 可直接交给 AI 的任务模板

```text
请基于当前 Router2API 仓库实现/修改 [提供方名称] 插件。

语言：[Jint TypeScript / C#]
包 ID：[...]
平台：[...]
工作范围：[新建 / 修改 / 从现有 C# 迁移，涉及哪些行为]
业务要求：[凭证、模型、协议、选号、积分、Cron/job、页面]
上游 origin/路由：[逐项列明，未知的先查现有实现]
重试授权：[可重放的操作及次数，不授权默认 POST 重放]
验收环境：[离线 mock / 测试宿主，真实账号另行授权]
发布范围：[仅代码和文档；不安装、不重载、不提交、不推送]

先读 sdk/README.md、sdk/AI-DEVELOPMENT.md、对应语言 DEVELOPMENT.md
和 sdk/HOST-LIFECYCLE.md，再追当前 Contracts、bridge、提供方源码与测试。
保留已有工作区修改，不编造 SDK API，不开放 Node/CLR，不重建账号/代理/任务框架。
先输出简短行为对照清单，然后实现、补必要测试、更新中文注释和使用说明。
必须保留流最终写出、取消、凭证 CAS、资源归属和显式重试边界。
最终列明实际运行的检查、未执行项，以及尚未验证的提供方行为。
```
