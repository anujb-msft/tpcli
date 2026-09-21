using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed class TranscriptTracker(Action<ProviderSignal> publish)
{
    private sealed class Segment(string speaker)
    {
        internal string Speaker = speaker;
        internal string Text = "";
        internal int Revision;
        internal bool Final;
        internal bool Interrupted;
        internal string Delivery = speaker == "recipient" ? "received" : "generated";
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Segment> _segments = new(StringComparer.Ordinal);
    private int _characters;

    internal void Text(string id, string speaker, string text, bool final)
    {
        lock (_gate)
        {
            var segment = Get(id, speaker);
            if (segment.Final && !final)
                return;
            var updated = final ? text : segment.Text + text;
            if (updated.Length > AzureValidation.MaximumTextLength ||
                _characters - segment.Text.Length + updated.Length > 2 * 1024 * 1024)
                throw new ProviderException("TRANSCRIPT_CAPACITY_EXCEEDED");
            if (segment.Final && final && updated == segment.Text)
                return;
            _characters += updated.Length - segment.Text.Length;
            segment.Text = updated;
            segment.Final |= final;
            Emit(id, segment, final ? "transcript.final" : "transcript.partial");
        }
    }

    internal void Sent(string id)
    {
        lock (_gate)
        {
            if (_segments.TryGetValue(id, out var segment) && segment.Final && !segment.Interrupted && segment.Delivery != "sent")
            {
                segment.Delivery = "sent";
                Emit(id, segment, "transcript.final");
            }
        }
    }

    internal void Interrupt(string id)
    {
        lock (_gate)
        {
            var segment = Get(id, "assistant");
            if (segment.Interrupted) return;
            segment.Interrupted = true;
            // The complete generated sentence is not evidence of complete playback.
            segment.Delivery = "unknown";
            Emit(id, segment, "transcript.interrupted");
        }
    }

    internal bool HasText(string id)
    {
        lock (_gate) return _segments.TryGetValue(id, out var segment) && segment.Text.Length != 0;
    }

    private Segment Get(string id, string speaker)
    {
        if (id.Length > 1024) throw new ProviderException("VOICE_PROTOCOL_INVALID");
        if (_segments.TryGetValue(id, out var segment)) return segment;
        if (_segments.Count >= 512) throw new ProviderException("TRANSCRIPT_CAPACITY_EXCEEDED");
        segment = new Segment(speaker);
        _segments.Add(id, segment);
        return segment;
    }

    private void Emit(string id, Segment segment, string type) => publish(new ProviderSignal(type, new JsonObject
    {
        ["segment_id"] = id,
        ["speaker"] = segment.Speaker,
        ["text"] = segment.Text,
        ["revision"] = ++segment.Revision,
        ["final"] = segment.Final,
        ["interrupted"] = segment.Interrupted,
        ["delivery"] = segment.Delivery
    }));
}
