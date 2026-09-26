using FluentAssertions;
using Router.Host.Security;

namespace Router.Tests;

[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    public void PasswordHasherUsesSaltedPbkdf2Sha256Hash()
    {
        var hasher = new PasswordHasher();
        var first = hasher.Hash("correct horse battery staple");
        var second = hasher.Hash("correct horse battery staple");

        first.Should().NotBe(second);
        hasher.Verify("correct horse battery staple", first).Should().BeTrue();
        hasher.Verify("wrong password", first).Should().BeFalse();
        first.Should().StartWith("pbkdf2$sha256$");
    }
}
