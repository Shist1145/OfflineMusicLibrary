using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

public readonly record struct LibraryRootProbeResult(
	string RootId,
	string RootKind,
	string Health,
	bool Reachable,
	long? LatencyMs,
	string? Error,
	DateTime CompletedUtc);

public readonly record struct PathAvailabilityResult(
	bool Reachable,
	bool TimedOut,
	long? LatencyMs,
	string? Error);

public static class LibraryRootRetrySchedule
{
	private static readonly int[] RetrySeconds = [1, 2, 5, 10];

	public static TimeSpan GetDelay(int completedAttempts)
	{
		int index = Math.Max(0, completedAttempts);
		return TimeSpan.FromSeconds(index < RetrySeconds.Length ? RetrySeconds[index] : 30);
	}
}

public sealed class LibraryRootHealthService
{
	private const int DefaultMaximumConcurrentProbes = 4;
	private const int DefaultMaximumOutstandingProbes = 32;
	private readonly ConcurrentDictionary<string, Lazy<Task<ProbeOutcome>>> _rootProbes = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Lazy<Task<PathAvailabilityResult>>> _pathProbes = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _executionGate;
	private readonly SemaphoreSlim _outstandingSlots;
	private readonly TimeSpan _slowThreshold;
	private readonly Func<string, Task<ProbeOutcome>> _probeRoot;
	private readonly Func<string, Task<PathAvailabilityResult>> _probePath;

	public LibraryRootHealthService(TimeSpan? slowThreshold = null)
		: this(
			DefaultProbeRootAsync,
			DefaultProbePathAsync,
			slowThreshold,
			DefaultMaximumConcurrentProbes,
			DefaultMaximumOutstandingProbes)
	{
	}

	internal LibraryRootHealthService(
		Func<string, Task<ProbeOutcome>> probeRoot,
		Func<string, Task<PathAvailabilityResult>> probePath,
		TimeSpan? slowThreshold = null,
		int maxConcurrentProbes = DefaultMaximumConcurrentProbes,
		int maxOutstandingProbes = DefaultMaximumOutstandingProbes)
	{
		_probeRoot = probeRoot ?? throw new ArgumentNullException(nameof(probeRoot));
		_probePath = probePath ?? throw new ArgumentNullException(nameof(probePath));
		_slowThreshold = slowThreshold ?? TimeSpan.FromMilliseconds(900);
		int concurrency = Math.Clamp(maxConcurrentProbes, 1, 16);
		int outstanding = Math.Clamp(maxOutstandingProbes, concurrency, 256);
		_executionGate = new SemaphoreSlim(concurrency, concurrency);
		_outstandingSlots = new SemaphoreSlim(outstanding, outstanding);
	}

	public async Task<LibraryRootProbeResult> ProbeAsync(
		LibraryRootState root,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(root);
		string rootId = LibraryRootCatalog.CreateRootId(root.Path);
		TimeSpan effectiveTimeout = NormalizeTimeout(timeout ?? TimeSpan.FromSeconds(root.ProbeTimeoutSeconds));
		Task<ProbeOutcome>? task = GetOrCreateProbe(_rootProbes, rootId, () => _probeRoot(root.Path));
		if (task == null)
		{
			return new LibraryRootProbeResult(
				rootId,
				LibraryRootKinds.Normalize(root.RootKind),
				LibraryRootHealthStates.Offline,
				false,
				0,
				"检测队列繁忙，请稍后重试",
				DateTime.UtcNow);
		}
		try
		{
			ProbeOutcome outcome = await task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
			string health = !outcome.Reachable
				? outcome.NeedsCredentials ? LibraryRootHealthStates.NeedsCredentials : LibraryRootHealthStates.Offline
				: outcome.Latency >= _slowThreshold ? LibraryRootHealthStates.Slow : LibraryRootHealthStates.Online;
			return new LibraryRootProbeResult(
				rootId,
				outcome.RootKind,
				health,
				outcome.Reachable,
				(long)Math.Max(0, outcome.Latency.TotalMilliseconds),
				outcome.Error,
				DateTime.UtcNow);
		}
		catch (TimeoutException)
		{
			return new LibraryRootProbeResult(
				rootId,
				LibraryRootKinds.Normalize(root.RootKind),
				LibraryRootHealthStates.Offline,
				false,
				(long)effectiveTimeout.TotalMilliseconds,
				$"检测超过 {effectiveTimeout.TotalSeconds:0.#} 秒",
				DateTime.UtcNow);
		}
	}

	public async Task<IReadOnlyList<LibraryRootProbeResult>> ProbeManyAsync(
		IEnumerable<LibraryRootState> roots,
		int maxConcurrency = 2,
		CancellationToken cancellationToken = default)
	{
		LibraryRootState[] items = (roots ?? []).Where(root => root != null).ToArray();
		using SemaphoreSlim gate = new(Math.Clamp(maxConcurrency, 1, 8));
		Task<LibraryRootProbeResult>[] tasks = items.Select(async root =>
		{
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				return await ProbeAsync(root, cancellationToken: cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				gate.Release();
			}
		}).ToArray();
		return await Task.WhenAll(tasks).ConfigureAwait(false);
	}

	public async Task<PathAvailabilityResult> ProbePathAsync(
		string path,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return new PathAvailabilityResult(false, false, 0, "路径为空");
		}
		TimeSpan effectiveTimeout = NormalizeTimeout(timeout);
		string key = LibraryRootCatalog.NormalizePath(path);
		if (string.IsNullOrWhiteSpace(key))
		{
			return new PathAvailabilityResult(false, false, 0, "路径无效或过长");
		}
		Task<PathAvailabilityResult>? task = GetOrCreateProbe(_pathProbes, key, () => _probePath(key));
		if (task == null)
		{
			return new PathAvailabilityResult(false, false, 0, "检测队列繁忙，请稍后重试");
		}
		try
		{
			PathAvailabilityResult result = await task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
			return result;
		}
		catch (TimeoutException)
		{
			return new PathAvailabilityResult(false, true, (long)effectiveTimeout.TotalMilliseconds, $"访问超过 {effectiveTimeout.TotalSeconds:0.#} 秒");
		}
	}

	private Task<T>? GetOrCreateProbe<T>(
		ConcurrentDictionary<string, Lazy<Task<T>>> probes,
		string key,
		Func<Task<T>> probe)
	{
		if (probes.TryGetValue(key, out Lazy<Task<T>>? existing))
		{
			return existing.Value;
		}
		if (!_outstandingSlots.Wait(0))
		{
			return null;
		}

		Lazy<Task<T>> candidate = new(
			async () =>
			{
				await _executionGate.WaitAsync().ConfigureAwait(false);
				try
				{
					return await probe().ConfigureAwait(false);
				}
				finally
				{
					_executionGate.Release();
				}
			},
			LazyThreadSafetyMode.ExecutionAndPublication);
		Lazy<Task<T>> selected = probes.GetOrAdd(key, candidate);
		if (!ReferenceEquals(selected, candidate))
		{
			_outstandingSlots.Release();
			return selected.Value;
		}

		Task<T> task = selected.Value;
		_ = task.ContinueWith(
			completed =>
			{
				if (completed.IsFaulted)
				{
					_ = completed.Exception;
				}
				if (probes.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, selected)))
				{
					_outstandingSlots.Release();
				}
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
		return task;
	}

	private static Task<ProbeOutcome> DefaultProbeRootAsync(string path)
	{
		return Task.Run(() =>
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			string kind = DetectRootKind(path);
			try
			{
				FileAttributes attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.Directory) == 0)
				{
					return new ProbeOutcome(false, false, kind, stopwatch.Elapsed, "路径不是文件夹");
				}
				return new ProbeOutcome(true, false, kind, stopwatch.Elapsed, null);
			}
			catch (Exception ex) when (IsExpectedProbeException(ex))
			{
				return new ProbeOutcome(false, IsCredentialError(ex), kind, stopwatch.Elapsed, ShortError(ex));
			}
		});
	}

	private static Task<PathAvailabilityResult> DefaultProbePathAsync(string path)
	{
		return Task.Run(() =>
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			try
			{
				bool exists = File.Exists(path) || Directory.Exists(path);
				return new PathAvailabilityResult(exists, false, (long)stopwatch.Elapsed.TotalMilliseconds, exists ? null : "路径不存在或当前不可访问");
			}
			catch (Exception ex) when (IsExpectedProbeException(ex))
			{
				return new PathAvailabilityResult(false, false, (long)stopwatch.Elapsed.TotalMilliseconds, ShortError(ex));
			}
		});
	}

	private static string DetectRootKind(string path)
	{
		if (LibraryRootCatalog.IsUncPath(path))
		{
			return LibraryRootKinds.Unc;
		}
		try
		{
			string? driveRoot = Path.GetPathRoot(path);
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
		catch (Exception ex) when (IsExpectedProbeException(ex))
		{
			return LibraryRootKinds.Unknown;
		}
	}

	private static TimeSpan NormalizeTimeout(TimeSpan value)
	{
		return TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 250, 15000));
	}

	private static string ShortError(Exception exception)
	{
		string message = exception.Message?.Trim() ?? exception.GetType().Name;
		return message.Length <= 180 ? message : message[..180];
	}

	private static bool IsCredentialError(Exception exception)
	{
		if (exception is UnauthorizedAccessException)
		{
			return true;
		}
		int win32Code = exception.HResult & 0xffff;
		return win32Code is 5 or 86 or 1219 or 1326;
	}

	private static bool IsExpectedProbeException(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException or NotSupportedException or
			SecurityException or ArgumentException;
	}

	internal readonly record struct ProbeOutcome(
		bool Reachable,
		bool NeedsCredentials,
		string RootKind,
		TimeSpan Latency,
		string? Error);
}
