using System.Collections.Immutable;
using WeChatJevHud.Observer;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Overlay;

/// <summary>Runtime-thread owned, bounded semantic session; visibility never retires an item.</summary>
public sealed class HudLifecycle
{
    private readonly Dictionary<HudKey, HudCard> _items = [];
    private readonly HashSet<HudKey> _used = [];
    private Dictionary<string, VisibleMessageSnapshot> _visible = [];
    private readonly JudgmentComposer _composer = new();
    private readonly int _maxTrackedSemanticItems;
    private readonly Action<HudTrace>? _trace;
    private long _epoch, _sequence;
    private bool _hidden;

    public HudLifecycle(int maxTrackedSemanticItems = 25, Action<HudTrace>? trace = null)
    {
        if (maxTrackedSemanticItems < 1) throw new ArgumentOutOfRangeException(nameof(maxTrackedSemanticItems));
        _maxTrackedSemanticItems = maxTrackedSemanticItems;
        _trace = trace;
    }
    public ImmutableArray<HudCard> TrackedItems => _items.Values.OrderBy(c => c.Sequence).ToImmutableArray();
    public ImmutableArray<HudCard> Cards => _hidden ? [] : TrackedItems.Where(c => !c.IsUnavailable).ToImmutableArray();
    public IReadOnlyList<HudKey> ActiveKeys => _items.Keys.ToArray();

    public void Observe(long epoch, IReadOnlyList<VisibleMessageSnapshot> visible, bool changed, bool temporarilyHidden)
    {
        if (epoch != _epoch) { Clear(); _epoch = epoch; }
        _hidden = temporarilyHidden;
        // Pending identity/layout/background does not provide message absence evidence.
        if (!temporarilyHidden)
        {
            _visible = visible.ToDictionary(v => v.LogicalMessageId, StringComparer.Ordinal);
            foreach (var (key, item) in _items.ToArray())
                _items[key] = _visible.TryGetValue(key.MessageId, out var bubble)
                    ? item with { Bubble = bubble.BubbleRect, Visibility = HudVisibilityState.Onscreen }
                    : item with { Visibility = HudVisibilityState.OffscreenRetained };
        }
        foreach (var (key, item) in _items)
            _trace?.Invoke(new("hud_lifecycle", new
            {
                key,
                exists = true,
                visible_now = item.CurrentBubbleRect is not null,
                visibility = item.Visibility.ToString(),
                ordinal = item.DisplayOrdinal,
                temporarily_hidden = _hidden,
                presentation_state = item.IsUnavailable ? "Unavailable" : item.Presentation.Rows.IsEmpty ? "Pending" : "Ready",
                bubble_rect = item.CurrentBubbleRect,
                last_known_bubble_rect = item.LastKnownBubbleRect
            }));
    }
    public bool Schedule(ObservedMessage message, JevStatus status)
    {
        var key = new HudKey(message.ConversationEpochId, message.Id);
        var reason = _hidden ? "hidden" : status != JevStatus.Queued ? "jev_not_queued"
            : !JevContextBuilder.IsEligible(message, _epoch) ? "not_eligible"
            : !_visible.ContainsKey(message.Id) ? "not_visible" : _used.Contains(key) ? "already_used"
            : _used.Count >= 2048 ? "capacity" : "success";
        _trace?.Invoke(new("hud_schedule", new { key, accepted = reason == "success", reason }));
        if (reason != "success") return false;
        _used.Add(key);
        _items.Add(key, new(key, _visible[message.Id].BubbleRect, HudPresentationModel.Pending, ++_sequence));
        if (_items.Count > _maxTrackedSemanticItems)
        {
            var oldest = _items.Values.MinBy(c => c.Sequence)!;
            _items.Remove(oldest.Key);
            _trace?.Invoke(new("hud_retired", new { key = oldest.Key, ordinal = oldest.DisplayOrdinal, reason = "capacity_eviction" }));
        }
        return true;
    }
    public bool Apply(JevAnalysisResult result)
    {
        var key = new HudKey(result.ConversationEpochId, result.MessageId);
        var reason = key.Epoch != _epoch ? "wrong_epoch" : !_items.ContainsKey(key) ? "item_missing" : "success";
        if (reason != "success")
        {
            _trace?.Invoke(new("hud_apply", new { key, result_status = result.Status.ToString(), applied = false, reason }));
            return false;
        }
        var item = _items[key];
        var presentation = _composer.Compose(result);
        if (presentation is null)
        {
            // Retain session identity; normal product presentation hides unavailable analyses.
            _items[key] = item with { IsUnavailable = true };
            _trace?.Invoke(new("hud_apply", new { key, result_status = result.Status.ToString(), applied = false, reason = "compose_failed" }));
            return false;
        }
        _items[key] = item with { Presentation = presentation, IsUnavailable = false };
        _trace?.Invoke(new("hud_apply", new { key, result_status = result.Status.ToString(), applied = true, reason = "success" }));
        return true;
    }
    public void Clear()
    {
        _items.Clear(); _used.Clear(); _visible.Clear(); _sequence = 0; _epoch = 0; _hidden = false;
    }
}
