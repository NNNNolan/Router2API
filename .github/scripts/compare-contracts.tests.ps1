#requires -Version 7
# 离线回归：不请求 nuget.org、不修改仓库历史、不打包或发布。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'compare-contracts.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Initialize-NuGetVersioning $repositoryRoot
$script:passedChecks = 0

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "[失败] $Message，预期 '$Expected'，实际 '$Actual'。" }
    $script:passedChecks++
    Write-Host "[通过] $Message"
    Write-Host "  预期：$Expected"
    Write-Host "  实际：$Actual"
}
function Assert-Fails([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        $script:passedChecks++
        Write-Host "[通过] $Message"
        Write-Host '  预期：抛出异常，停止检查'
        Write-Host "  实际：已拒绝；$($_.Exception.Message)"
        return
    }
    throw "[失败] $Message：预期抛出异常，实际正常返回。"
}
function Assert-PublishDecision([bool]$Expected, $Result, [string]$Scenario) {
    Write-Host "`n[模拟场景] $Scenario"
    Write-Host "  待发布版本：$($Result.version)"
    Write-Host "  对比基准：$($Result.previousVersion ?? '无')"
    Write-Host "  上一版 SHA-256（模拟）：$(if ($Result.previousHash) { $Result.previousHash } else { '未计算，无需比较' })"
    Write-Host "  当前 SHA-256（模拟）：$(if ($Result.currentHash) { $Result.currentHash } else { '未计算，无需比较' })"
    Write-Host "  判断原因：$($Result.reason)"
    Assert-Equal ($Expected ? '允许发布' : '跳过发布') ($Result.changed ? '允许发布' : '跳过发布') '发布决策'
}

Write-Host 'Contracts 发布去重离线回归：以下是模拟场景和临时示例 DLL，不是当前仓库与 nuget.org 的实际对比。'
Write-Host "`n=== 1. NuGet 版本选择 ==="
# NuGet 版本比较必须按数字/预发布语义，而不是字符串排序。
Assert-Equal '2.0.10' (Get-ContractsVersionPlan '2.0.11' @('2.0.9', '2.0.10')).PreviousVersion '目标 2.0.11：按版本数值选择 2.0.9、2.0.10 中的最新基准'
Assert-Equal '2.0.1' (Get-ContractsVersionPlan '2.1.0' @('2.0.1', '2.1.0-rc.1')).PreviousVersion '目标 2.1.0 正式版：排除 2.1.0-rc.1 预发布基准'
Assert-Equal '2.1.0-rc.2' (Get-ContractsVersionPlan '2.1.0-rc.3' @('2.0.1', '2.1.0-rc.2')).PreviousVersion '目标 2.1.0-rc.3：可与上一个预发布版本比较'
Assert-Equal '2.0.0' (Get-ContractsVersionPlan '2.0.1' @('2.0.0', '3.0.0')).PreviousVersion '目标 2.0.1：排除高于目标的 3.0.0'
Assert-Equal $true (Get-ContractsVersionPlan '2.0.1.0' @('2.0.1')).AlreadyPublished '目标 2.0.1.0：识别已发布的等价版本 2.0.1'
Assert-Fails { Get-ContractsVersionPlan 'not-a-version' @() } '无效版本必须失败'

Write-Host "`n=== 2. 发布决策（NuGet 响应和 DLL 哈希均为模拟） ==="
# 用公开接口形状模拟 NuGet 响应，覆盖首次发布、重复/未变跳过及失败关闭。
& {
    $script:feedStatus = 200
    $script:versions = @('2.0.0', '2.0.1')
    $script:previousCommit = '1' * 40
    $script:currentHash = 'A' * 64
    $script:hashCalls = 0
    $script:buildFails = $false
    function Invoke-RestMethod([string]$Uri, [int]$TimeoutSec) {
        if ($script:feedStatus -ne 200) {
            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]$script:feedStatus)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('模拟 NuGet 故障', $response)
        }
        if ($Uri.EndsWith('/index.json')) { return @{ versions = $script:versions } }
        return "<package><metadata><repository url='https://github.com/NNNNolan/Router2API' commit='$script:previousCommit' /></metadata></package>"
    }
    function Get-ContractsRevisionHash([string]$Repository, [string]$Revision) {
        $script:hashCalls++
        if ($script:buildFails) { throw '模拟构建失败' }
        if ($Revision -eq $script:previousCommit) { return 'A' * 64 }
        return $script:currentHash
    }

    $script:feedStatus = 404
    Assert-PublishDecision $true (Get-ContractsPublishDecision '2.0.2' 'HEAD' $repositoryRoot) '首次发布：NuGet 返回 404'
    Assert-Equal 0 $script:hashCalls '首次发布无需比较'
    $script:feedStatus = 503
    Assert-Fails { Get-ContractsPublishDecision '2.0.2' 'HEAD' $repositoryRoot } '服务错误不能当作首次发布'
    $script:feedStatus = 200
    Assert-PublishDecision $false (Get-ContractsPublishDecision '2.0.1' 'HEAD' $repositoryRoot) '目标版本已存在'
    Assert-Equal 0 $script:hashCalls '已有版本无需构建'
    Assert-PublishDecision $false (Get-ContractsPublishDecision '2.0.2' 'HEAD' $repositoryRoot) '前后 DLL 哈希相同'
    Assert-Equal 2 $script:hashCalls '必须比较前后两版'
    $script:currentHash = 'B' * 64
    Assert-PublishDecision $true (Get-ContractsPublishDecision '2.0.2' 'HEAD' $repositoryRoot) '前后 DLL 哈希不同'
    $script:buildFails = $true
    Assert-Fails { Get-ContractsPublishDecision '2.0.2' 'HEAD' $repositoryRoot } '比较构建失败必须停止'
    $script:buildFails = $false
    $script:previousCommit = ''
    Assert-Fails { Get-ContractsPublishDecision '2.0.2' 'HEAD' $repositoryRoot } '缺失来源提交必须停止'
}

Write-Host "`n=== 3. 真实编译的临时示例 DLL 对比（不是已发布包） ==="
# 真实编译：仅版本号、源码路径或注释变化不改变哈希，实际 DLL 内容变化会改变哈希。
$temporary = [System.IO.Directory]::CreateTempSubdirectory('router-contracts-tests-').FullName
try {
    foreach ($name in @('previous', 'current')) {
        $project = Join-Path $temporary "$name/src/Router.Contracts"
        New-Item -ItemType Directory -Path $project -Force | Out-Null
        $version = if ($name -eq 'previous') { '2.0.0' } else { '9.0.0' }
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>$version</Version>
    <AssemblyVersion>$version.0</AssemblyVersion>
    <FileVersion>$version.0</FileVersion>
    <InformationalVersion>$version+$name</InformationalVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $project 'Router.Contracts.csproj')
        "// $name 的不同注释`npublic static class Contract { public const string Value = `"one`"; }" |
            Set-Content -LiteralPath (Join-Path $project 'Contract.cs')
    }
    Write-Host '编译基准：版本 2.0.0，常量 Value = "one"。'
    $previousHash = Get-ContractsDllHash (Join-Path $temporary 'previous')
    Write-Host '编译对比版本：版本 9.0.0，仅版本信息、注释和源码路径不同。'
    $currentHash = Get-ContractsDllHash (Join-Path $temporary 'current')
    Write-Host "`n[真实哈希对比] 仅版本、注释和路径变化"
    Write-Host "  基准 DLL SHA-256：$previousHash"
    Write-Host "  当前 DLL SHA-256：$currentHash"
    Assert-Equal '相同，跳过发布' ($previousHash -eq $currentHash ? '相同，跳过发布' : '不同，允许发布') 'DLL 对比结论'
    Set-Content -LiteralPath (Join-Path $temporary 'current/src/Router.Contracts/Contract.cs') `
        -Value 'public static class Contract { public const string Value = "two"; }'
    Write-Host "`n编译内容变化版本：常量 Value 从 `"one`" 改为 `"two`"。"
    $changedHash = Get-ContractsDllHash (Join-Path $temporary 'current')
    Write-Host "`n[真实哈希对比] DLL 编译内容变化"
    Write-Host "  基准 DLL SHA-256：$previousHash"
    Write-Host "  当前 DLL SHA-256：$changedHash"
    Assert-Equal '不同，允许发布' ($changedHash -eq $previousHash ? '相同，跳过发布' : '不同，允许发布') 'DLL 对比结论'
}
finally {
    $boundary = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('/\') + [System.IO.Path]::DirectorySeparatorChar
    if (-not [System.IO.Path]::GetFullPath($temporary).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw '测试临时目录超出清理范围。'
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
Write-Host "`n=== 回归汇总：$script:passedChecks 项断言通过，0 项失败 ==="
Write-Host '版本信息/注释/路径变化：DLL 哈希相同，跳过发布；实际编译内容变化：DLL 哈希不同，允许发布。'
Write-Host '以上仅验证去重规则，未执行 NuGet 发布。实际版本对比请运行 compare-contracts.ps1，并指定 -Version 和 -Revision。'
