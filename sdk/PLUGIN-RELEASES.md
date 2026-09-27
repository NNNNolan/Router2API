# 插件 GitHub Release 索引协议

一个仓库的一次 Release 可以包含多个插件。每个插件有独立 ZIP；同一次 Release 的 `release-index.json` 是唯一的资产目录。仓库 tag 标识这一批产物，插件本身的版本仍由 JS 清单或 C# 程序集给出。

```json
{
  "schemaVersion": 1,
  "tag": "v1.2.3",
  "plugins": [
    {
      "id": "forwardapi",
      "name": "ForwardAPI",
      "description": "将兼容 OpenAI API 的站点接入 Router2API，支持多账号与请求转发。",
      "runtime": "dotnet",
      "version": "1.0.0.0",
      "asset": "forwardapi.zip",
      "sha256": "64 位小写十六进制 SHA-256",
      "contentSha256": "按相对路径和文件内容生成的稳定 SHA-256",
      "sizeBytes": 123456
    }
  ]
}
```

`description` 是插件简介，供宿主订阅列表展示，也会写入 GitHub Release 正文。C# 从项目文件的 `Description` 读取，JS 从 `plugin.json` 读取。Release 正文按插件分段列出简介、版本和下载文件，再附仓库变更记录。`runtime` 为 `dotnet` 或 `jint`；`asset` 是**同一个 Release** 中的资产文件名，不是任意下载 URL。ZIP 顶层是 `id/`，其中直接放 C# 主 DLL 或 JS `plugin.json`。索引不包含安装路径、仓库外 URL 或自动执行指令。

宿主插件管理页支持公开 GitHub 仓库订阅。管理员添加仓库后，选择一个正式 Release 和其中的插件下载；下载选择不会自动扩展到该仓库其他插件。订阅记录保存在运行目录 `plugins/subscription/subscriptions.json`，下载暂存也位于 `plugins/subscription/`，均不进入数据库。更新页签比较已订阅插件的 `contentSha256`，只更新管理员勾选的插件；旧索引没有此字段时回退到 ZIP 的 `sha256`。删除插件会移除安装目录和该插件的订阅记录；仓库记录保留，便于之后再次选择安装。

宿主订阅按以下顺序处理：

1. 管理员配置公开 GitHub 仓库 `owner/repo`，宿主列出该仓库正式 Release；预发布版暂不列出。
2. 校验 `schemaVersion`、索引 `tag` 与 Release tag 一致、每个 `id` 和 `asset` 唯一且文件名安全；只展示索引中的插件，不猜测 ZIP 名称。
3. 用户按 `id` 订阅具体插件；持久化 `owner/repo + id`，避免一个仓库发布新插件时意外安装。不同来源声明相同 `id` 时要求管理员选择来源。
4. 从同一 Release 的资产列表按 `asset` 名称取 ZIP，限制下载和解压大小，校验字节数与 SHA-256，并拒绝 ZIP 路径穿越、绝对路径和符号链接。再校验包内目录名与清单/程序集声明的插件身份。
5. 下载到临时目录，验证完整包后替换对应插件目录并重载；失败则恢复旧目录。下载校验使用 ZIP 的 `sha256`；更新判断优先使用不受 ZIP 时间戳影响的 `contentSha256`。插件 `version` 用于展示；兼容性仍以包内清单或程序集声明为准。

GitHub Release 及其工作流需要来自可信维护者；摘要用于发现下载或索引不一致，不能代替仓库信任。原生 DLL 可在宿主进程执行代码，JS 也不是操作系统沙箱。更新需要管理员在页面明确选择；目前仅支持公开仓库，不保存 GitHub 凭据。
