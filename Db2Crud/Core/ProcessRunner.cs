#nullable enable
using System;
using System.Diagnostics;

namespace Db2Crud.Core;

internal sealed class ProcessRunner
{
    public void Run(string exe, string arguments, string workingDir, bool verbose = false)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory   = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        if (verbose) WriteGray($"> {exe} {arguments}");

        var sw = Stopwatch.StartNew();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };

        if (!proc.Start()) throw new Exception($"Failed to start: {exe} {arguments}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        sw.Stop();

        if (proc.ExitCode != 0)
            throw new Exception($"Command failed ({proc.ExitCode}): {exe} {arguments}");

        if (verbose) WriteGray($"(done in {sw.Elapsed.TotalSeconds:F1}s)");
    }

    private static void WriteGray(string msg)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(msg);
        Console.ForegroundColor = old;
    }
}
