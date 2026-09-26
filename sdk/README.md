# Router2API 宿主 SDK 入口

这里记录当前宿主的公开边界，供人和 AI 共用。宿主为 .NET 10，JS 运行时为 Jint 4.16.4；C# Contracts 2.0 和 JS Host API 1 是两套版本编号。

## 本仓库

- [宿主运转全过程](HOST-LIFECYCLE.md)：启动、包加载、选号、Polly、SSE、任务、页面和卸载。
- [AI 开发工作单](AI-DEVELOPMENT.md)：事实来源、工作顺序、约束和验收。
- [C# 公共服务](../src/Router.Contracts/Host/PluginServices.cs)、[插件声明](../src/Router.Contracts/Plugins/PluginContracts.cs)、[响应契约](../src/Router.Contracts/Domain/Models.cs)。
- [Jint 运行时实现](../src/Router.Host/Plugins/JavaScript/) 和 [宿主测试](../tests/Router.Tests/)。

## 独立插件仓库

| 仓库 | 开发入口 |
| --- | --- |
| `Rouer-Plugins-js` | `sdk/js/DEVELOPMENT.md`、`sdk/js/API.md`、带中文注释的 `sdk/js/index.d.ts` |
| `Rouer-Plugins-Csharp` | `sdk/csharp/DEVELOPMENT.md`、`sdk/csharp/README.md`、ForwardAPI 源码 |

由于尚未指定公开远程地址，跨仓库引用以“仓库名/路径”标明，不虚构 URL。宿主不依赖插件仓库源码才能构建；相关测试命令在[根目录说明](../README.md)。

文档以当前 Contracts、bridge、loader 和真实测试为准；历史方案不能冒充已实现 API。只安装经过审阅的可信插件，原生 DLL/Jint 都不是 OS 安全沙箱。
