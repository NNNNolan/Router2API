# Contracts 发布去重

`publish-contracts.yml` 以 nuget.org **实际发布过的上一版本**为基准，不依赖上一个 Git tag 是否发布成功。

1. 从 NuGet 版本列表选择小于目标版本的最近版本；正式版只比较正式版，预发布可比较正式或预发布版本。
2. 从上一版 `.nuspec` 读取本仓库的来源提交，用完整 Git 历史导出上一版和目标 tag 的源码。
3. 使用同一 .NET SDK、固定版本信息和路径、不含调试信息的确定性构建，比较 `Router.Contracts.dll` 的 SHA-256。
4. 哈希相同时跳过打包、NuGet 登录和推送；不同或首次发布时，仍按目标 tag 版本正常打包。目标版本已存在也会跳过。

归一化只用于比较，不改变正式包中的程序集版本、提交信息或调试文件。网络故障、缺失来源提交或比较构建失败时会终止，不能当作首次发布。比较版本、来源提交、两个哈希和发布/跳过结论会打印到控制台，并写入 Actions Summary。注释、宿主或前端改动不触发 Contracts 空版本发布。

手动运行时，比较脚本来自选中的工作流分支，实际比较及打包源码来自 `version` 对应的 `v<version>` tag。该分支必须包含新的工作流和脚本。DLL 内容没有变化的纯包元数据调整也会跳过；此检查不是 API 兼容性分析。

本地离线回归（PowerShell 7 和 .NET 10 SDK）：

```powershell
pwsh -File .github/scripts/compare-contracts.tests.ps1
```

输出分为版本选择、模拟发布决策、真实编译示例 DLL 三部分，逐项打印预期值、实际值、异常原因及 DLL SHA-256，明确说明应发布还是跳过，最后汇总断言数量。**其中 NuGet 响应为模拟数据，真实哈希来自临时示例 DLL，不代表当前仓库与已发布包的实际比较结果。**

只检查某个待发布 tag，不推送：

```powershell
pwsh -File .github/scripts/compare-contracts.ps1 -Version 2.0.2 -Revision refs/tags/v2.0.2
```

后一个命令需要网络和完整 Git 历史，且对应 tag 必须存在；不会执行 `dotnet pack`、登录或推送。
