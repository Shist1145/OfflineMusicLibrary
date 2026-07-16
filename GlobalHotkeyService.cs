using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OfflineMusicLibrary;

public enum HotkeyAction
{
    PlayPause,
    Previous,
    Next,
    VolumeUp,
    VolumeDown,
    ToggleLyrics,
    ToggleFavorite,
    ToggleMiniMode
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;
    private const uint VkP = 0x50;
    private const uint VkD = 0x44;
    private const uint VkL = 0x4C;
    private const uint VkM = 0x4D;
    private const uint VkMediaNextTrack = 0xB0;
    private const uint VkMediaPreviousTrack = 0xB1;
    private const uint VkMediaPlayPause = 0xB3;
    private const int FirstId = 0x5A10;

    private readonly Dictionary<int, HotkeyAction> _actions = [];
    private HwndSource? _source;
    private nint _handle;

    public event Action<HotkeyAction>? Invoked;
    public int FailedRegistrations { get; private set; }

    public void Configure(Window window, bool globalHotkeys, bool systemMediaKeys)
    {
        EnsureHook(window);
        UnregisterAll();
        FailedRegistrations = 0;

        if (globalHotkeys)
        {
            Register(0, ModControl | ModAlt | ModNoRepeat, VkP, HotkeyAction.PlayPause);
            Register(1, ModControl | ModAlt | ModNoRepeat, VkLeft, HotkeyAction.Previous);
            Register(2, ModControl | ModAlt | ModNoRepeat, VkRight, HotkeyAction.Next);
            Register(3, ModControl | ModAlt | ModNoRepeat, VkUp, HotkeyAction.VolumeUp);
            Register(4, ModControl | ModAlt | ModNoRepeat, VkDown, HotkeyAction.VolumeDown);
            Register(5, ModControl | ModAlt | ModNoRepeat, VkD, HotkeyAction.ToggleLyrics);
            Register(6, ModControl | ModAlt | ModNoRepeat, VkL, HotkeyAction.ToggleFavorite);
            Register(7, ModControl | ModAlt | ModNoRepeat, VkM, HotkeyAction.ToggleMiniMode);
        }

        if (systemMediaKeys)
        {
            Register(8, ModNoRepeat, VkMediaPlayPause, HotkeyAction.PlayPause);
            Register(9, ModNoRepeat, VkMediaPreviousTrack, HotkeyAction.Previous);
            Register(10, ModNoRepeat, VkMediaNextTrack, HotkeyAction.Next);
        }
    }

    private void EnsureHook(Window window)
    {
        if (_source is not null)
            return;
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessageHook);
    }

    private void Register(int offset, uint modifiers, uint key, HotkeyAction action)
    {
        var id = FirstId + offset;
        if (RegisterHotKey(_handle, id, modifiers, key))
            _actions[id] = action;
        else
            FailedRegistrations++;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Invoked?.Invoke(action);
        }
        return 0;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_handle, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
