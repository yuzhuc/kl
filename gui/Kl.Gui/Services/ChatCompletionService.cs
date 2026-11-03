using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Kl.Gui.Services;

public sealed record ChatRequestMessage(string Role, string Content);

public sealed record ChatToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string? Arguments);

public sealed record ChatStreamDelta(
    string Reasoning,
    string Content,
    IReadOnlyList<ChatToolCallDelta>? ToolCalls = null);

public sealed record ChatCompletionSettings(string Endpoint, string Model, string ApiKey);

public interface IChatCompletionService
{
    IAsyncEnumerable<ChatStreamDelta> StreamAsync(
        ChatCompletionSettings settings,
        IReadOnlyList<ChatRequestMessage> messages,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiCompatibleChatService : IChatCompletionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;

    public OpenAiCompatibleChatService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async IAsyncEnumerable<ChatStreamDelta> StreamAsync(
        ChatCompletionSettings settings,
        IReadOnlyList<ChatRequestMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException(
                "未找到 API Key。请先到设置页面填写并保存。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(settings.Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = settings.Model,
                messages = messages.Select(message => new
                {
                    role = message.Role,
                    content = message.Content
                }),
                stream = true
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (detail.Length > 2000)
            {
                detail = detail[..2000];
            }
            throw new InvalidOperationException(
                $"模型请求失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                yield break;
            }
            if (data.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out var delta))
            {
                continue;
            }

            var reasoning = GetString(delta, "reasoning_content") ?? GetString(delta, "reasoning") ?? "";
            var content = GetString(delta, "content") ?? "";
            var toolCalls = GetToolCalls(delta);
            if (reasoning.Length > 0 || content.Length > 0 || toolCalls.Count > 0)
            {
                yield return new ChatStreamDelta(reasoning, content, toolCalls);
            }
        }
    }

    private static List<ChatToolCallDelta> GetToolCalls(JsonElement delta)
    {
        var result = new List<ChatToolCallDelta>();
        if (!delta.TryGetProperty("tool_calls", out var calls)
            || calls.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var fallbackIndex = 0;
        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement)
                && indexElement.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : fallbackIndex;
            fallbackIndex++;
            var id = GetString(call, "id");
            string? name = null;
            string? arguments = null;
            if (call.TryGetProperty("function", out var function))
            {
                name = GetString(function, "name");
                arguments = GetString(function, "arguments");
            }
            result.Add(new ChatToolCallDelta(index, id, name, arguments));
        }
        return result;
    }

    private static Uri ResolveEndpoint(string endpoint)
    {
        var value = endpoint.Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            throw new InvalidOperationException("API Endpoint 不能为空。");
        }
        if (!value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            value += "/chat/completions";
        }
        return new Uri(value, UriKind.Absolute);
    }

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
