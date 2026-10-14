using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Utils;

public static class Http
{
    public static HttpClient Client { get; } = new();

    // Long-running, unattended exports should be resilient to Discord being unavailable for
    // extended periods of time, so failed requests are retried at a fixed interval for up to
    // two hours before giving up.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    // 24 retries at 5 minutes each adds up to roughly two hours of retrying
    private const int MaxApiRetryCount = 24;

    // A single attempt is abandoned if it takes longer than this, so that a hung connection
    // is retried rather than blocking the export indefinitely.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(3);

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
        ||
        // Treat all server-side errors as retryable
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/908
        (int)statusCode >= 500;

    private static bool IsRetryableException(Exception exception) =>
        exception
            .GetSelfAndDescendants()
            .Any(ex =>
                // I/O failures cover responses that were truncated or interrupted mid-transfer,
                // while the rejected timeout comes from the per-attempt timeout policy below
                ex
                    is TimeoutException
                        or TimeoutRejectedException
                        or SocketException
                        or AuthenticationException
                        or IOException
                // A missing status code indicates a transport-level failure (e.g. the connection
                // was reset before a response could be produced), which is always retryable
                || ex is HttpRequestException hrex
                    && (hrex.StatusCode is null || IsRetryableStatusCode(hrex.StatusCode.Value))
            );

    public static ResiliencePipeline ResiliencePipeline { get; } =
        new ResiliencePipelineBuilder()
            .AddRetry(
                new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsRetryableException),
                    MaxRetryAttempts = 4,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                }
            )
            .Build();

    public static ResiliencePipeline<HttpResponseMessage> ResponseResiliencePipeline { get; } =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new RetryStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<Exception>(IsRetryableException)
                        .HandleResult(m => IsRetryableStatusCode(m.StatusCode)),
                    MaxRetryAttempts = MaxApiRetryCount,
                    DelayGenerator = args =>
                    {
                        // If rate-limited, use retry-after header as the guide.
                        // The response can be null here if an exception was thrown.
                        if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } retryAfter)
                        {
                            // Add some buffer just in case
                            return ValueTask.FromResult<TimeSpan?>(
                                retryAfter + TimeSpan.FromSeconds(1)
                            );
                        }

                        return ValueTask.FromResult<TimeSpan?>(RetryDelay);
                    },
                }
            )
            // The timeout is applied after the retry strategy, which means each attempt gets
            // its own timeout instead of the whole retry loop sharing a single one.
            .AddTimeout(AttemptTimeout)
            .Build();
}
