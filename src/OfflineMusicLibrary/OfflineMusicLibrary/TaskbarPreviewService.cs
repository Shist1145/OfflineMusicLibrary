using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace OfflineMusicLibrary;

internal sealed class TaskbarPreviewService : IDisposable
{
	private struct BitmapInfoHeader
	{
		public uint Size;

		public int Width;

		public int Height;

		public ushort Planes;

		public ushort BitCount;

		public uint Compression;

		public uint SizeImage;

		public int XPelsPerMeter;

		public int YPelsPerMeter;

		public uint ColorsUsed;

		public uint ColorsImportant;
	}

	private struct BitmapInfo
	{
		public BitmapInfoHeader Header;

		public uint Colors;
	}

	private const int WmDwmSendIconicThumbnail = 803;

	private const int WmDwmSendIconicLivePreviewBitmap = 806;

	private const int DwmwaForceIconicRepresentation = 7;

	private const int DwmwaHasIconicBitmap = 10;

	private const int EFail = -2147467259;

	private readonly Window _window;

	private readonly TaskbarItemInfo _itemInfo;

	private HwndSource? _source;

	private nint _handle;

	private TrackModel? _track;

	private BitmapSource? _cover;

	private bool _loggedCurrentRequest;

	private bool _disposed;

	public TaskbarPreviewService(Window window, TaskbarItemInfo itemInfo)
	{
		_window = window;
		_itemInfo = itemInfo;
		_itemInfo.Description = "本地音乐库 · 尚未播放";
		_window.SourceInitialized += Window_SourceInitialized;
		if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
		{
			Attach();
		}
	}

	public void UpdateTrack(TrackModel? track, BitmapSource? cover)
	{
		bool num = !string.Equals(_track?.Id, track?.Id, StringComparison.OrdinalIgnoreCase);
		_track = track;
		if (num || cover != null)
		{
			_cover = cover;
		}
		string title = (string.IsNullOrWhiteSpace(track?.Title) ? "尚未播放" : track.Title.Trim());
		string artist = (string.IsNullOrWhiteSpace(track?.Artist) ? "本地音乐库" : track.Artist.Trim());
		_window.Title = ((track == null) ? "本地音乐库" : (title + " - " + artist));
		_itemInfo.Description = ((track == null) ? "本地音乐库 · 尚未播放" : (title + " · " + artist));
		_loggedCurrentRequest = false;
		Invalidate();
	}

	private void Window_SourceInitialized(object? sender, EventArgs e)
	{
		Attach();
	}

	private void Attach()
	{
		if (_disposed || _source != null || !OperatingSystem.IsWindowsVersionAtLeast(6, 1) || DwmIsCompositionEnabled(out var compositionEnabled) != 0 || !compositionEnabled)
		{
			return;
		}
		_handle = new WindowInteropHelper(_window).Handle;
		if (_handle != IntPtr.Zero)
		{
			_source = HwndSource.FromHwnd(_handle);
			_source?.AddHook(WindowMessageHook);
			if (_window.TaskbarItemInfo != _itemInfo)
			{
				_window.TaskbarItemInfo = _itemInfo;
			}
			int enabled = 1;
			int forceResult = DwmSetWindowAttribute(_handle, 7, ref enabled, 4);
			int bitmapResult = DwmSetWindowAttribute(_handle, 10, ref enabled, 4);
			if (forceResult != 0 || bitmapResult != 0)
			{
				DiagnosticLog.Write("TASKBAR", $"Could not enable custom thumbnail: force=0x{forceResult:X8}, bitmap=0x{bitmapResult:X8}");
			}
			Invalidate();
		}
	}

	private void Invalidate()
	{
		if (_handle != IntPtr.Zero)
		{
			DwmInvalidateIconicBitmaps(_handle);
		}
	}

	private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
	{
		try
		{
			switch (message)
			{
			case 803:
			{
				(int Width, int Height) tuple = DecodeRequestedSize(lParam);
				int requestedWidth = tuple.Width;
				int requestedHeight = tuple.Height;
				(int Width, int Height) tuple2 = ConstrainToSquare(requestedWidth, requestedHeight);
				int thumbnailWidth = tuple2.Width;
				int thumbnailHeight = tuple2.Height;
				int result = SetThumbnail(hwnd, RenderThumbnail(thumbnailWidth, thumbnailHeight));
				handled = true;
				if (!_loggedCurrentRequest)
				{
					_loggedCurrentRequest = true;
					DiagnosticLog.Write("TASKBAR", $"Thumbnail request {requestedWidth}x{requestedHeight}, rendered {thumbnailWidth}x{thumbnailHeight}, result=0x{result:X8}, cover={_cover != null}");
				}
				break;
			}
			case 806:
				SetLivePreview(hwnd, RenderThumbnail(640, 360));
				handled = true;
				break;
			}
		}
		catch (Exception exception)
		{
			handled = true;
			DiagnosticLog.Write("TASKBAR", "Could not render the custom taskbar player", exception);
		}
		return IntPtr.Zero;
	}

	internal static (int Width, int Height) DecodeRequestedSize(nint lParam)
	{
		ulong packed = (ulong)((IntPtr)lParam).ToInt64();
		return (Width: Math.Max(1, (int)((packed >> 16) & 0xFFFF)), Height: Math.Max(1, (int)(packed & 0xFFFF)));
	}

	internal static (int Width, int Height) ConstrainToSquare(int maximumWidth, int maximumHeight)
	{
		int num = Math.Clamp(Math.Min(maximumWidth, maximumHeight), 1, 480);
		return (Width: num, Height: num);
	}

	private BitmapSource RenderThumbnail(int width, int height)
	{
		DrawingVisual visual = new DrawingVisual();
		RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
		using (DrawingContext drawing = visual.RenderOpen())
		{
			Rect bounds = new Rect(0.0, 0.0, width, height);
			if (_cover != null)
			{
				drawing.DrawRectangle(new ImageBrush(_cover)
				{
					Stretch = Stretch.UniformToFill,
					AlignmentX = AlignmentX.Center,
					AlignmentY = AlignmentY.Center
				}, null, bounds);
			}
			else
			{
				drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 27, 33)), null, bounds);
				double margin = Math.Max(4.0, (double)Math.Min(width, height) * 0.055);
				DrawFileFallback(drawing, new Rect(margin, margin, Math.Max(1.0, (double)width - margin * 2.0), Math.Max(1.0, (double)height - margin * 2.0)));
			}
		}
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(visual);
		renderTargetBitmap.Freeze();
		return renderTargetBitmap;
	}

	private void DrawFileFallback(DrawingContext drawing, Rect bounds)
	{
		double iconSide = Math.Min(bounds.Width * 0.34, bounds.Height * 0.52);
		Rect iconBounds = new Rect(bounds.Left + (bounds.Width - iconSide) / 2.0, bounds.Top + Math.Max(0.0, bounds.Height * 0.08), iconSide, iconSide);
		drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(45, 49, 59)), new Pen(new SolidColorBrush(Color.FromRgb(byte.MaxValue, 75, 107)), Math.Max(1.0, iconSide * 0.035)), iconBounds, Math.Max(2.0, iconSide * 0.08), Math.Max(2.0, iconSide * 0.08));
		FormattedText note = CreateText("♫", "Segoe UI Symbol", Math.Max(10.0, iconSide * 0.43), FontWeights.Normal, Color.FromRgb(byte.MaxValue, 75, 107), iconBounds.Width, iconBounds.Height, TextAlignment.Center);
		drawing.DrawText(note, new Point(iconBounds.Left, iconBounds.Top + Math.Max(0.0, (iconBounds.Height - note.Height) / 2.0)));
		double textTop = iconBounds.Bottom + Math.Max(3.0, bounds.Height * 0.055);
		Rect titleBounds = new Rect(bounds.Left, textTop, bounds.Width, Math.Max(1.0, bounds.Bottom - textTop));
		FormattedText title = CreateText(_track?.Title ?? "尚未播放", "Microsoft YaHei UI", Math.Clamp(titleBounds.Height * 0.38, 10.0, 17.0), FontWeights.SemiBold, Color.FromRgb(245, 247, 250), titleBounds.Width, titleBounds.Height, TextAlignment.Center);
		drawing.DrawText(title, new Point(titleBounds.Left, titleBounds.Top));
	}

	private static FormattedText CreateText(string value, string font, double size, FontWeight weight, Color color, double maxWidth, double maxHeight, TextAlignment alignment)
	{
		return new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal), size, new SolidColorBrush(color), 1.0)
		{
			MaxTextWidth = Math.Max(1.0, maxWidth),
			MaxTextHeight = Math.Max(1.0, maxHeight),
			Trimming = TextTrimming.CharacterEllipsis,
			TextAlignment = alignment
		};
	}

	private static int SetThumbnail(nint hwnd, BitmapSource bitmap)
	{
		nint hBitmap = CreateHBitmap(bitmap);
		if (hBitmap == IntPtr.Zero)
		{
			return -2147467259;
		}
		try
		{
			return DwmSetIconicThumbnail(hwnd, hBitmap, 0u);
		}
		finally
		{
			DeleteObject(hBitmap);
		}
	}

	private static int SetLivePreview(nint hwnd, BitmapSource bitmap)
	{
		nint hBitmap = CreateHBitmap(bitmap);
		if (hBitmap == IntPtr.Zero)
		{
			return -2147467259;
		}
		try
		{
			return DwmSetIconicLivePreviewBitmap(hwnd, hBitmap, IntPtr.Zero, 0u);
		}
		finally
		{
			DeleteObject(hBitmap);
		}
	}

	private static nint CreateHBitmap(BitmapSource bitmap)
	{
		byte[] pixels;
		checked
		{
			int stride = bitmap.PixelWidth * 4;
			pixels = GC.AllocateUninitializedArray<byte>(stride * bitmap.PixelHeight);
			bitmap.CopyPixels(pixels, stride, 0);
		}
		BitmapInfo info = new BitmapInfo
		{
			Header = new BitmapInfoHeader
			{
				Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
				Width = bitmap.PixelWidth,
				Height = -bitmap.PixelHeight,
				Planes = 1,
				BitCount = 32,
				SizeImage = (uint)pixels.Length
			}
		};
		nint dc = GetDC(IntPtr.Zero);
		try
		{
			nint bits;
			nint result = CreateDIBSection(dc, ref info, 0u, out bits, IntPtr.Zero, 0u);
			if (result == IntPtr.Zero || bits == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			Marshal.Copy(pixels, 0, bits, pixels.Length);
			return result;
		}
		finally
		{
			if (dc != IntPtr.Zero)
			{
				ReleaseDC(IntPtr.Zero, dc);
			}
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_window.SourceInitialized -= Window_SourceInitialized;
			_source?.RemoveHook(WindowMessageHook);
			_source = null;
			if (_handle != IntPtr.Zero)
			{
				int disabled = 0;
				DwmSetWindowAttribute(_handle, 7, ref disabled, 4);
				DwmSetWindowAttribute(_handle, 10, ref disabled, 4);
				DwmInvalidateIconicBitmaps(_handle);
			}
			_handle = IntPtr.Zero;
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetIconicThumbnail(nint hwnd, nint bitmap, uint flags);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetIconicLivePreviewBitmap(nint hwnd, nint bitmap, nint clientPoint, uint flags);

	[DllImport("dwmapi.dll")]
	private static extern int DwmInvalidateIconicBitmaps(nint hwnd);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern nint CreateDIBSection(nint hdc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(nint handle);

	[DllImport("user32.dll")]
	private static extern nint GetDC(nint hwnd);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(nint hwnd, nint hdc);
}
