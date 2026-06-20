using System.Runtime.InteropServices;

namespace FortniteVideoSoftware.App;

public static partial class NativeDialog
{
    private const uint Ok = 0x00000000;
    private const uint IconInformation = 0x00000040;
    private const uint IconError = 0x00000010;

    public static void ShowInfo(string message)
    {
        Show(message, "Fortnite Video Software", Ok | IconInformation);
    }

    public static void ShowError(string message)
    {
        Show(message, "Fortnite Video Software - Startup Failed", Ok | IconError);
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
