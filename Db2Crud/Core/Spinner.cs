#nullable enable
using System;
using System.Threading;

namespace Db2Crud.Core;

internal static class Steps
{
    public static T Run<T>(string title, Func<T> action, bool spinner = true)
    {
        using var s = Spinner.Start(title, spinner);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = action();
            sw.Stop();
            WriteGray($"{title} (completed in {sw.Elapsed.TotalSeconds:F1}s)");
            return result;
        }
        catch
        {
            sw.Stop();
            Console.WriteLine();
            WriteRed($"{title} (failed after {sw.Elapsed.TotalSeconds:F1}s)");
            throw;
        }
    }

    public static void Run(string title, Action action, bool spinner = true)
        => Run(title, () => { action(); return 0; }, spinner);

    private static void WriteGray(string msg)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(msg);
        Console.ForegroundColor = old;
    }

    private static void WriteRed(string msg)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg);
        Console.ForegroundColor = old;
    }
}

internal sealed class Spinner : IDisposable
{
    private static readonly char[] Glyphs = ['|', '/', '-', '\\'];
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    private Spinner(string message, bool enabled, int intervalMs)
    {
        if (!enabled)
        {
            Console.WriteLine(message);
            _thread = null!;
            return;
        }

        _thread = new Thread(() =>
        {
            int i = 0;
            Console.Write(message + " ");
            while (!_cts.IsCancellationRequested)
            {
                Console.Write(Glyphs[i++ % Glyphs.Length]);
                Thread.Sleep(intervalMs);
                Console.Write('\b');
            }
            Console.WriteLine("✓");
        })
        { IsBackground = true };
        _thread.Start();
    }

    public static Spinner Start(string message, bool enabled = true, int intervalMs = 80)
        => new(message, enabled, intervalMs);

    public void Dispose() => _cts.Cancel();
}
