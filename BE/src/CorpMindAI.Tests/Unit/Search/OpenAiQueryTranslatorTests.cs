using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Xunit;

namespace CorpMindAI.Tests.Unit.Search;

[Trait("TestType", "Unit")]
public sealed class OpenAiQueryTranslatorTests
{
    [Fact]
    public async Task TranslateToAlternateLanguageAsync_uses_stateless_responses_request()
    {
        JsonDocument? requestBody = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            requestBody = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            content = new[]
                            {
                                new
                                {
                                    type = "output_text",
                                    text = "When are privileged access reviews performed?"
                                }
                            }
                        }
                    }
                })
            };
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        var translator = new OpenAiQueryTranslator(
            client,
            Options.Create(new OpenAiQueryTranslationSettings { ApiKey = "test-key" }));

        var result = await translator.TranslateToAlternateLanguageAsync(
            "Việc rà soát quyền truy cập đặc quyền được thực hiện khi nào?");

        Assert.Equal("When are privileged access reviews performed?", result);
        Assert.NotNull(requestBody);
        Assert.Equal("gpt-4.1-mini", requestBody.RootElement.GetProperty("model").GetString());
        Assert.False(requestBody.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(0, requestBody.RootElement.GetProperty("temperature").GetInt32());
        Assert.Equal(1, handler.CallCount);
        requestBody.Dispose();
    }

    [Fact]
    public async Task TranslateToAlternateLanguageAsync_does_not_retry_exhausted_credit()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonContent.Create(new
            {
                error = new
                {
                    code = "credit_balance_exhausted",
                    message = "No credits remain."
                }
            })
        }));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        var translator = new OpenAiQueryTranslator(
            client,
            Options.Create(new OpenAiQueryTranslationSettings { ApiKey = "test-key" }));

        var exception = await Assert.ThrowsAsync<QueryTranslationException>(() =>
            translator.TranslateToAlternateLanguageAsync("truy vấn"));

        Assert.Contains("credit_balance_exhausted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responseFactory(request, cancellationToken);
        }
    }
}
