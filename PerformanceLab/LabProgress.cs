using System.Diagnostics;
using System.Threading;

namespace Memory2.PerformanceLab;

internal static class LabProgress
{
    private static readonly Stopwatch Total = new();
    private static int _completed;
    private static int _total;

    internal static void Initialize(int totalPhases)
    {
        _total = Math.Max(1, totalPhases);
        _completed = 0;
        Total.Restart();
        SetTitle("starting", 0);
    }

    internal static void Complete(string label)
    {
        int completed = Interlocked.Increment(ref _completed);
        SetTitle(label, (int)Math.Round(100.0 * completed / _total));
    }

    internal static void SetPhase(string label, int percent) => SetTitle(label, percent);

    internal static void Finish()
    {
        _completed = _total;
        SetTitle("complete", 100);
    }

    internal static IDisposable Activity(string label)
    {
        int percent = Math.Clamp((int)Math.Round(100.0 * _completed / Math.Max(1, _total)), 0, 100);
        SetTitle(label, percent);
        return new ActivityScope(label);
    }

    private static void SetTitle(string label, int percent)
    {
        try { Console.Title = $"Memory2 PerformanceLab — {Math.Clamp(percent, 0, 100)}% — {label} — {Total.Elapsed:hh\\:mm\\:ss}"; }
        catch { }
    }

    private sealed class ActivityScope : IDisposable
    {
        private readonly string _label;
        private readonly Timer _timer;
        private int _disposed;

        internal ActivityScope(string label)
        {
            _label = label;
            _timer = new Timer(_ => Pulse(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void Pulse()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            int percent = Math.Clamp((int)Math.Round(100.0 * _completed / Math.Max(1, _total)), 0, 100);
            SetTitle(_label, percent);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
        }
    }
}
