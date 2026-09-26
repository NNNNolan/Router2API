#!/bin/sh
set -eu
mkdir -p /app/data /app/plugins /app/Config
# 不捆绑、覆盖或从网络下载提供方插件；由操作者安装审阅过的独立发行包。
exec dotnet /app/Router.Host.dll
