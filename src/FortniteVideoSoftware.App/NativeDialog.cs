using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App;

public static partial class NativeDialog
{
    private const uint Ok = 0x00000000;
    private const uint IconInformation = 0x00000040;
    private const uint IconError = 0x00000010;
    private const uint IconQuestion = 0x00000020;
    private const uint YesNo = 0x00000004;
    private const int IdYes = 6;
    private const int IdNo = 7;

    public static void ShowInfo(string message)
    {
        Show(message, "Fortnite Video Software", Ok | IconInformation);
    }

    public static void ShowError(string message)
    {
        Show(message, "Fortnite Video Software - Startup Failed", Ok | IconError);
    }

    /// <summary>
    /// Shows a Yes/No question dialog. Returns true if the user clicks "Yes".
    /// Used for crash recovery prompts before the Avalonia UI is fully loaded.
    /// </summary>
    public static bool ShowQuestion(string message, string title = "Fortnite Video Software")
    {
        try
        {
            int result = MessageBoxW(IntPtr.Zero, message, title, YesNo | IconQuestion);
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
            MessageBoxW(IntPtr.Zero, message, title, flags);
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
