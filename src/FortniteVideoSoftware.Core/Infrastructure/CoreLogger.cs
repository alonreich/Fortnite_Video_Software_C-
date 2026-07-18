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
}
