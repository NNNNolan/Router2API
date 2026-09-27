using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Router.Host.Api;

namespace Router.Tests;

/// <summary>版本接口读取宿主程序集，而不是测试入口或插件/Contracts 版本。</summary>
[TestClass]
public sealed class HostVersionTests
{
    /// <summary>返回编译进宿主的版本，不暴露附加的源码提交号。</summary>
    [TestMethod]
    public void VersionEndpointReturnsTheRunningHostVersion()
    {
        var assemblyVersion = typeof(ApiEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var result = ApiEndpoints.GetHostVersion();

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        var json = JsonSerializer.SerializeToNode(((IValueHttpResult)result).Value)!;
        json["version"]!.GetValue<string>().Should().Be(assemblyVersion.Split('+')[0])
            .And.NotContain("+").And.NotBeNullOrWhiteSpace();
    }
}
