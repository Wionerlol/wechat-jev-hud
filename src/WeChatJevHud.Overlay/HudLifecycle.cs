using System.Collections.Immutable;
using WeChatJevHud.Observer;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Overlay;

/// <summary>Single runtime-thread owned; no text or inference here. Disappearance uses observations, not time.</summary>
public sealed class HudLifecycle(int missingObservations = 2)
{
    private sealed record Item(HudCard Card, int Missing = 0, bool HideMissing = false);
    private readonly Dictionary<HudKey, Item> _items = [];
    private readonly HashSet<HudKey> _used = [];
    private Dictionary<string, VisibleMessageSnapshot> _visible = [];
    private readonly JudgmentComposer _composer = new();
    private long _epoch, _sequence;
    private bool _hidden;
    public ImmutableArray<HudCard> Cards => _hidden ? [] : _items.Values.Where(i => !i.HideMissing).Select(i => i.Card).ToImmutableArray();

    public void Observe(long epoch, IReadOnlyList<VisibleMessageSnapshot> visible, bool changed, bool temporarilyHidden)
    {
        if (epoch != _epoch) { _items.Clear(); _used.Clear(); _epoch = epoch; }
        _hidden = temporarilyHidden;
        if (temporarilyHidden) return; // Pending identity/layout/background is not disappearance evidence.
        _visible = visible.ToDictionary(v => v.LogicalMessageId, StringComparer.Ordinal);
        foreach (var (key, item) in _items.ToArray())
        {
            if (_visible.TryGetValue(key.MessageId, out var bubble))
                _items[key] = item with { Card = item.Card with { Bubble = bubble.BubbleRect }, Missing = 0, HideMissing = false };
            else if (changed)
            {
                if (item.Missing + 1 >= missingObservations) _items.Remove(key);
                else _items[key] = item with { Missing = item.Missing + 1 };
            }
            else if (item.Missing > 0)
                // A static offscreen view must not leave a ghost indefinitely. Hide after grace,
                // but unchanged observations still do not advance permanent retirement.
                _items[key] = item with { HideMissing = true };
        }
    }
    public bool Schedule(ObservedMessage message, JevStatus status)
    {
        var key = new HudKey(message.ConversationEpochId, message.Id);
        if (_hidden || status != JevStatus.Queued || !JevContextBuilder.IsEligible(message, _epoch) ||
            !_visible.ContainsKey(message.Id) || _used.Contains(key) || _used.Count >= 2048) return false;
        _used.Add(key);
        _items.Add(key, new(new(key, _visible[message.Id].BubbleRect, HudPresentationModel.Pending, ++_sequence)));
        return true;
    }
    public bool Apply(JevAnalysisResult result)
    {
        var key = new HudKey(result.ConversationEpochId, result.MessageId);
        if (key.Epoch != _epoch || !_items.TryGetValue(key, out var item) || !_visible.ContainsKey(key.MessageId)) return false;
        var presentation = _composer.Compose(result);
        if (presentation is null) { _items.Remove(key); return false; }
        _items[key] = item with { Card = item.Card with { Presentation = presentation } };
        return true;
    }
    public void Clear() { _items.Clear(); _used.Clear(); _visible.Clear(); }
}
