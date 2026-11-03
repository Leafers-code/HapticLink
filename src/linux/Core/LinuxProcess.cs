using System.Diagnostics;

namespace HapticLink.Core;

internal static class LinuxProcess
{
    public static Process Start(string command, params string[] args)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start " + command);
    }

    public static string Run(string command, params string[] args)
    {
        using var process = Start(command, args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(4000)) { process.Kill(true); throw new TimeoutException(command + " timed out"); }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Result.Trim());
        return stdout.Result;
    }
}
