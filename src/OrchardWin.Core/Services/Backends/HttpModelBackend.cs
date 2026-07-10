using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// Discovers local model providers by probing conventional loopback ports over HTTP.
/// An unreachable port means "that provider isn't running."
public sealed class HttpModelBackend : IModelBackend
{
    private sealed record Candidate(ModelProviderKind Kind, ushort Port, ModelApiStyle Api, string ListPath);

    // Documented defaults: Ollama 11434, LM Studio 1234. User-changed ports are not probed.
    private static readonly Candidate[] Candidates =
    [
        new(ModelProviderKind.Ollama, 11434, ModelApiStyle.Ollama, "/api/tags"),
        // Ollama also exposes an OpenAI-compatible listing (newer installs).
        new(ModelProviderKind.Ollama, 11434, ModelApiStyle.OpenAI, "/v1/models"),
        new(ModelProviderKind.LmStudio, 1234, ModelApiStyle.OpenAI, "/v1/models"),
    ];

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost"];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;

    public HttpModelBackend(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            return;
        }

        // Bypass system proxy — corporate proxies often break loopback probes and make a
        // running Ollama look "not detected".
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<IReadOnlyList<ModelProvider>> DetectProvidersAsync(CancellationToken ct = default)
    {
        // Dedupe by Kind+Port (we may probe the same port via multiple list paths).
        var found = new Dictionary<string, ModelProvider>(StringComparer.Ordinal);
        var probes = Candidates
            .SelectMany(c => LoopbackHosts.Select(host => ProbeAsync(host, c, ct)))
            .ToArray();
        var results = await Task.WhenAll(probes);
        foreach (var provider in results)
        {
            if (provider is null) continue;
            // Prefer result with more model names if we hit the same port twice.
            if (!found.TryGetValue(provider.Id, out var existing)
                || provider.Models.Count > existing.Models.Count)
            {
                found[provider.Id] = provider;
            }
        }

        var list = found.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        Log.Backend.Debug($"model detection: {list.Count} provider(s) — {string.Join(", ", list.Select(p => $"{p.Kind}:{p.Port}"))}");
        return list;
    }

    private async Task<ModelProvider?> ProbeAsync(string host, Candidate candidate, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        var url = $"http://{host}:{candidate.Port}{candidate.ListPath}";
        try
        {
            using var response = await _http.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log.Backend.Debug($"model probe {url} → HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var models = ParseModels(body, candidate.Api);
            // /v1/models on Ollama maps to OpenAI style but we still label as Ollama.
            return new ModelProvider
            {
                Kind = candidate.Kind,
                Port = candidate.Port,
                Api = candidate.Kind == ModelProviderKind.Ollama ? ModelApiStyle.Ollama : candidate.Api,
                Models = models,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Backend.Debug($"model probe {url} timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Backend.Debug($"model probe {url} failed: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Backend.Debug($"model probe {url} error: {ex.GetType().Name}: {ex.Message}");
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
            catch (Exception ex) when (ex is IOException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
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

    internal static List<string> ParseModels(string json, ModelApiStyle api)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (api)
            {
                case ModelApiStyle.OpenAI:
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        return data.EnumerateArray()
                            .Where(entry => entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            .Select(entry => entry.GetProperty("id").GetString()!)
                            .ToList();
                    }
                    return [];
                case ModelApiStyle.Ollama:
                    if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                    {
                        return models.EnumerateArray()
                            .Select(entry =>
                            {
                                if (entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                    return name.GetString()!;
                                if (entry.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                                    return model.GetString()!;
                                return null;
                            })
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Select(s => s!)
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
