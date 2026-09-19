using System.Net;
using System.Net.Http.Json;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Xunit;

namespace CorpMindAI.Tests.Unit.Search;

[Trait("TestType", "Unit")]
public sealed class OpenAiEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_does_not_retry_when_openai_reports_exhausted_credit()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonContent.Create(new
            {
                error = new
                {
                    code = "credit_balance_exhausted",
                    message = "No credits remain."
                }
            })
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        var generator = new OpenAiEmbeddingGenerator(
            client,
            Options.Create(new OpenAiEmbeddingSettings { ApiKey = "test-key" }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            generator.GenerateAsync(new[] { "health check" }));

        Assert.Contains("credit_balance_exhausted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
