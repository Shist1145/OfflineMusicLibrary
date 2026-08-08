using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

if (args.Length < 2)
{
	Console.Error.WriteLine("Usage: StateStoreBench <OfflineMusicLibrary.dll> <library-v2.json> [iterations]");
	return 2;
}

string assemblyPath = Path.GetFullPath(args[0]);
string sourceStatePath = Path.GetFullPath(args[1]);
int iterations = args.Length >= 3 && int.TryParse(args[2], out int parsedIterations)
	? Math.Max(1, parsedIterations)
	: 3;
string temporaryDirectory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary-StateStoreBench-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);

BenchmarkLoadContext? loadContext = null;
try
{
	string primaryPath = Path.Combine(temporaryDirectory, "library-v2.json");
	string backupPath = Path.Combine(temporaryDirectory, "library-v2.backup.json");
	File.Copy(sourceStatePath, primaryPath);
	File.Copy(sourceStatePath, backupPath);

	loadContext = new BenchmarkLoadContext(assemblyPath);
	Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
	Type storeType = assembly.GetType("OfflineMusicLibrary.AppStore", throwOnError: true)!;
	object store = Activator.CreateInstance(storeType, [temporaryDirectory])
		?? throw new InvalidOperationException("Could not construct AppStore.");

	MethodInfo loadMethod = storeType.GetMethod("LoadAsync", Type.EmptyTypes)
		?? throw new MissingMethodException(storeType.FullName, "LoadAsync");
	Stopwatch stopwatch = Stopwatch.StartNew();
	Task loadTask = (Task)(loadMethod.Invoke(store, null)
		?? throw new InvalidOperationException("LoadAsync returned null."));
	await loadTask.ConfigureAwait(false);
	stopwatch.Stop();
	object state = loadTask.GetType().GetProperty("Result")?.GetValue(loadTask)
		?? throw new InvalidOperationException("LoadAsync did not return a state.");
	state.GetType().GetProperty("StateBackupEnabled")?.SetValue(state, true);

	MethodInfo saveMethod = storeType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
		.Where(method => method.Name == "SaveAsync")
		.OrderBy(method => method.GetParameters().Length)
		.First();
	Console.WriteLine($"assembly={assemblyPath}");
	Console.WriteLine($"version={assembly.GetName().Version}");
	Console.WriteLine($"state_bytes={new FileInfo(sourceStatePath).Length}");
	Console.WriteLine($"load_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");

	for (int iteration = 1; iteration <= iterations; iteration++)
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		stopwatch.Restart();
		ParameterInfo[] parameters = saveMethod.GetParameters();
		object?[] saveArguments = parameters.Length == 1
			? [state]
			: [state, CancellationToken.None];
		Task saveTask = (Task)(saveMethod.Invoke(store, saveArguments)
			?? throw new InvalidOperationException("SaveAsync returned null."));
		await saveTask.ConfigureAwait(false);
		stopwatch.Stop();
		long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
		Console.WriteLine($"save_{iteration}_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
		Console.WriteLine($"save_{iteration}_allocated_bytes={allocatedBytes}");
	}

	(double dispatcherSaveMilliseconds, double maximumHeartbeatGapMilliseconds) = await MeasureDispatcherSaveAsync(
		() => InvokeSaveAsync(saveMethod, store, state)).ConfigureAwait(false);
	Console.WriteLine($"dispatcher_save_ms={dispatcherSaveMilliseconds:F1}");
	Console.WriteLine($"dispatcher_max_heartbeat_gap_ms={maximumHeartbeatGapMilliseconds:F1}");

	foreach (string name in new[] { "library-v2.json", "library-v2.backup.json", "library-v2.previous.json" })
	{
		string path = Path.Combine(temporaryDirectory, name);
		Console.WriteLine($"{name}_bytes={(File.Exists(path) ? new FileInfo(path).Length : 0)}");
	}
	Console.WriteLine($"temporary_files={Directory.EnumerateFiles(temporaryDirectory, "*.tmp").Count()}");
	return 0;
}
finally
{
	loadContext?.Unload();
	try
	{
		Directory.Delete(temporaryDirectory, recursive: true);
	}
	catch
	{
	}
}

static Task InvokeSaveAsync(MethodInfo saveMethod, object store, object state)
{
	ParameterInfo[] parameters = saveMethod.GetParameters();
	object?[] saveArguments = parameters.Length == 1
		? [state]
		: [state, CancellationToken.None];
	return (Task)(saveMethod.Invoke(store, saveArguments)
		?? throw new InvalidOperationException("SaveAsync returned null."));
}

static async Task<(double SaveMilliseconds, double MaximumHeartbeatGapMilliseconds)> MeasureDispatcherSaveAsync(Func<Task> saveAction)
{
	TaskCompletionSource<Dispatcher> dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	Thread dispatcherThread = new(() =>
	{
		Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
		dispatcherReady.SetResult(dispatcher);
		Dispatcher.Run();
	})
	{
		IsBackground = true,
		Name = "StateStoreBench-Dispatcher"
	};
	dispatcherThread.SetApartmentState(ApartmentState.STA);
	dispatcherThread.Start();
	Dispatcher dispatcher = await dispatcherReady.Task.ConfigureAwait(false);

	double maximumGapMilliseconds = 0;
	long lastHeartbeat = 0;
	DispatcherTimer? heartbeat = null;
	await dispatcher.InvokeAsync(() =>
	{
		lastHeartbeat = Stopwatch.GetTimestamp();
		heartbeat = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(10)
		};
		heartbeat.Tick += (_, _) =>
		{
			long now = Stopwatch.GetTimestamp();
			maximumGapMilliseconds = Math.Max(maximumGapMilliseconds, Stopwatch.GetElapsedTime(lastHeartbeat, now).TotalMilliseconds);
			lastHeartbeat = now;
		};
		heartbeat.Start();
	});

	await Task.Delay(100).ConfigureAwait(false);
	Stopwatch stopwatch = Stopwatch.StartNew();
	await dispatcher.InvokeAsync(saveAction).Task.Unwrap().ConfigureAwait(false);
	stopwatch.Stop();
	await Task.Delay(100).ConfigureAwait(false);
	await dispatcher.InvokeAsync(() => heartbeat?.Stop());
	dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
	dispatcherThread.Join(TimeSpan.FromSeconds(5));
	return (stopwatch.Elapsed.TotalMilliseconds, maximumGapMilliseconds);
}

sealed class BenchmarkLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
	private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		string? path = _resolver.ResolveAssemblyToPath(assemblyName);
		return path == null ? null : LoadFromAssemblyPath(path);
	}
}
