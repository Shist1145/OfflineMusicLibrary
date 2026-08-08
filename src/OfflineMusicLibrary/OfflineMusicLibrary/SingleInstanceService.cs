using System;
using System.Collections.Generic;
using System.Threading;

namespace OfflineMusicLibrary;

public sealed class SingleInstanceService : IDisposable
{
	public const string DefaultMutexName = @"Local\Shist1145.OfflineMusicLibrary.SingleInstance.v2";

	private static readonly object ProcessOwnershipLock = new object();

	private static readonly HashSet<string> ProcessOwnedNames = new HashSet<string>(StringComparer.Ordinal);

	private readonly string _mutexName;

	private Mutex? _mutex;

	private bool _ownsMutex;

	public SingleInstanceService(string? mutexName = null)
	{
		_mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
	}

	public bool TryAcquire()
	{
		if (_ownsMutex)
		{
			return true;
		}

		Mutex mutex = new Mutex(initiallyOwned: false, _mutexName);
		bool acquired;
		try
		{
			acquired = mutex.WaitOne(0, exitContext: false);
		}
		catch (AbandonedMutexException)
		{
			acquired = true;
		}

		if (!acquired)
		{
			mutex.Dispose();
			return false;
		}

		lock (ProcessOwnershipLock)
		{
			if (!ProcessOwnedNames.Add(_mutexName))
			{
				mutex.ReleaseMutex();
				mutex.Dispose();
				return false;
			}
		}

		_mutex = mutex;
		_ownsMutex = true;
		return true;
	}

	public void Dispose()
	{
		if (!_ownsMutex)
		{
			_mutex?.Dispose();
			_mutex = null;
			return;
		}

		lock (ProcessOwnershipLock)
		{
			ProcessOwnedNames.Remove(_mutexName);
		}

		try
		{
			_mutex?.ReleaseMutex();
		}
		catch (ApplicationException)
		{
		}
		finally
		{
			_ownsMutex = false;
			_mutex?.Dispose();
			_mutex = null;
		}
	}
}
