using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class RequestBoundaryTests
{
    [Theory]
    [InlineData(65535, false)]
    [InlineData(65536, false)]
    [InlineData(65537, false)]
    [InlineData(65535, true)]
    [InlineData(65536, true)]
    [InlineData(65537, true)]
    public async Task ExactRequestByteBoundaryIsIdenticalWithAndWithoutContentLength(int length, bool streamed)
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        var session = await h.CreateSessionAsync();
        using var owner = await h.ConnectAsync(session.SessionId);
        await ApiHarness.ReceiveAsync(owner);
        var json = JsonSerializer.Serialize(ApiHarness.StartRequest(session), Protocol.Json);
        var bytes = Encoding.UTF8.GetBytes(json + new string(' ', length - Encoding.UTF8.GetByteCount(json)));
        using HttpContent content = streamed ? new StreamingContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        var response = await h.Client.PostAsync("/v1/commands", content);
        Assert.Equal(length <= RuntimeSettings.MaxRequestBytes ? HttpStatusCode.Accepted : HttpStatusCode.RequestEntityTooLarge,
            response.StatusCode);
        if (length > RuntimeSettings.MaxRequestBytes)
        {
            var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal("REQUEST_TOO_LARGE", error["error"]!["code"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(16385)]
    public async Task TaskLimitCountsUtf8BytesNotCharacters(int length)
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        var session = await h.CreateSessionAsync();
        using var owner = await h.ConnectAsync(session.SessionId);
        await ApiHarness.ReceiveAsync(owner);
        var request = ApiHarness.StartRequest(session);
        var text = new string('\u03bb', length / 2) + (length % 2 == 0 ? "" : "a");
        Assert.Equal(length, Encoding.UTF8.GetByteCount(text));
        request.Payload["task"] = text;
        var response = await h.Client.PostAsJsonAsync("/v1/commands", request, Protocol.Json);
        Assert.Equal(length <= RuntimeSettings.MaxTaskBytes ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest,
            response.StatusCode);
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < bytes.Length; offset += 1024)
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(1024, bytes.Length - offset)));
        }
    }
}
