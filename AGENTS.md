# 仓库开发约定

- 本地 shell 默认 PowerShell 7。
- 先阅读 README.md、sdk/README.md 和 sdk/AI-DEVELOPMENT.md。
- 这是独立公开源码仓库，不依赖或导入私有开发仓库、真实配置、凭据、日志、数据库及 Git 历史。
- 构建、测试、安装和推送是不同操作；未经明确授权不安装到生产、不提交或推送。
- 保留 Contracts/Host API 和 SSE 生命周期边界，按 README 执行对应仓库测试。
