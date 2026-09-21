using System.Text.Json.Nodes;
using Azure.Communication;
using Tpcli.Contracts;

namespace Tpcli.Azure.Tests;

public sealed class AudioTests
{
    internal const string Metadata = """{"kind":"AudioMetadata","audioMetadata":{"subscriptionId":"fixture","encoding":"PCM","sampleRate":24000,"channels":1,"length":960}}""";

    [Fact]
    public async Task TwoSecondBoundIncludesInFlightAudioAndClearDoesNotReplay()
    {
        using var buffer = new AudioBuffer(TimeProvider.System);
        buffer.Enqueue(new byte[96_000]);
        var inFlight = await buffer.TakeAsync(CancellationToken.None);
        Assert.Equal(96_000, buffer.ReservedBytes);
        Assert.Equal("MEDIA_BACKLOG_EXCEEDED", Assert.Throws<ProviderException>(() => buffer.Enqueue(new byte[2])).Code);
        buffer.Clear();
        Assert.Equal(960, buffer.ReservedBytes);
        buffer.Release(inFlight);
        Assert.Equal(0, buffer.ReservedBytes);
        buffer.Enqueue([1, 2]);
        var next = await buffer.TakeAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2 }, next.Bytes);
        buffer.Release(next);
    }

    [Fact]
    public async Task StaleFramesFailInsteadOfPlayingAfterAnOutage()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var buffer = new AudioBuffer(clock);
        buffer.Enqueue(new byte[960]);
        clock.Advance(TimeSpan.FromSeconds(2.1));
        var error = await Assert.ThrowsAsync<ProviderException>(() => buffer.TakeAsync(CancellationToken.None));
        Assert.Equal("MEDIA_BACKLOG_STALE", error.Code);
        Assert.Equal(0, buffer.ReservedBytes);
    }

    [Theory]
    [InlineData("16000", "1", "PCM")]
    [InlineData("24000", "2", "PCM")]
    [InlineData("24000", "1", "MULAW")]
    public void MetadataMustBePcm16Mono24Khz(string rate, string channels, string encoding)
    {
        var metadata = Metadata.Replace("24000", rate).Replace("\"channels\":1", "\"channels\":" + channels).Replace("PCM", encoding);
        var error = Assert.Throws<ProviderException>(() => new AcsMediaProtocol(Fixture.Recipient).Read(metadata));
        Assert.Equal("MEDIA_FORMAT_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void RecipientOnlyInputExcludesApplicationAudio()
    {
        var protocol = new AcsMediaProtocol(Fixture.Recipient);
        protocol.Read(Metadata);
        Assert.True(protocol.MetadataReceived);
        Assert.Equal(new byte[] { 1, 2 }, protocol.Read(Packet(Fixture.Recipient))!.Value.ToArray());
        Assert.Null(protocol.Read(Packet("+12025550125")));
        Assert.Throws<ProviderException>(() => protocol.Read(Metadata));
    }

    [Fact]
    public void AudioBeforeMetadataAndMissingParticipantAreRejected()
    {
        Assert.Equal("MEDIA_METADATA_REQUIRED",
            Assert.Throws<ProviderException>(() => new AcsMediaProtocol(Fixture.Recipient).Read(Packet(Fixture.Recipient))).Code);
        var protocol = new AcsMediaProtocol(Fixture.Recipient);
        protocol.Read(Metadata);
        var packet = JsonNode.Parse(Packet(Fixture.Recipient))!;
        packet["audioData"]!.AsObject().Remove("participantRawID");
        Assert.Throws<ProviderException>(() => protocol.Read(packet.ToJsonString()));
    }

    [Fact]
    public void ProviderTimestampRejectsAlreadyStaleOrReplayedInput()
    {
        var protocol = new AcsMediaProtocol(Fixture.Recipient);
        protocol.Read(Metadata);
        var packet = Packet(Fixture.Recipient);
        protocol.Read(packet);
        Assert.Equal("MEDIA_TIMESTAMP_REPLAY", Assert.Throws<ProviderException>(() => protocol.Read(packet)).Code);
        var stale = JsonNode.Parse(packet)!;
        stale["audioData"]!["timestamp"] = DateTimeOffset.UtcNow.AddSeconds(-3).ToString("O");
        Assert.Equal("MEDIA_TIMESTAMP_STALE", Assert.Throws<ProviderException>(() => protocol.Read(stale.ToJsonString())).Code);
    }

    [Fact]
    public void OutboundPacketsUseThePinnedSdkHelpers()
    {
        var audio = JsonNode.Parse(AcsMediaProtocol.Outgoing([1, 2]))!;
        Assert.Equal("AudioData", audio["Kind"]!.GetValue<string>());
        Assert.Equal("AQI=", audio["AudioData"]!["Data"]!.GetValue<string>());
        Assert.Equal("StopAudio", JsonNode.Parse(AcsMediaProtocol.StopAudio())!["Kind"]!.GetValue<string>());
    }

    internal static string Packet(string recipient) => new JsonObject
    {
        ["kind"] = "AudioData",
        ["audioData"] = new JsonObject
        {
            ["participantRawID"] = new PhoneNumberIdentifier(recipient).RawId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["data"] = "AQI=",
            ["silent"] = false
        }
    }.ToJsonString();
}
