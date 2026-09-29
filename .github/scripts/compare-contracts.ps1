#requires -Version 7
<#
.SYNOPSIS
根据 Router.Contracts.csproj 的 NuGet 版本判断是否需要发布。
.DESCRIPTION
从指定来源提交读取 <Version>，目标版本尚未出现在 nuget.org 时允许发布；已存在时跳过。
#>
[CmdletBinding()]
param(
    [string]$Revision = 'HEAD',
    [string]$Repository = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'

function Initialize-NuGetVersioning([string]$Repository) {
    if ('NuGet.Versioning.NuGetVersion' -as [type]) { return }
    $project = Join-Path $Repository 'src/Router.Contracts/Router.Contracts.csproj'
    $sdk = @(& dotnet msbuild $project -nologo -getProperty:MSBuildBinPath)[-1]
    if ($LASTEXITCODE -ne 0) { throw '无法确定 .NET SDK 路径。' }
    # 复用 SDK 自带的 NuGet 版本比较器，不增加依赖或自行实现 SemVer。
    Add-Type -LiteralPath (Join-Path ([string]$sdk).Trim() 'NuGet.Versioning.dll')
}

function Get-ContractsSourceVersion([string]$Repository, [string]$Revision) {
    $commit = & git -C $Repository rev-parse --verify --end-of-options "$Revision^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "待发布来源不存在：$Revision" }

    $projectPath = 'src/Router.Contracts/Router.Contracts.csproj'
    $specification = '{0}:{1}' -f ([string]$commit).Trim(), $projectPath
    $source = & git -C $Repository show $specification
    if ($LASTEXITCODE -ne 0) { throw "无法读取 $Revision 中的 $projectPath。" }

    try {
        $project = [xml]([string]::Join([Environment]::NewLine, [string[]]$source))
    }
    catch {
        throw "无法解析 $Revision 中的 $projectPath：$($_.Exception.Message)"
    }

    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $version -or [string]::IsNullOrWhiteSpace($version.InnerText)) {
        throw "$projectPath 缺少非空的 <Version>，停止发布。"
    }
    return $version.InnerText.Trim()
}

function Get-ContractsVersionPlan([string]$Version, [string[]]$PublishedVersions) {
    $target = [NuGet.Versioning.NuGetVersion]::Parse($Version)
    $versions = @($PublishedVersions | ForEach-Object { [NuGet.Versioning.NuGetVersion]::Parse($_) })
    [pscustomobject]@{
        Version = $target.ToNormalizedString()
        AlreadyPublished = $versions -contains $target
    }
}

function Get-PublishedContractsVersions {
    try {
        $index = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/router.contracts/index.json' -TimeoutSec 30
    }
    catch {
        # 只有明确的 404 才表示首次发布；网络、权限及服务错误必须终止流程。
        if ($_.Exception.Response.StatusCode -eq 404) { return @() }
        throw
    }
    if ($index.versions -isnot [array]) {
        throw 'nuget.org 返回了无效的版本列表，停止发布。'
    }
    return @($index.versions)
}

function Get-ContractsPublishDecision([string]$Revision, [string]$Repository) {
    Initialize-NuGetVersioning $Repository
    $commit = & git -C $Repository rev-parse --verify --end-of-options "$Revision^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "待发布来源不存在：$Revision" }

    $sourceVersion = Get-ContractsSourceVersion $Repository $Revision
    $plan = Get-ContractsVersionPlan $sourceVersion @(Get-PublishedContractsVersions)
    $alreadyPublished = [bool]$plan.AlreadyPublished
    [pscustomobject]@{
        changed = -not $alreadyPublished
        version = $plan.Version
        revision = ([string]$commit).Trim()
        alreadyPublished = $alreadyPublished
        reason = if ($alreadyPublished) {
            "Router.Contracts $($plan.Version) 已存在，跳过重复发布。"
        } else {
            "Router.Contracts $($plan.Version) 尚未发布，允许发布。"
        }
    }
}

# 允许离线测试点入函数；只有直接执行脚本时才查询 nuget.org。
if ($MyInvocation.InvocationName -ne '.') {
    $result = Get-ContractsPublishDecision $Revision (Resolve-Path -LiteralPath $Repository).Path
    if ($env:GITHUB_OUTPUT) {
        @(
            "changed=$($result.changed.ToString().ToLowerInvariant())"
            "version=$($result.version)"
            "revision=$($result.revision)"
        ) | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    }
    $summary = @"
## Router.Contracts 发布检查

$($result.reason)

- 项目版本：$($result.version)
- 待发布来源提交：$($result.revision)
- nuget.org 中该版本：$(if ($result.alreadyPublished) { '已存在，跳过发布' } else { '不存在，允许发布' })
"@
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
}
