using System.Runtime.InteropServices;

namespace NjuPrepaidStatus.UI;

internal sealed class NativeTrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int CallbackMessage = WM_USER + 500;
    private readonly Guid _guid;
    private readonly IntPtr _windowHandle;
    private bool _added;

    public NativeTrayIcon(Guid guid)
    {
        _guid = guid;
        _windowHandle = MessageWindow.Instance.Handle;
    }

    public void Show(Icon icon, string tooltip)
    {
        var data = CreateBaseData();
        data.uFlags = NotifyIconFlags.Icon | NotifyIconFlags.Tip | NotifyIconFlags.Message | NotifyIconFlags.Guid | NotifyIconFlags.ShowTip;
        data.hIcon = icon.Handle;
        data.uCallbackMessage = CallbackMessage;
        data.szTip = TrimTooltip(tooltip);

        if (!_added)
        {
            // Clear any stale shell entry for this GUID before adding.
            var deleteData = CreateBaseData();
            deleteData.uFlags = NotifyIconFlags.Guid;
            Shell_NotifyIcon(NotifyCommand.Delete, ref deleteData);

            if (!Shell_NotifyIcon(NotifyCommand.Add, ref data))
            {
                throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed.");
            }

            data.uVersion = NotifyVersion.V4;
            Shell_NotifyIcon(NotifyCommand.SetVersion, ref data);
            _added = true;
            return;
        }

        if (!Shell_NotifyIcon(NotifyCommand.Modify, ref data))
        {
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_MODIFY) failed.");
        }
    }

    public void Dispose()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateBaseData();
        data.uFlags = NotifyIconFlags.Guid;
        Shell_NotifyIcon(NotifyCommand.Delete, ref data);
        _added = false;
    }

    private NOTIFYICONDATA CreateBaseData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = 0,
            guidItem = _guid
        };
    }

    private static string TrimTooltip(string tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return string.Empty;
        }

        const int maxLength = 127;
        var trimmed = tooltip.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(NotifyCommand dwMessage, ref NOTIFYICONDATA lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public NotifyIconFlags uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;

        public uint uVersion
        {
            readonly get => uTimeoutOrVersion;
            set => uTimeoutOrVersion = value;
        }
    }

    [Flags]
    private enum NotifyIconFlags : uint
    {
        Message = 0x00000001,
        Icon = 0x00000002,
        Tip = 0x00000004,
        Guid = 0x00000020,
        ShowTip = 0x00000080
    }

    private enum NotifyCommand : uint
    {
        Add = 0x00000000,
        Modify = 0x00000001,
        Delete = 0x00000002,
        SetVersion = 0x00000004
    }

    private static class NotifyVersion
    {
        public const uint V4 = 4;
    }

    private sealed class MessageWindow : NativeWindow
    {
        private static readonly Lazy<MessageWindow> LazyInstance = new(() => new MessageWindow());
        public static MessageWindow Instance => LazyInstance.Value;

        private MessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "NjuPrepaidStatusTrayHost",
                ClassName = "Static"
            });
        }
    }
}
