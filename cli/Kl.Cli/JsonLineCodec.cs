using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kl.Cli;

public static class JsonLineCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeRequest(long id, string method, object? parameters)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["kl_protocol"] = Protocol.Version,
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        var json = JsonSerializer.Serialize(payload, Options);
        if (json.Contains('\n') || json.Contains('\r'))
        {
            throw new InvalidOperationException("JSON-RPC payload must be a single line");
        }

        return json;
    }

    public static bool TryParse(string line, out JsonDocument? document)
    {
        document = null;
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
