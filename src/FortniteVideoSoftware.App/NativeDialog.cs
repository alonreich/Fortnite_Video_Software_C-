using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App;

public static partial class NativeDialog
{
    private const uint Ok = 0x00000000;
    private const uint IconInformation = 0x00000040;
    private const uint IconError = 0x00000010;
    private const uint IconQuestion = 0x00000020;
    private const uint YesNo = 0x00000004;
    private const uint Topmost = 0x00040000;
    private const uint SetForeground = 0x00010000;
    private const int IdYes = 6;
    private const int IdNo = 7;

    private static IntPtr GetOwnerHandle()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var handle = desktop.MainWindow.TryGetPlatformHandle();
                if (handle != null) return handle.Handle;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    public static void ShowInfo(string message)
    {
        Show(message, "Fortnite Video Software", Ok | IconInformation | Topmost | SetForeground);
    }

    public static void ShowError(string message)
    {
        Show(message, "Fortnite Video Software - Startup Failed", Ok | IconError | Topmost | SetForeground);
    }

    public static void ShowError(string message, string title)
    {
        Show(message, title, Ok | IconError | Topmost | SetForeground);
    }

    /// <summary>
    /// Shows a Yes/No question dialog. Returns true if the user clicks "Yes".
    /// Used for crash recovery prompts before the Avalonia UI is fully loaded.
    /// </summary>
    public static bool ShowQuestion(string message, string title = "Fortnite Video Software")
    {
        try
        {
            int result = MessageBoxW(GetOwnerHandle(), message, title, YesNo | IconQuestion | Topmost | SetForeground);
            return result == IdYes;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Show(string message, string title, uint flags)
    {
        try
        {
            MessageBoxW(GetOwnerHandle(), message, title, flags);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
