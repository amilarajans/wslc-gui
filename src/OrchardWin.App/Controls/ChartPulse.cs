using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OrchardWin.App.Controls;

/// <summary>
/// App-wide 1 Hz UI pulse for every chart control (MetricChart, Sparkline, MiniBarChart).
/// Independent of whether StatsService has delivered new samples — charts always redraw
/// (with a zero baseline when empty).
/// </summary>
internal static class ChartPulse
{
    private static readonly object Gate = new();
    private static DispatcherTimer? _timer;
    private static DispatcherQueue? _queue;
    private static int _subscribers;
    private static event Action? Ticked;

    /// Sampling / paint cadence shared by all charts.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public static void Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Gate)
        {
            Ticked += handler;
            _subscribers++;
            EnsureTimer_NoLock();
        }
    }

    public static void Unsubscribe(Action handler)
    {
        if (handler is null) return;
        lock (Gate)
        {
            Ticked -= handler;
            _subscribers = Math.Max(0, _subscribers - 1);
            if (_subscribers == 0)
                StopTimer_NoLock();
        }
    }

    /// Pad any series so MetricChart / Sparkline can stroke a path (≥2 points).
    public static IReadOnlyList<double> EnsureDrawable(IReadOnlyList<double>? values)
    {
        if (values is null || values.Count == 0) return [0.0, 0.0];
        if (values.Count == 1) return [values[0], values[0]];
        return values;
    }

    private static void EnsureTimer_NoLock()
    {
        if (_timer is not null) return;

        // Prefer the current UI dispatcher; fall back if none (tests).
        _queue = DispatcherQueue.GetForCurrentThread();
        if (_queue is null)
        {
            // Defer until a UI thread attaches a chart.
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) =>
        {
            Action? handlers;
            lock (Gate) handlers = Ticked;
            handlers?.Invoke();
        };
        _timer.Start();
    }

    private static void StopTimer_NoLock()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
        _queue = null;
    }

    /// Call from a control's Loaded handler if the first subscribe happened off-UI.
    public static void EnsureStartedOnUiThread()
    {
        lock (Gate)
        {
            if (_subscribers > 0 && _timer is null)
                EnsureTimer_NoLock();
        }
    }
}
