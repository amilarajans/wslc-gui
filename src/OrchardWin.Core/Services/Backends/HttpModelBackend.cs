using System.Text;
using System.Text.Json;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// <see cref="IModelBackend"/> that discovers providers by probing their conventional
/// loopback ports over HTTP. An unreachable port simply means "that provider isn't
/// running." Ported from Orchard's `LiveModelBackend` - candidate list, probe-concurrently
/// pattern, and chat-completion request/response shapes carried over verbatim, except the
/// two `mlxServer` candidates (ports 8080/8000) are dropped entirely: MLX is an Apple
/// Silicon-only inference framework with no Windows build, so there is nothing to probe for.
public sealed class HttpModelBackend : IModelBackend
{
    private sealed record Candidate(ModelProviderKind Kind, ushort Port, ModelApiStyle Api, string ListPath);

    // VERIFY: these are each provider's documented default port - Ollama's local server
    // listens on 11434, LM Studio's on 1234 - both are user-configurable in their
    // respective apps, so a provider moved off its default port will not be detected. This
    // mirrors Orchard's own "conventional defaults" caveat on `LiveModelBackend.candidates`.
    private static readonly Candidate[] Candidates =
    [
        new(ModelProviderKind.Ollama, 11434, ModelApiStyle.Ollama, "/api/tags"),
        new(ModelProviderKind.LmStudio, 1234, ModelApiStyle.OpenAI, "/v1/models"),
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;

    public HttpModelBackend(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public async Task<IReadOnlyList<ModelProvider>> DetectProvidersAsync(CancellationToken ct = default)
    {
        var probes = Candidates.Select(candidate => ProbeAsync(candidate, ct)).ToArray();
        var results = await Task.WhenAll(probes);
        return results
            .Where(provider => provider is not null)
            .Select(provider => provider!)
            .OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<ModelProvider?> ProbeAsync(Candidate candidate, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{candidate.Port}{candidate.ListPath}", cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return new ModelProvider
            {
                Kind = candidate.Kind,
                Port = candidate.Port,
                Api = candidate.Api,
                Models = ParseModels(body, candidate.Api),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own short probe timeout tripped, not the caller's token - treat exactly
            // like a connection failure: that provider just isn't running.
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Backend.Debug($"model provider probe failed for {candidate.Kind} on {candidate.Port}: {ex.Message}");
            return null;
        }
    }

    public async Task<string> CompleteAsync(ushort port, ModelApiStyle api, string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var root = $"http://127.0.0.1:{port}";
        var wireMessages = messages
            .Select(message => new Dictionary<string, object> { ["role"] = RoleString(message.Role), ["content"] = message.Content })
            .ToList();

        string path;
        object body;
        switch (api)
        {
            case ModelApiStyle.OpenAI:
                path = "/v1/chat/completions";
                body = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = wireMessages,
                    ["max_tokens"] = 512,
                    ["temperature"] = 0.7,
                };
                break;
            case ModelApiStyle.Ollama:
                path = "/api/chat";
                body = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = wireMessages,
                    ["stream"] = false,
                };
                break;
            default:
                throw OrchardWinException.Generic("Invalid model endpoint.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CompleteTimeout);

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(root + path, content, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw OrchardWinException.Generic("No response from the model server.");
        }
        catch (HttpRequestException ex)
        {
            throw OrchardWinException.Generic($"No response from the model server: {ex.Message}");
        }

        using (response)
        {
            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw OrchardWinException.Generic($"Could not read the model server's response: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = responseBody.Length > 300 ? responseBody[..300] : responseBody;
                throw OrchardWinException.Generic($"Model server returned HTTP {(int)response.StatusCode}. {detail}");
            }
            return ParseCompletion(responseBody, api);
        }
    }

    private static string RoleString(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => role.ToString().ToLowerInvariant(),
    };

    /// Extract the assistant's reply text from a chat-completion response. Both shapes are
    /// flat JSON, so this is parsed dynamically (via <see cref="JsonDocument"/>) rather than
    /// against a typed Models class - these are third-party wire shapes we don't own, unlike
    /// wslc's own JSON that the Models types are attributed against.
    internal static string ParseCompletion(string json, ModelApiStyle api)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw OrchardWinException.Generic("Could not read the model server's response.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            switch (api)
            {
                case ModelApiStyle.OpenAI:
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.TryGetProperty("message", out var message)
                            && message.TryGetProperty("content", out var content)
                            && content.ValueKind == JsonValueKind.String)
                        {
                            return content.GetString()!;
                        }
                    }
                    break;
                case ModelApiStyle.Ollama:
                    if (root.TryGetProperty("message", out var ollamaMessage)
                        && ollamaMessage.TryGetProperty("content", out var ollamaContent)
                        && ollamaContent.ValueKind == JsonValueKind.String)
                    {
                        return ollamaContent.GetString()!;
                    }
                    break;
            }
        }

        throw OrchardWinException.Generic("The model server returned an unexpected response shape.");
    }

    /// Extract model ids from a provider's listing response. Both shapes are flat JSON.
    internal static List<string> ParseModels(string json, ModelApiStyle api)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (api)
            {
                case ModelApiStyle.OpenAI:
                    // { "data": [ { "id": "..." }, ... ] }
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        return data.EnumerateArray()
                            .Where(entry => entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            .Select(entry => entry.GetProperty("id").GetString()!)
                            .ToList();
                    }
                    return [];
                case ModelApiStyle.Ollama:
                    // { "models": [ { "name": "..." }, ... ] }
                    if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                    {
                        return models.EnumerateArray()
                            .Where(entry => entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                            .Select(entry => entry.GetProperty("name").GetString()!)
                            .ToList();
                    }
                    return [];
                default:
                    return [];
            }
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
