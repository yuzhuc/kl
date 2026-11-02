using System.Net.Http.Headers;
using System.Text.Json;

namespace DeepSeekHarness.Gui.Services;

public sealed record ConnectionTestResult(bool Success, string Message);

public interface IConnectionTestService
{
    Task<ConnectionTestResult> TestAsync(
        ChatCompletionSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiCompatibleConnectionTestService : IConnectionTestService
{
    private readonly HttpClient _http;

    public OpenAiCompatibleConnectionTestService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ConnectionTestResult> TestAsync(
        ChatCompletionSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return new ConnectionTestResult(false, "未找到 API Key，请先在设置页面填写并保存。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ResolveModelsEndpoint(settings.Endpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (body.Length > 500)
                {
                    body = body[..500];
                }
                return new ConnectionTestResult(
                    false,
                    $"连接失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{body}".Trim());
            }

            var modelCount = ReadModelCount(body);
            return new ConnectionTestResult(
                true,
                modelCount is null
                    ? "连接成功，模型服务可访问。"
                    : $"连接成功，检测到 {modelCount} 个模型。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, "连接测试超时，请检查 Endpoint 和网络。");
        }
        catch (Exception error) when (error is HttpRequestException or UriFormatException or JsonException)
        {
            return new ConnectionTestResult(false, $"连接失败：{error.Message}");
        }
    }

    private static Uri ResolveModelsEndpoint(string endpoint)
    {
        var value = endpoint.Trim().TrimEnd('/');
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/chat/completions".Length];
        }
        if (value.Length == 0)
        {
            throw new InvalidOperationException("API Endpoint 不能为空。");
        }
        return new Uri(value + "/models", UriKind.Absolute);
    }

    private static int? ReadModelCount(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : null;
    }
}
