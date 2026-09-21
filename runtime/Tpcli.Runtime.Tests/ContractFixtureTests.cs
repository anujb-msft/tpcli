using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class ContractFixtureTests
{
    [Theory]
    [InlineData("command.json", typeof(CommandRequest))]
    [InlineData("event.json", typeof(EventEnvelope))]
    [InlineData("receipt.json", typeof(CommandReceipt))]
    [InlineData("receipt-failed.json", typeof(CommandReceipt))]
    [InlineData("state.json", typeof(CallState))]
    [InlineData("approval.json", typeof(ApprovalView))]
    [InlineData("summary.json", typeof(TaskSummary))]
    public async Task EverySharedFixtureRetainsItsFieldsAndRoundTrips(string filename, Type type)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", filename));
        var expected = JsonNode.Parse(text)!.AsObject();
        var typed = JsonSerializer.Deserialize(text, type, Protocol.Json)!;
        var actual = JsonSerializer.SerializeToNode(typed, type, Protocol.Json)!.AsObject();
        foreach (var (name, value) in expected)
        {
            if (value is null)
            {
                Assert.Null(actual[name]);
                continue;
            }
            Assert.True(actual.ContainsKey(name), $"Contract lost {filename}:{name}");
            if (name is "accepted_at" or "updated_at" or "created_at" or "deadline" or "expires_at" or "timestamp")
            {
                Assert.Equal(
                    DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(actual[name]!.GetValue<string>(), CultureInfo.InvariantCulture));
            }
            else
            {
                Assert.True(JsonNode.DeepEquals(value, actual[name]), $"Contract changed {filename}:{name}");
            }
        }
        var encoded = JsonSerializer.Serialize(typed, type, Protocol.Json);
        Assert.Equal(encoded, JsonSerializer.Serialize(JsonSerializer.Deserialize(encoded, type, Protocol.Json), type, Protocol.Json));
    }
}
