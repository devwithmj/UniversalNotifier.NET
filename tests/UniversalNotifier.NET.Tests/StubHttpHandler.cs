using System.Net;
using System.Text;

namespace UniversalNotifier.NET.Tests;

/// <summary>A captured HTTP request with its body already read.</summary>
public sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Replaces the network in provider tests: records requests and answers from a script.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, HttpResponseMessage> _respond;
    private readonly List<CapturedRequest> _requests = [];

    public StubHttpHandler(Func<CapturedRequest, HttpResponseMessage> respond) => _respond = respond;

    public StubHttpHandler(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null)
        : this(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return response;
        })
    {
    }

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public CapturedRequest OnlyRequest => Requests.Single();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);

        lock (_requests)
        {
            _requests.Add(captured);
        }

        return _respond(captured);
    }
}
