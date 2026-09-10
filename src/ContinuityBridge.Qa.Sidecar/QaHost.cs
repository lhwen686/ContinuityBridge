using System.Text.Json;
using ContinuityBridge.Qa.Protocol;

namespace ContinuityBridge.Qa.Sidecar;

public static class QaHost
{
    public static WebApplication Build(string[] args, QaLease? testLease = null)
    {
        var builder = WebApplication.CreateBuilder(args); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 4096;
            options.Limits.MaxConcurrentConnections = 16; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        QaLease lease = testLease ?? JsonSerializer.Deserialize<QaLease>(File.ReadAllBytes(
            builder.Configuration["Qa:LeaseFile"] ?? throw new InvalidDataException("qa_lease_required")), QaWire.Json)!;
        var mailbox = new QaMailbox(lease, CandidateBuild.Sha);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                bool runner = context.Request.Path is var path && (path == "/qa/v1/poll" || path == "/qa/v1/result");
                mailbox.Authenticate(context.Request.Headers.Authorization.ToString(), runner);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(10)); context.RequestAborted = timeout.Token;
                await next(context).ConfigureAwait(false);
            }
            catch (QaRequestException ex) { context.Response.StatusCode = ex.Status; }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidDataException or OperationCanceledException)
            { context.Response.StatusCode = 400; }
        });
        // Explicit Func selects the route-handler overload. RequestDelegate would
        // await Task<IResult> as Task and silently discard the authenticated JSON.
        app.MapPost("/qa/v1/commands", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(mailbox.Submit(await Read<QaCommand>(context).ConfigureAwait(false)), QaWire.Json)));
        app.MapPost("/qa/v1/poll", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(mailbox.Poll(await Read<QaPoll>(context).ConfigureAwait(false)), QaWire.Json)));
        app.MapPost("/qa/v1/result", (Func<HttpContext, Task<IResult>>)(async context => Results.Json(mailbox.Acknowledge(await Read<QaResult>(context).ConfigureAwait(false)), QaWire.Json)));
        app.MapGet("/qa/v1/status", () => Results.Json(mailbox.Status(), QaWire.Json));
        return app;
    }

    private static async Task<T> Read<T>(HttpContext context)
    {
        if (context.Request.ContentType != "application/json" || context.Request.Headers.ContainsKey("Content-Encoding")) throw new QaRequestException(415);
        using var output = new MemoryStream(); byte[] buffer = new byte[4097]; int total = 0;
        while (true)
        {
            int count = await context.Request.Body.ReadAsync(buffer.AsMemory(0, 4097 - total), context.RequestAborted).ConfigureAwait(false);
            if (count == 0) break;
            total += count; if (total > 4096) throw new QaRequestException(413); output.Write(buffer, 0, count);
        }
        byte[] bytes = output.ToArray();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new QaRequestException(400);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new QaRequestException(400);
        return JsonSerializer.Deserialize<T>(bytes, QaWire.Json) ?? throw new QaRequestException(400);
    }
}
