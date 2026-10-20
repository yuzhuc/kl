using System.Text.Json;
using Xunit;

namespace Kl.Tui.Tests;

public sealed class JsonLineCodecTests
{
    [Fact]
    public void SerializesSingleLineRequest()
    {
        var line = JsonLineCodec.SerializeRequest(7, Protocol.Ping, new { nonce = "x" });
        Assert.DoesNotContain('\n', line);

        using var document = JsonDocument.Parse(line);
        Assert.Equal(Protocol.Version, document.RootElement.GetProperty("kl_protocol").GetInt32());
        Assert.Equal(7, document.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(Protocol.Ping, document.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public void RejectsInvalidJson()
    {
        Assert.False(JsonLineCodec.TryParse("{not json", out var document));
        Assert.Null(document);
    }
}
