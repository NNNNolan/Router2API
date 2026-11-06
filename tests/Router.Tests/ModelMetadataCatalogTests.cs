using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ModelMetadataCatalogTests
{
    [TestMethod]
    public async Task FindReadsOnlyTheCurrentSnapshotAndNeverStartsAnExternalRefresh()
    {
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        using var catalog = new ModelMetadataCatalog(http.Object, Mock.Of<ISharedKeyValueStore>(),
            NullLogger<ModelMetadataCatalog>.Instance);

        (await catalog.FindAsync("openai", "model")).Should().BeNull();
        http.VerifyNoOtherCalls();

        http.Setup(value => value.CreateClient("models-dev"))
            .Returns(() => new HttpClient(new MetadataHandler()));
        var snapshot = await catalog.GetAsync(forceRefresh: true);
        var metadata = await catalog.FindAsync("openai", "model");

        metadata.Should().BeEquivalentTo(snapshot.Models.Single());
        http.Verify(value => value.CreateClient("models-dev"), Times.Once);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"openai":{"id":"openai","name":"OpenAI","models":{"model":{"id":"model","name":"Model"}}}}""")
            });
    }
}
