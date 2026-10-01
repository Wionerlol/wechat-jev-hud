using System.Collections.Immutable;
using WeChatJevHud.Observer;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Overlay;

/// <summary>Single runtime-thread owned; no text or inference here. Disappearance uses observations, not time.</summary>
public sealed class HudLifecycle(int missingObservations = 2, Action<HudTrace>? trace = null)
{
    private sealed record Item(HudCard Card, int Missing = 0, bool HideMissing = false);
    private readonly Dictionary<HudKey, Item> _items = [];
    private readonly HashSet<HudKey> _used = [];
    private Dictionary<string, VisibleMessageSnapshot> _visible = [];
    private readonly JudgmentComposer _composer = new();
    private long _epoch, _sequence;
    private bool _hidden;
    public ImmutableArray<HudCard> Cards => _hidden ? [] : _items.Values.Where(i => !i.HideMissing).Select(i => i.Card).ToImmutableArray();
    public IReadOnlyList<HudKey> ActiveKeys => _items.Keys.ToArray();

    private void TraceItems(IReadOnlyList<VisibleMessageSnapshot> current)
    {
        if (trace is null) return;
        foreach (var (key, item) in _items)
            trace(new("hud_lifecycle", new
            {
                key,
                exists = true,
                visible_now = current.Any(v => v.LogicalMessageId == key.MessageId),
                missing_count = item.Missing,
                hide_missing = item.HideMissing,
                temporarily_hidden = _hidden,
                presentation_state = item.Card.Presentation.Rows.IsEmpty ? "Pending" : "Ready",
                bubble_rect = item.Card.Bubble
            }));
    }

    public void Observe(long epoch, IReadOnlyList<VisibleMessageSnapshot> visible, bool changed, bool temporarilyHidden)
    {
        if (epoch != _epoch) { _items.Clear(); _used.Clear(); _epoch = epoch; }
        _hidden = temporarilyHidden;
        if (temporarilyHidden) { TraceItems(visible); return; } // Pending identity/layout/background is not disappearance evidence.
        _visible = visible.ToDictionary(v => v.LogicalMessageId, StringComparer.Ordinal);
        foreach (var (key, item) in _items.ToArray())
        {
            if (_visible.TryGetValue(key.MessageId, out var bubble))
                _items[key] = item with { Card = item.Card with { Bubble = bubble.BubbleRect }, Missing = 0, HideMissing = false };
            else if (changed)
            {
                if (item.Missing + 1 >= missingObservations)
                {
                    _items.Remove(key);
                    trace?.Invoke(new("hud_lifecycle", new
                    {
                        key,
                        exists = false,
                        visible_now = false,
                        missing_count = item.Missing + 1,
                        hide_missing = true,
                        presentation_state = "Retired",
                        bubble_rect = item.Card.Bubble
                    }));
                }
                else _items[key] = item with { Missing = item.Missing + 1 };
            }
            else if (item.Missing > 0)
                // A static offscreen view must not leave a ghost indefinitely. Hide after grace,
                // but unchanged observations still do not advance permanent retirement.
                _items[key] = item with { HideMissing = true };
        }
        TraceItems(visible);
    }
    public bool Schedule(ObservedMessage message, JevStatus status)
    {
        var key = new HudKey(message.ConversationEpochId, message.Id);
        var reason = _hidden ? "hidden" : status != JevStatus.Queued ? "jev_not_queued"
            : !JevContextBuilder.IsEligible(message, _epoch) ? "not_eligible"
            : !_visible.ContainsKey(message.Id) ? "not_visible" : _used.Contains(key) ? "already_used"
            : _used.Count >= 2048 ? "capacity" : "success";
        trace?.Invoke(new("hud_schedule", new { key, accepted = reason == "success", reason }));
        if (reason != "success") return false;
        _used.Add(key);
        _items.Add(key, new(new(key, _visible[message.Id].BubbleRect, HudPresentationModel.Pending, ++_sequence)));
        return true;
    }
    public bool Apply(JevAnalysisResult result)
    {
        var key = new HudKey(result.ConversationEpochId, result.MessageId);
        var reason = key.Epoch != _epoch ? "wrong_epoch" : !_items.ContainsKey(key) ? "item_missing"
            : !_visible.ContainsKey(key.MessageId) ? "target_not_visible" : "success";
        if (reason != "success")
        {
            trace?.Invoke(new("hud_apply", new { key, result_status = result.Status.ToString(), applied = false, reason }));
            return false;
        }
        var item = _items[key];
        var presentation = _composer.Compose(result);
        if (presentation is null)
        {
            _items.Remove(key);
            trace?.Invoke(new("hud_apply", new { key, result_status = result.Status.ToString(), applied = false, reason = "compose_failed" }));
            return false;
        }
        _items[key] = item with { Card = item.Card with { Presentation = presentation } };
        trace?.Invoke(new("hud_apply", new { key, result_status = result.Status.ToString(), applied = true, reason = "success" }));
        return true;
    }
    public void Clear() { _items.Clear(); _used.Clear(); _visible.Clear(); }
}
