using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OfflineMusicLibrary;

public static class LibraryRootKinds
{
	public const string Unknown = "Unknown";
	public const string Local = "Local";
	public const string Unc = "Unc";
	public const string MappedNetwork = "MappedNetwork";
	public const string Removable = "Removable";
	public const string Optical = "Optical";

	public static string Normalize(string? value)
	{
		return value switch
		{
			Local => Local,
			Unc => Unc,
			MappedNetwork => MappedNetwork,
			Removable => Removable,
			Optical => Optical,
			_ => Unknown
		};
	}

	public static bool IsReconnectable(string? value)
	{
		return Normalize(value) is Unc or MappedNetwork or Removable or Optical;
	}

	public static bool IsNetwork(string? value)
	{
		return Normalize(value) is Unc or MappedNetwork;
	}
}

public static class LibraryRootHealthStates
{
	public const string Unknown = "Unknown";
	public const string Checking = "Checking";
	public const string Online = "Online";
	public const string Slow = "Slow";
	public const string Offline = "Offline";
	public const string NeedsCredentials = "NeedsCredentials";

	public static string Normalize(string? value)
	{
		return value switch
		{
			Checking => Checking,
			Online => Online,
			Slow => Slow,
			Offline => Offline,
			NeedsCredentials => NeedsCredentials,
			_ => Unknown
		};
	}

	public static bool IsReachable(string? value)
	{
		return Normalize(value) is Online or Slow;
	}
}

public sealed class LibraryRootState : INotifyPropertyChanged
{
	private string _path = "";
	private string _rootKind = LibraryRootKinds.Unknown;
	private string _health = LibraryRootHealthStates.Unknown;
	private long? _lastLatencyMs;
	private string _lastError = "";

	public string RootId { get; set; } = "";

	public string Path
	{
		get => _path;
		set => SetField(ref _path, value ?? "");
	}

	public string RootKind
	{
		get => _rootKind;
		set
		{
			if (SetField(ref _rootKind, LibraryRootKinds.Normalize(value)))
			{
				OnPropertyChanged(nameof(KindText));
			}
		}
	}

	public string Health
	{
		get => _health;
		set
		{
			if (SetField(ref _health, LibraryRootHealthStates.Normalize(value)))
			{
				OnPropertyChanged(nameof(HealthText));
				OnPropertyChanged(nameof(StatusText));
			}
		}
	}

	public DateTime? LastSeenUtc { get; set; }

	public DateTime? LastSuccessfulScanUtc { get; set; }

	public long? LastLatencyMs
	{
		get => _lastLatencyMs;
		set
		{
			if (SetField(ref _lastLatencyMs, value))
			{
				OnPropertyChanged(nameof(StatusText));
			}
		}
	}

	public string LastError
	{
		get => _lastError;
		set
		{
			if (SetField(ref _lastError, value ?? ""))
			{
				OnPropertyChanged(nameof(StatusText));
			}
		}
	}

	public int ProbeTimeoutSeconds { get; set; } = 3;

	public string RetryPolicy { get; set; } = "1,2,5,10,30";

	public string CachePolicy { get; set; } = "MetadataAndAssets";

	[JsonIgnore]
	public string KindText => RootKind switch
	{
		LibraryRootKinds.Unc => "UNC / SMB",
		LibraryRootKinds.MappedNetwork => "映射网络盘",
		LibraryRootKinds.Removable => "移动存储",
		LibraryRootKinds.Optical => "光盘",
		LibraryRootKinds.Local => "本机磁盘",
		_ => "待识别"
	};

	[JsonIgnore]
	public string HealthText => Health switch
	{
		LibraryRootHealthStates.Checking => "正在检测",
		LibraryRootHealthStates.Online => "在线",
		LibraryRootHealthStates.Slow => "在线但延迟较高",
		LibraryRootHealthStates.Offline => "离线",
		LibraryRootHealthStates.NeedsCredentials => "需要 Windows 凭据",
		_ => "尚未检测"
	};

	[JsonIgnore]
	public string StatusText
	{
		get
		{
			string latency = LastLatencyMs.HasValue ? $" · {LastLatencyMs.Value:N0} ms" : "";
			string lastSeen = !LibraryRootHealthStates.IsReachable(Health) && LastSeenUtc.HasValue
				? $" · 上次在线 {LastSeenUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
				: "";
			string error = string.IsNullOrWhiteSpace(LastError) ? "" : $" · {LastError}";
			return $"{KindText} · {HealthText}{latency}{lastSeen}{error}";
		}
	}

	public void ApplyProbeResult(LibraryRootProbeResult result)
	{
		RootKind = result.RootKind;
		Health = result.Health;
		LastLatencyMs = result.LatencyMs;
		LastError = result.Error ?? "";
		if (result.Reachable)
		{
			LastSeenUtc = result.CompletedUtc;
		}
		OnPropertyChanged(nameof(StatusText));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

public static class LibraryRootCatalog
{
	internal const int MaximumPathCharacters = 32767;

	public static List<LibraryRootState> Synchronize(
		IEnumerable<string>? paths,
		IEnumerable<LibraryRootState>? existingRoots)
	{
		Dictionary<string, LibraryRootState> existingByPath = (existingRoots ?? [])
			.Where(root => root != null && !string.IsNullOrWhiteSpace(root.Path))
			.GroupBy(root => NormalizePath(root.Path), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
		List<LibraryRootState> result = new();
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string path in paths ?? [])
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				continue;
			}
			string normalized = NormalizePath(path);
			if (string.IsNullOrWhiteSpace(normalized))
			{
				continue;
			}
			if (!seen.Add(normalized))
			{
				continue;
			}
			if (!existingByPath.TryGetValue(normalized, out LibraryRootState? root))
			{
				root = Create(normalized);
			}
			else
			{
				root.Path = normalized;
				Normalize(root);
			}
			result.Add(root);
		}
		return result;
	}

	public static LibraryRootState Create(string path)
	{
		string normalized = NormalizePath(path);
		return new LibraryRootState
		{
			RootId = CreateRootId(normalized),
			Path = normalized,
			RootKind = FastClassify(normalized),
			Health = LibraryRootHealthStates.Unknown
		};
	}

	public static void Normalize(LibraryRootState root)
	{
		ArgumentNullException.ThrowIfNull(root);
		root.Path = NormalizePath(root.Path);
		root.RootId = CreateRootId(root.Path);
		root.RootKind = LibraryRootKinds.Normalize(root.RootKind);
		if (root.RootKind == LibraryRootKinds.Unknown)
		{
			root.RootKind = FastClassify(root.Path);
		}
		root.Health = LibraryRootHealthStates.Normalize(root.Health);
		root.ProbeTimeoutSeconds = Math.Clamp(root.ProbeTimeoutSeconds, 1, 15);
		root.RetryPolicy = string.IsNullOrWhiteSpace(root.RetryPolicy) ? "1,2,5,10,30" : root.RetryPolicy;
		root.CachePolicy = string.IsNullOrWhiteSpace(root.CachePolicy) ? "MetadataAndAssets" : root.CachePolicy;
		root.LastError ??= "";
	}

	public static string CreateRootId(string path)
	{
		string normalized = NormalizePath(path).ToUpperInvariant();
		byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
		return "root-" + Convert.ToHexString(digest.AsSpan(0, 10)).ToLowerInvariant();
	}

	public static string NormalizePath(string path)
	{
		string trimmed = (path ?? "").Trim();
		if (trimmed.Length == 0 || trimmed.Length > MaximumPathCharacters)
		{
			return "";
		}
		try
		{
			string full = System.IO.Path.GetFullPath(trimmed);
			string root = System.IO.Path.GetPathRoot(full) ?? "";
			return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
				? full
				: System.IO.Path.TrimEndingDirectorySeparator(full);
		}
		catch
		{
			return System.IO.Path.TrimEndingDirectorySeparator(trimmed);
		}
	}

	public static string FastClassify(string path)
	{
		if (IsUncPath(path))
		{
			return LibraryRootKinds.Unc;
		}
		try
		{
			string? driveRoot = System.IO.Path.GetPathRoot(path);
			if (string.IsNullOrWhiteSpace(driveRoot))
			{
				return LibraryRootKinds.Unknown;
			}
			return new DriveInfo(driveRoot).DriveType switch
			{
				DriveType.Network => LibraryRootKinds.MappedNetwork,
				DriveType.Removable => LibraryRootKinds.Removable,
				DriveType.CDRom => LibraryRootKinds.Optical,
				DriveType.Fixed or DriveType.Ram => LibraryRootKinds.Local,
				_ => LibraryRootKinds.Unknown
			};
		}
		catch
		{
			return LibraryRootKinds.Unknown;
		}
	}

	public static bool IsUncPath(string? path)
	{
		return !string.IsNullOrWhiteSpace(path) &&
			(path.StartsWith(@"\\", StringComparison.Ordinal) ||
			 path.StartsWith("//", StringComparison.Ordinal));
	}

	public static bool IsPathWithinRoot(string? path, string? rootPath)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
		{
			return false;
		}
		try
		{
			string normalizedPath = NormalizePath(path);
			string normalizedRoot = NormalizePath(rootPath);
			if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string prefix = normalizedRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
				? normalizedRoot
				: normalizedRoot + System.IO.Path.DirectorySeparatorChar;
			return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	public static LibraryRootState? FindOwningRoot(IEnumerable<LibraryRootState>? roots, string? path)
	{
		return (roots ?? [])
			.Where(root => IsPathWithinRoot(path, root.Path))
			.OrderByDescending(root => root.Path.Length)
			.FirstOrDefault();
	}
}
