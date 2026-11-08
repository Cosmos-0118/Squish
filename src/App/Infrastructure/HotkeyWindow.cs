using System;
using System.Windows.Forms;

namespace Squish.App.Infrastructure;

/// <summary>
/// A message-only window that exists purely to receive <c>WM_HOTKEY</c>. Registering global hotkeys against a real WPF
/// window would force WPF to build its render target (and load the Direct3D driver) just to get an HWND, so the tray
/// app keeps this tiny native window instead and creates real windows only when something needs to be shown.
/// Must be created and disposed on a thread that pumps messages (the UI thread).
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Squish.Hotkeys",
            Parent = HwndMessage
        });
    }

    /// <summary>Raised on the creating thread with the id passed to <c>RegisterHotKey</c>.</summary>
    public event Action<int>? HotkeyPressed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
