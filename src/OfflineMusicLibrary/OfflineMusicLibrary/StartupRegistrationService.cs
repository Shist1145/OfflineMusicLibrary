using System;
using Microsoft.Win32;

namespace OfflineMusicLibrary;

public static class StartupRegistrationService
{
	private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string ValueName = "OfflineMusicLibrary";

	public static void Apply(bool enabled)
	{
		using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true) ?? Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
		if (enabled)
		{
			string executable = Environment.ProcessPath;
			if (!string.IsNullOrWhiteSpace(executable))
			{
				key.SetValue("OfflineMusicLibrary", "\"" + executable + "\"");
			}
		}
		else
		{
			key.DeleteValue("OfflineMusicLibrary", throwOnMissingValue: false);
		}
	}
}
