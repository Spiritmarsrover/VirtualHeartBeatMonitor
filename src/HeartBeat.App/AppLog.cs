namespace HeartBeat.App;

/// <summary>
/// Plain-text log next to the exe (app.log; the previous run is kept as app.previous.log), so
/// problems seen in VR leave a trace.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static string? _last;

    public static string Path => _path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "app.log");

    public static void Start()
    {
        _path = System.IO.Path.Combine(AppContext.BaseDirectory, "app.log");
        try
        {
            if (File.Exists(_path)) File.Copy(_path, System.IO.Path.Combine(AppContext.BaseDirectory, "app.previous.log"), overwrite: true);
            File.WriteAllText(_path, "");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Write($"Started {Environment.ProcessPath} {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (message == _last) return; // repeated errors are written once
            _last = message;
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
