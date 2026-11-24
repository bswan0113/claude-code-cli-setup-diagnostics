var marker = Environment.GetEnvironmentVariable("CLAUDE_LAUNCHER_QA_MARKER");
if (!string.IsNullOrEmpty(marker))
    File.AppendAllText(marker, $"{Environment.CurrentDirectory}{Environment.NewLine}");

if (args is ["--qa-hold", var stopFile])
{
    if (Environment.GetEnvironmentVariable("CLAUDE_QA_CONSOLE_READY") is { Length: > 0 } ready)
    {
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            hwnd = NativeMethods.GetConsoleWindow().ToInt64(), cwd = Environment.CurrentDirectory,
        });
        File.WriteAllText(ready + ".tmp", snapshot);
        File.Move(ready + ".tmp", ready);
    }

    var lifetime = System.Diagnostics.Stopwatch.StartNew();
    while (lifetime.Elapsed < TimeSpan.FromSeconds(75) && !File.Exists(stopFile))
        Thread.Sleep(100);
    return;
}

Console.WriteLine("99.98.97 (Claude Code)");

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();
}
