#requires -Version 7
# 离线回归：不请求 nuget.org、不打包或发布。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'compare-contracts.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Initialize-NuGetVersioning $repositoryRoot
$script:passedChecks = 0

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "[失败] $Message，预期 '$Expected'，实际 '$Actual'。" }
    $script:passedChecks++
    Write-Host "[通过] $Message：$Actual"
}

function Assert-Fails([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        $script:passedChecks++
        Write-Host "[通过] $Message：已按预期拒绝"
        return
    }
    throw "[失败] $Message：预期抛出异常，实际正常返回。"
}

Write-Host 'Contracts 版本发布去重离线回归：不比较 DLL 哈希。'
$project = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Router.Contracts/Router.Contracts.csproj') -Raw)
$projectVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$sourceVersion = Get-ContractsSourceVersion $repositoryRoot 'HEAD'
Assert-Equal $projectVersion $sourceVersion '从目标提交的 csproj 读取项目版本'

Write-Host "`n=== NuGet 版本匹配 ==="
Assert-Equal '2.0.10' (Get-ContractsVersionPlan '2.0.10' @('2.0.9')).Version '保留 NuGet 版本的数字语义'
Assert-Equal $true (Get-ContractsVersionPlan '2.0.1.0' @('2.0.1')).AlreadyPublished '识别归一化后相同的 NuGet 版本'
Assert-Equal $false (Get-ContractsVersionPlan '2.0.2' @('2.0.0', '2.0.1')).AlreadyPublished '未发布的项目版本允许发布'
Assert-Fails { Get-ContractsVersionPlan 'not-a-version' @() } '无效项目版本必须失败'

Write-Host "`n=== 发布决策 ==="
$script:sourceVersion = '2.0.2'
$script:publishedVersions = @('2.0.0', '2.0.1')
$script:feedFails = $false
function Get-ContractsSourceVersion([string]$Repository, [string]$Revision) { return $script:sourceVersion }
function Get-PublishedContractsVersions {
    if ($script:feedFails) { throw '模拟 nuget.org 网络错误' }
    return @($script:publishedVersions)
}

$result = Get-ContractsPublishDecision 'HEAD' $repositoryRoot
Assert-Equal $true $result.changed '新项目版本允许发布'
Assert-Equal '2.0.2' $result.version '发布版本取自项目版本'

$script:sourceVersion = '2.0.1.0'
$result = Get-ContractsPublishDecision 'HEAD' $repositoryRoot
Assert-Equal $false $result.changed '已发布版本跳过，不受 DLL 内容影响'
Assert-Equal '2.0.1' $result.version '项目版本按 NuGet 规则归一化'

$script:feedFails = $true
Assert-Fails { Get-ContractsPublishDecision 'HEAD' $repositoryRoot } 'NuGet 查询失败必须停止发布'
$script:feedFails = $false
Assert-Fails { Get-ContractsPublishDecision 'missing-ref' $repositoryRoot } '缺失来源提交必须停止发布'

Write-Host "`n=== 回归汇总：$script:passedChecks 项断言通过，0 项失败 ==="
Write-Host '发布判定只依据目标 tag 中的 <Version> 与 nuget.org 已发布版本；未执行打包或发布。'
