#requires -Version 7
<#
.SYNOPSIS
比较待发布版本与上一版 NuGet 包来源提交的 Contracts DLL，不执行发布。
.DESCRIPTION
两版源码使用同一 SDK、固定程序集版本和路径构建，再比较 SHA-256。
比较构建不含提交号或调试信息；正式打包仍使用原来的发布版本和元数据。
#>
[CmdletBinding()]
param(
    [string]$Version,
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

function Get-ContractsVersionPlan([string]$Version, [string[]]$PublishedVersions) {
    $target = [NuGet.Versioning.NuGetVersion]::Parse($Version)
    $versions = @($PublishedVersions | ForEach-Object { [NuGet.Versioning.NuGetVersion]::Parse($_) })
    $previous = $versions |
        Where-Object { $_ -lt $target -and ($target.IsPrerelease -or -not $_.IsPrerelease) } |
        Sort-Object -Descending |
        Select-Object -First 1
    [pscustomobject]@{
        Version = $target.ToNormalizedString()
        AlreadyPublished = $versions -contains $target
        PreviousVersion = if ($null -ne $previous) { $previous.ToNormalizedString() } else { $null }
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

function Get-PublishedContractsCommit([string]$Version) {
    $versionPath = [Uri]::EscapeDataString($Version.ToLowerInvariant())
    [xml]$spec = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/router.contracts/$versionPath/router.contracts.nuspec" -TimeoutSec 30
    $repository = $spec.package.metadata.repository
    if ([string]$repository.url -notmatch '^https://github\.com/NNNNolan/Router2API/?$' -or $repository.commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Router.Contracts $Version 缺少可信的来源提交，无法比较 DLL，停止发布。"
    }
    return [string]$repository.commit
}

function Get-ContractsDllHash([string]$SourceRoot) {
    $project = Join-Path $SourceRoot 'src/Router.Contracts/Router.Contracts.csproj'
    $output = Join-Path $SourceRoot 'artifacts/contracts-content'
    & dotnet build $project -c Release -o $output --nologo --verbosity quiet --no-incremental `
        --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false `
        -p:Version=0.0.0 -p:AssemblyVersion=0.0.0.0 -p:FileVersion=0.0.0.0 -p:InformationalVersion=0.0.0 `
        -p:IncludeSourceRevisionInInformationalVersion=false -p:EnableSourceControlManagerQueries=false `
        -p:Deterministic=true -p:DebugType=none -p:DebugSymbols=false "-p:PathMap=$SourceRoot=/_/contracts" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Contracts 比较构建失败，停止发布。' }
    return (Get-FileHash -LiteralPath (Join-Path $output 'Router.Contracts.dll') -Algorithm SHA256).Hash
}

function Get-ContractsRevisionHash([string]$Repository, [string]$Revision) {
    $temporary = [System.IO.Directory]::CreateTempSubdirectory('router-contracts-').FullName
    try {
        $archive = Join-Path $temporary 'source.zip'
        & git -C $Repository archive --format=zip "--output=$archive" $Revision
        if ($LASTEXITCODE -ne 0) { throw "无法读取来源提交 $Revision，请确保已获取完整 Git 历史。" }
        $source = Join-Path $temporary 'source'
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $source)
        return Get-ContractsDllHash $source
    }
    finally {
        $boundary = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('/\') + [System.IO.Path]::DirectorySeparatorChar
        if (-not [System.IO.Path]::GetFullPath($temporary).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
            throw '临时构建目录超出清理范围。'
        }
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}

function Get-ContractsPublishDecision([string]$Version, [string]$Revision, [string]$Repository) {
    Initialize-NuGetVersioning $Repository
    $plan = Get-ContractsVersionPlan $Version @(Get-PublishedContractsVersions)
    $commit = & git -C $Repository rev-parse --verify --end-of-options "$Revision^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "待发布来源不存在：$Revision" }
    $result = [ordered]@{
        changed = $false
        version = $plan.Version
        revision = [string]$commit
        previousVersion = $plan.PreviousVersion
        previousHash = ''
        currentHash = ''
        reason = ''
    }
    if ($plan.AlreadyPublished) {
        $result.reason = "Router.Contracts $($plan.Version) 已存在，跳过重复发布。"
    }
    elseif ($null -eq $plan.PreviousVersion) {
        $result.changed = $true
        $result.reason = '没有可比较的已发布版本，允许首次发布。'
    }
    else {
        $previousCommit = Get-PublishedContractsCommit $plan.PreviousVersion
        $result.previousHash = Get-ContractsRevisionHash $Repository $previousCommit
        $result.currentHash = Get-ContractsRevisionHash $Repository $commit
        $result.changed = $result.previousHash -ne $result.currentHash
        $result.reason = if ($result.changed) {
            "DLL 内容与 Router.Contracts $($plan.PreviousVersion) 不同，允许发布。"
        } else {
            "DLL 内容与 Router.Contracts $($plan.PreviousVersion) 相同，跳过 NuGet 发布。"
        }
    }
    return [pscustomobject]$result
}

# 允许离线测试点入函数；只有直接执行脚本时才查询 nuget.org。
if ($MyInvocation.InvocationName -ne '.') {
    $result = Get-ContractsPublishDecision $Version $Revision (Resolve-Path -LiteralPath $Repository).Path
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

- 待发布版本：$($result.version)
- 待发布来源提交：$($result.revision)
- 比较基准：$($result.previousVersion ?? '无')
- 上一版归一化 DLL SHA-256：$(if ($result.previousHash) { $result.previousHash } else { '未计算，无需比较' })
- 当前归一化 DLL SHA-256：$(if ($result.currentHash) { $result.currentHash } else { '未计算，无需比较' })
"@
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
}
