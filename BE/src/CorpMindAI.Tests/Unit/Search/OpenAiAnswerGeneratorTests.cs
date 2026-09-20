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
public sealed class OpenAiAnswerGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_sends_grounded_structured_output_request_and_maps_response()
    {
        JsonDocument? requestBody = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            requestBody = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
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
                                    text = "{\"answer\":\"Accounts must be disabled within 24 hours.\",\"hasAnswer\":true,\"citationChunkIds\":[\"child-1\"]}"
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
        var generator = new OpenAiAnswerGenerator(
            client,
            Options.Create(new OpenAiAnswerSettings { ApiKey = "test-key" }));

        var result = await generator.GenerateAsync(
            "How quickly must terminated accounts be disabled?",
            new[]
            {
                new AnswerContext(
                    "child-1",
                    28,
                    0.91,
                    new[] { "Access" },
                    "Terminated user accounts must be disabled within 24 hours.",
                    2,
                    2,
                    new[] { "component-1" })
            });

        Assert.True(result.HasAnswer);
        Assert.Equal("Accounts must be disabled within 24 hours.", result.Answer);
        Assert.Equal(new[] { "child-1" }, result.CitationChunkIds);
        Assert.NotNull(requestBody);
        Assert.Equal("gpt-4.1-mini", requestBody!.RootElement.GetProperty("model").GetString());
        Assert.False(requestBody.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(
            "json_schema",
            requestBody.RootElement
                .GetProperty("text")
                .GetProperty("format")
                .GetProperty("type")
                .GetString());
        Assert.Contains("child-1", requestBody.RootElement.GetProperty("input").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
        requestBody.Dispose();
    }

    [Fact]
    public async Task GenerateAsync_does_not_retry_exhausted_credit()
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
        var generator = new OpenAiAnswerGenerator(
            client,
            Options.Create(new OpenAiAnswerSettings { ApiKey = "test-key" }));

        var exception = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            generator.GenerateAsync(
                "question",
                new[]
                {
                    new AnswerContext("child-1", 1, 0.9, Array.Empty<string>(), "context", 1, 1, Array.Empty<string>())
                }));

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
