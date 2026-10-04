using System.Net;

namespace Xur.IO;

/// <summary>Retry bodyless reads before a response is handed to the caller. Never replay mutations.</summary>
public sealed class HttpRetryHandler(HttpMessageHandler inner, int retries = 2,
    TimeSpan? baseDelay = null, TimeSpan? maximumWait = null) : DelegatingHandler(inner)
{
    readonly TimeSpan delay = baseDelay ?? TimeSpan.FromMilliseconds(200);
    readonly TimeSpan waitLimit = maximumWait ?? TimeSpan.FromSeconds(2);

    public static bool Transient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public static bool Transient(HttpRequestException error) => error.StatusCode is { } code ? Transient(code) :
        error.HttpRequestError is HttpRequestError.Unknown or HttpRequestError.ConnectionError or
        HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null || request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return await base.SendAsync(request, cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            using var copy = Copy(request);
            HttpResponseMessage? response = null;
            try { response = await base.SendAsync(copy, cancellationToken); }
            catch (HttpRequestException error) when (attempt < retries && Transient(error)) { }
            if (response is not null && (attempt >= retries || !Transient(response.StatusCode)))
            {
                response.RequestMessage = request;
                return response;
            }
            var retryAfter = response?.Headers.RetryAfter;
            var pause = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow :
                TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(2, attempt) + (delay == TimeSpan.Zero ? 0 : Random.Shared.Next(100))));
            // An excessive server wait is returned to the caller, never retried earlier than requested.
            if (pause > waitLimit && response is not null)
            {
                response.RequestMessage = request;
                return response;
            }
            response?.Dispose();
            await Task.Delay(pause < TimeSpan.Zero ? TimeSpan.Zero : pause, cancellationToken);
        }
    }

    static HttpRequestMessage Copy(HttpRequestMessage request)
    {
        var result = new HttpRequestMessage(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) result.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) result.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return result;
    }
}
