using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OfflineMusicLibrary;

public sealed class GlobalHotkeyService : IDisposable
{
	private const int WmHotkey = 786;

	private const uint ModAlt = 1u;

	private const uint ModControl = 2u;

	private const uint ModNoRepeat = 16384u;

	private const uint VkLeft = 37u;

	private const uint VkUp = 38u;

	private const uint VkRight = 39u;

	private const uint VkDown = 40u;

	private const uint VkP = 80u;

	private const uint VkD = 68u;

	private const uint VkL = 76u;

	private const uint VkM = 77u;

	private const uint VkMediaNextTrack = 176u;

	private const uint VkMediaPreviousTrack = 177u;

	private const uint VkMediaPlayPause = 179u;

	private const int FirstId = 23056;

	private readonly Dictionary<int, HotkeyAction> _actions = new Dictionary<int, HotkeyAction>();

	private HwndSource? _source;

	private nint _handle;

	public int FailedRegistrations { get; private set; }

	public event Action<HotkeyAction>? Invoked;

	public void Configure(Window window, bool globalHotkeys, bool systemMediaKeys)
	{
		EnsureHook(window);
		UnregisterAll();
		FailedRegistrations = 0;
		if (globalHotkeys)
		{
			Register(0, 16387u, 80u, HotkeyAction.PlayPause);
			Register(1, 16387u, 37u, HotkeyAction.Previous);
			Register(2, 16387u, 39u, HotkeyAction.Next);
			Register(3, 16387u, 38u, HotkeyAction.VolumeUp);
			Register(4, 16387u, 40u, HotkeyAction.VolumeDown);
			Register(5, 16387u, 68u, HotkeyAction.ToggleLyrics);
			Register(6, 16387u, 76u, HotkeyAction.ToggleFavorite);
			Register(7, 16387u, 77u, HotkeyAction.ToggleMiniMode);
		}
		if (systemMediaKeys)
		{
			Register(8, 16384u, 179u, HotkeyAction.PlayPause);
			Register(9, 16384u, 177u, HotkeyAction.Previous);
			Register(10, 16384u, 176u, HotkeyAction.Next);
		}
	}

	private void EnsureHook(Window window)
	{
		if (_source == null)
		{
			_handle = new WindowInteropHelper(window).EnsureHandle();
			_source = HwndSource.FromHwnd(_handle);
			_source?.AddHook(WindowMessageHook);
		}
	}

	private void Register(int offset, uint modifiers, uint key, HotkeyAction action)
	{
		int id = 23056 + offset;
		if (RegisterHotKey(_handle, id, modifiers, key))
		{
			_actions[id] = action;
		}
		else
		{
			FailedRegistrations++;
		}
	}

	private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
	{
		if (message == 786 && _actions.TryGetValue(((IntPtr)wParam).ToInt32(), out var action))
		{
			handled = true;
			this.Invoked?.Invoke(action);
		}
		return 0;
	}

	private void UnregisterAll()
	{
		foreach (int id in _actions.Keys)
		{
			UnregisterHotKey(_handle, id);
		}
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
