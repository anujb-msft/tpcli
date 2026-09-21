using System.Text;
using System.Text.Json.Nodes;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class ResultContentTests
{
    private static readonly string[] Fields = ["facts", "commitments", "outstanding_items", "source_references"];

    [Fact]
    public async Task CompletedSummaryPreservesAllFourArraysThroughEndWithoutDurableContent()
    {
        await using var h = new Harness(settings => settings.Fake.EmitTranscript = false);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        var arguments = new JsonObject { ["outcome"] = "completed", ["summary"] = "Summary-" + Guid.NewGuid().ToString("N") };
        foreach (var field in Fields)
            arguments[field] = new JsonArray(field + "-" + Guid.NewGuid().ToString("N"), "second-" + field);
        await h.ToolAsync(receipt.CallId!, "report_result", arguments);
        await h.WaitStateAsync(receipt.CallId!, state => state.SummaryStatus == "complete");
        await h.ToolAsync(receipt.CallId!, "end_call", []);
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "ended");
        var batch = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!);
        var summary = Assert.Single(batch.Events, item => item.Type == "summary.ready");
        Assert.Equal("complete", summary.Payload["status"]!.GetValue<string>());
        Assert.Equal("completed", summary.Payload["task_outcome"]!.GetValue<string>());
        Assert.Equal(arguments["summary"]!.GetValue<string>(), summary.Payload["summary"]!.GetValue<string>());
        foreach (var field in Fields)
            Assert.True(JsonNode.DeepEquals(arguments[field], summary.Payload[field]), $"Summary changed {field}.");
        var stored = Assert.Single(await h.Store.TransactionAsync(tx => tx.EventsAsync(receipt.CallId!, 0)),
            item => item.Type == "summary.ready");
        Assert.Null(stored.Payload);
        var markers = Fields.Select(field => arguments[field]![0]!.GetValue<string>())
            .Append(arguments["summary"]!.GetValue<string>()).ToArray();
        foreach (var path in Directory.GetFiles(h.DirectoryPath, "control.db*"))
        {
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            foreach (var marker in markers) Assert.DoesNotContain(marker, bytes);
        }
    }

    [Theory]
    [InlineData("facts", "{}")]
    [InlineData("facts", "[1]")]
    [InlineData("commitments", "null")]
    [InlineData("commitments", "[null]")]
    [InlineData("outstanding_items", "\"not an array\"")]
    [InlineData("outstanding_items", "[{}]")]
    [InlineData("source_references", "true")]
    [InlineData("source_references", "[[]]")]
    public async Task NonStringArrayContentCannotPublishACompletedResult(string field, string invalidJson)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        var arguments = new JsonObject { ["outcome"] = "completed", ["summary"] = "Must not be accepted." };
        foreach (var name in Fields) arguments[name] = new JsonArray();
        arguments[field] = JsonNode.Parse(invalidJson);
        await h.ToolAsync(receipt.CallId!, "report_result", arguments);
        var state = await h.WaitStateAsync(receipt.CallId!, value => value.Lifecycle == "ended");
        Assert.NotEqual("completed", state.TaskOutcome);
        Assert.NotEqual("complete", state.SummaryStatus);
        Assert.DoesNotContain((await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events,
            item => item.Type == "summary.ready");
    }

    [Fact]
    public async Task AggregateStructuredResultIsBoundedByTheUnchangedProviderSignalLimit()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        var arguments = new JsonObject { ["outcome"] = "completed", ["summary"] = "Must not be accepted." };
        foreach (var field in Fields)
            arguments[field] = new JsonArray(new string('x', RuntimeSettings.MaxRequestBytes / Fields.Length));
        var error = await Assert.ThrowsAsync<ControlException>(() =>
            h.ToolAsync(receipt.CallId!, "report_result", arguments));
        Assert.Equal("INVALID_PROVIDER_SIGNAL", error.Code);
        Assert.Equal("connected", (await h.Runtime.StateAsync(h.Owner, receipt.CallId!)).Lifecycle);
        Assert.DoesNotContain((await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events,
            item => item.Type == "summary.ready");
    }
}
