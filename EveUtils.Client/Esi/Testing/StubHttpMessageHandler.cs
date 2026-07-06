namespace EveUtils.Client.Esi.Testing;

/// <summary>
/// The innermost handler in the test chain: scripts ESI responses (statuses/headers/bodies) and records
/// every request, so the <c>--esi-test</c> scenarios are deterministic with no live ESI. The
/// responder may throw to simulate a timeout/network failure.
/// </summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly List<CapturedRequest> _captured = [];

    public int Calls { get; private set; }
    public IReadOnlyList<CapturedRequest> Captured => _captured;
    public CapturedRequest Last => _captured[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Calls++;
        _captured.Add(CapturedRequest.From(request));
        await Task.Yield();
        return responder(request, index);
    }
}
