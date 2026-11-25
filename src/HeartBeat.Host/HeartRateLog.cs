using System.Globalization;
using System.Text;

namespace HeartBeat.App;

/// <summary>
/// The heart rate log: one CSV row per engine update (about once a second) while SteamVR runs, one
/// file per day in <see cref="Folder"/>. Columns: local time, BPM (empty while it showed "--"),
/// confidence, state. <see cref="Export"/> joins all days into one CSV on the Desktop.
/// </summary>
public sealed class HeartRateLog : IDisposable
{
    public const string Header = "time,bpm,confidence,state";
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "heart-rate-log");

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public void Write(DateTimeOffset time, HudView view, double confidence)
    {
        string bpm = view.State is HudState.Confident or HudState.Unsure ? view.Text : "";
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{time:yyyy-MM-ddTHH:mm:sszzz},{bpm},{confidence:0.00},{StateName(view.State)}");
        lock (_gate)
        {
            var day = DateOnly.FromDateTime(time.LocalDateTime);
            if (_writer == null || day != _day)
            {
                _writer?.Dispose();
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, $"heart-rate-{day:yyyy-MM-dd}.csv");
                bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false));
                _day = day;
                if (isNew) _writer.WriteLine(Header);
            }
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <summary>Closes the current file (the log was turned off or the session ended).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static string StateName(HudState state) => state switch
    {
        HudState.Confident => "ok",
        HudState.Unsure => "unsure",
        HudState.Measuring => "measuring",
        HudState.Moving => "moving",
        _ => "no data",
    };

    /// <summary>Joins all logged days into one CSV in <paramref name="folder"/>; returns its path.</summary>
    public static string Export(string folder)
    {
        var days = Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "heart-rate-*.csv").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
        if (days.Count == 0) throw new InvalidOperationException("The heart rate log is empty.");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"VirtualHeartBeatMonitor-heart-rate-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var output = new StreamWriter(path, false, new UTF8Encoding(false));
        output.WriteLine(Header);
        int rows = 0;
        foreach (var day in days)
        {
            // The current day is still open for writing.
            using var input = new StreamReader(new FileStream(day, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (input.ReadLine() is { } line)
            {
                if (line.Length == 0 || line == Header) continue;
                output.WriteLine(line);
                rows++;
            }
        }
        AppLog.Write($"Heart rate log exported: {rows} rows from {days.Count} day(s) to {path}");
        return path;
    }

    public void Dispose() => Close();
}
