namespace EveUtils.Client.Esi.Testing;

/// <summary>Test <see cref="IHttpClientFactory"/> that always hands back the one pre-built chain client.</summary>
public sealed class SingleClientHttpFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
