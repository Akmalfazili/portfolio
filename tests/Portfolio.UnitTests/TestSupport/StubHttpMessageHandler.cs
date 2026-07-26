using System.Net;

namespace Portfolio.UnitTests.TestSupport;

/// <summary>
/// A <see cref="HttpMessageHandler"/> that never makes a live network call — every provider test
/// in this project stubs its HTTP responses through this class (or
/// <see cref="SequencedStubHttpMessageHandler"/>) rather than hitting a real endpoint.
/// </summary>
internal sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content),
        };
        return Task.FromResult(response);
    }
}

/// <summary>Stub that returns one canned response per call, in order — for tests that exercise
/// more than one HTTP call against the same provider instance.</summary>
internal sealed class SequencedStubHttpMessageHandler(params (HttpStatusCode Status, string Content)[] responses) : HttpMessageHandler
{
    private int _index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, content) = responses[_index];
        _index = Math.Min(_index + 1, responses.Length - 1);

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content),
        };
        return Task.FromResult(response);
    }
}
