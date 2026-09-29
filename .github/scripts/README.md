# Contracts 发布去重

`publish-contracts.yml` 使用待发布来源 tag 中 `src/Router.Contracts/Router.Contracts.csproj` 的 `<Version>` 作为 NuGet 包版本。宿主版本与 Contracts 包版本可以独立维护。

1. 从待发布 tag 读取 `<Version>` 并使用 NuGet 的版本解析规则归一化。
2. 查询 nuget.org 的版本列表；该版本已存在时跳过，未发布时按项目版本打包并推送。

版本号是去重依据，不通过 DLL 内容哈希判断是否发布。Contracts API 有改动时需同步提升 `.csproj` 的 `<Version>`；NuGet 不允许覆盖同一版本。网络故障会终止流程，不能当作首次发布。项目版本、来源提交及发布/跳过结论会打印到控制台，并写入 Actions Summary。

手动运行时，选择一个来源 tag（例如 `v2.0.0`）；脚本从该 tag 读取项目版本，打包源码也来自同一 tag。所选工作流分支必须包含新的工作流和脚本。

本地离线回归（PowerShell 7 和 .NET 10 SDK）：

```powershell
pwsh -File .github/scripts/compare-contracts.tests.ps1
```

输出覆盖 NuGet 版本匹配、未发布版本允许发布、已发布版本跳过以及异常失败关闭；不构建 DLL，也不执行发布。

只检查某个待发布 tag，不推送：

```powershell
pwsh -File .github/scripts/compare-contracts.ps1 -Revision refs/tags/v2.0.2
```

后一个命令需要网络和完整 Git 历史，且对应 tag 必须存在；不会执行 `dotnet pack`、登录或推送。
