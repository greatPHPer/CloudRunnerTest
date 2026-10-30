using System.Text.RegularExpressions;

namespace CloudRunner.HostAgent;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--pipe", StringComparison.Ordinal))
        {
            return 2;
        }

        var pipeName = args[1];
        if (!Regex.IsMatch(pipeName, @"^CloudRunner_[A-Fa-f0-9]{32}$", RegexOptions.CultureInvariant))
        {
            return 2;
        }

        try
        {
            await using var server = new AgentServer();
            await server.RunAsync(pipeName, CancellationToken.None);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudRunner", "Logs");
                Directory.CreateDirectory(logDirectory);
                await File.AppendAllTextAsync(
                    Path.Combine(logDirectory, "host-agent.log"),
                    $"[{DateTimeOffset.UtcNow:O}] {ex}{Environment.NewLine}");
            }
            catch
            {
                // The agent must not crash again while trying to write its diagnostic log.
            }

            return 1;
        }
    }
}
