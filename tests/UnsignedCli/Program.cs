var marker = Environment.GetEnvironmentVariable("CLAUDE_LAUNCHER_QA_MARKER");
if (!string.IsNullOrEmpty(marker))
    File.AppendAllText(marker, $"{Environment.CurrentDirectory}{Environment.NewLine}");

Console.WriteLine("99.98.97 (Claude Code)");
