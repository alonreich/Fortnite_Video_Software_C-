using System;

namespace FortniteVideoSoftware.Core.Infrastructure;

public static class CoreLogger
{
    public static Action<string, string>? InfoAction;
    public static Action<string, string>? FailAction;
    public static Action<string, string>? DebugAction;

    public static Action<string>? AppendAction;

    public static void Info(string step, string detail)
    {
        InfoAction?.Invoke(step, detail);
    }

    public static void Debug(string step, string detail)
    {
        DebugAction?.Invoke(step, detail);
    }

    public static void Fail(string step, string detail)
    {
        FailAction?.Invoke(step, detail);
    }

    public static void Append(string line)
    {
        AppendAction?.Invoke(line);
    }

    public static void Swallowed(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";
            Info("SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}");
            Debug("SWALLOWED", $"{where}{Environment.NewLine}{ex}");
        }
        catch (Exception)
        {
        }
    }
}
