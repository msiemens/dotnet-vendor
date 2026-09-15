namespace DotnetVendor;

public sealed class ProgressBar : IDisposable
{
    readonly int _total;
    int _current;
    bool _drawn;

    public ProgressBar(int total)
    {
        _total = total;
    }

    static int SafeWindowWidth
    {
        get
        {
            try { return Console.WindowWidth; }
            catch { return 80; }
        }
    }

    static bool IsInteractive => !Console.IsOutputRedirected;

    /// <summary>Erase the progress bar line so normal output can be written.</summary>
    public void Clear()
    {
        if (!_drawn || !IsInteractive) return;
        Console.Write($"\r{new string(' ', SafeWindowWidth - 1)}\r");
        _drawn = false;
    }

    /// <summary>Advance by one and redraw.</summary>
    public void Tick()
    {
        _current++;
        if (!IsInteractive) return;
        Draw();
    }

    void Draw()
    {
        var width = Math.Max(SafeWindowWidth - 1, 40);
        var suffix = $" {_current}/{_total}";
        var barWidth = width - 4 - suffix.Length; // 4 = "  [" + "]"
        if (barWidth < 10) barWidth = 10;

        var fraction = _total > 0 ? (double)_current / _total : 0;
        var filled = (int)(barWidth * fraction);
        var empty = barWidth - filled;

        Console.Write($"\r  [{new string('\u2588', filled)}{new string('\u2591', empty)}]{suffix}");
        _drawn = true;
    }

    public void Dispose() => Clear();
}
