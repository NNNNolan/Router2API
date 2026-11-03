using FluentAssertions;
using Router.Contracts.Domain;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class CredentialCodecTests
{
    [TestMethod]
    public void LegacyNamesNullAliasesAndInferenceKeepTheirPrecedence()
    {
        CredentialCodec.Decode("O-A_U_T_H", """{"ACCESS_TOKEN":"access","refresh-token":"refresh","ACCOUNT_ID":"account","display_name":"name"}""")
            .Should().Be(new OAuthCredential("access", RefreshToken: "refresh", AccountId: "account", Nickname: "name"));
        CredentialCodec.Decode("OAuth", """{"key":"first","api_key":"second","access_token":"ignored"}""")
            .Should().Be(new ApiKeyCredential("first"));
        CredentialCodec.Decode("OAuth", """{"api_key":null,"key":"ignored","access_token":"access"}""")
            .Should().Be(new OAuthCredential("access"));
        CredentialCodec.Decode("unknown", """{"refresh_token":null,"account_id":"later","token":"bearer"}""")
            .Should().Be(new BearerTokenCredential("bearer"));
        CredentialCodec.Decode("OAuth", """{"username":"u","password":"p","access_token":"ignored"}""")
            .Should().Be(new BasicAuthCredential("u", "p"));
        CredentialCodec.Decode("OAuth", """{"nickname":null,"displayName":"ignored"}""")
            .Should().Be(new OAuthCredential(""));
    }

    [TestMethod]
    public void ExplicitCustomKindKeepsCookieAndTokenFieldsInsteadOfDowngrading()
    {
        // 插件把会话写在自定义字段 `cookie` 里；过去的解码启发式会把整份凭据降级成 CookieCredential，
        // 插件再读就只剩一个无字段的空凭据（豆包插件因此一直显示“缺少登录态”）。
        var custom = (CustomCredential)CredentialCodec.Decode("Custom",
            """{"cookie":"sessionid=abc","web_id":"7488554124442884203","nickname":"用户073255","uid":"4311","rate":1}""");
        custom.Fields.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["cookie"] = "sessionid=abc",
            ["web_id"] = "7488554124442884203",
            ["nickname"] = "用户073255",
            ["uid"] = "4311",
            ["rate"] = "1"
        });

        // 带 token 的自定义凭据同样不能被降级成 BearerTokenCredential。
        var tokenCustom = (CustomCredential)CredentialCodec.Decode("Custom", """{"token":"t","apiKey":"k","extra":"x"}""");
        tokenCustom.Fields.Should().ContainKeys("token", "apiKey", "extra");

        // 显式 Cookie 类型与旧版无 kind 的裸 JSON 仍保持原有降级行为。
        CredentialCodec.Decode("Cookie", """{"cookie":"a=1"}""").Should().Be(new CookieCredential("a=1"));
        CredentialCodec.Decode("unknown", """{"cookie":"a=1"}""").Should().Be(new CookieCredential("a=1"));
    }

    [TestMethod]
    public void CredentialKindsRoundTripAndDecodeConcurrentlyWithoutSharedState()
    {
        Credential[] credentials = [new ApiKeyCredential("key"), new OAuthCredential("access", RefreshToken: "refresh"),
            new BearerTokenCredential("bearer"), new BasicAuthCredential("u", "p"), new CookieCredential("cookie")];
        Parallel.For(0, 500, index =>
        {
            var expected = credentials[index % credentials.Length];
            var encoded = CredentialCodec.Encode(expected);
            CredentialCodec.Decode(encoded.Kind, encoded.Json).Should().Be(expected);
        });
        var custom = (CustomCredential)CredentialCodec.Decode("Custom", """{"nested":{},"count":123,"flag":true,"text":"value"}""");
        custom.Fields.Should().BeEquivalentTo(new Dictionary<string, string?> { ["count"] = "123", ["flag"] = "True", ["text"] = "value" });
    }
}
