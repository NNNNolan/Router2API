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
