using System.Net.Http.Headers;
using System.Text.Json;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;

namespace ContinuityBridge.TestAgent;

internal sealed class RunnerQa : IRunnerQa
{
    private readonly HttpClient client;
    internal RunnerQa(Uri origin, string secret, HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
    public Task<QaSnapshot> PollAsync(QaPoll poll, CancellationToken cancellationToken) => PostAsync("/qa/v1/poll", poll, cancellationToken);
    public Task<QaSnapshot> ResultAsync(QaResult result, CancellationToken cancellationToken) => PostAsync("/qa/v1/result", result, cancellationToken);
    private async Task<QaSnapshot> PostAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, QaWire.Json)) };
        request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new InvalidDataException("qa_http_rejected_" + (int)response.StatusCode);
        if (response.Headers.CacheControl?.NoStore != true) throw new InvalidDataException("qa_no_store_required");
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException("qa_json_required");
        byte[] bytes = await CloudTransport.ReadBoundedAsync(response.Content, 8192, deadline.Token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        CheckProperties(document.RootElement);
        return JsonSerializer.Deserialize<QaSnapshot>(bytes, QaWire.Json) ?? throw new InvalidDataException();
    }
    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException();
            CheckProperties(property.Value);
        }
    }
    public void Dispose() => client.Dispose();
}
