using System;
using System.Collections.Generic;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using System.IO.MemoryMappedFiles;
namespace SignalTesting
{
	public interface ISignal<T> : IDisposable
	{
		void Send(T signal);

		T Receive();

		T Receive(int timeOut);
	}
	
	public static class SignalFactory 
	{
		public static ISignal<T> GetInstanse<T>()
		{
			return new Signal<T>();
		}

		public static ISignal<T> GetInstanse<T>(string name)
		{
			return new CrossProcessSignal<T>(name);
		}
	}
	
	// Signal — internal класс для синхронизации внутри одного процесса. Для синхронизации необходима ссылка на объект.
	internal class Signal<T> : ISignal<T>
	{
		private T buffer;

		Dictionary<int,AutoResetEvent> events = new Dictionary<int, AutoResetEvent>();

		// Объект sync будет нам необходим при вызове Send, дабы несколько потоков не начали перетирать буфер.
		private volatile object sync = new object();

		private bool isDisposabled = false;

		~Signal()
		{
			if (!isDisposabled)
			{
				Dispose();
			}
		}

		/*
		 * GetEvents() достает из словаря AutoResetEvent если есть, если нет то создает новый и кладет его в словарь.

waiter.WaitOne() блокировка потока до ожидания сигнала.

waiter.Reset() сброс текущего состояния AutoResetEvent. Следующий вызов WaitOne приведет к блокировке потока.
		 * */
		public T Receive()
		{
			var waiter = GetEvents();
			waiter.WaitOne();
			waiter.Reset();
			return buffer;
		}

		public T Receive(int timeOut)
		{
			var waiter = GetEvents();
			waiter.WaitOne(timeOut);
			waiter.Reset();
			return buffer;
		}

		public void Send(T signal)
		{
			lock (sync)
			{
				buffer = signal;
				foreach(var autoResetEvent in events.Values)
				{
					autoResetEvent.Set();
				}
			}
		}

		private AutoResetEvent GetEvents()
		{
			var threadId = Thread.CurrentThread.ManagedThreadId;
			AutoResetEvent autoResetEvent;
			if (!events.ContainsKey(threadId))
			{
				autoResetEvent = new AutoResetEvent(false);
				events.Add(threadId, autoResetEvent);
			}
			else
			{
				autoResetEvent = events[threadId];
			}
			return autoResetEvent;
		}

		public void Dispose()
		{
			foreach(var resetEvent in events.Values)
			{
				resetEvent.Dispose();
			}
			isDisposabled = true;
		}
	}
	
	internal interface IBuffer<T>
	{
		void SetBuffer(T entity);

		T GetBuffer();
	}
	
	public interface ISerializer<T>
	{
		byte[] Serialize(T entity);

		T Deserialize(byte[] buffer);
	}
	
	public interface ISignalFactory<T>
	{
		ISignal<T> GetInstanse();
	}
	
	internal class SharedBuffer<T> : IBuffer<T>
	{
		private string bufferName;

		private ISerializer<T> serializer;

		public SharedBuffer(string bufferName)
		{
			this.bufferName = String.Format("Signal/{0}", bufferName);
			serializer = new ComplexSerializer<T>();
		}

		public T GetBuffer()
		{
			try
			{
				MemoryMappedFile file = MemoryMappedFile.OpenExisting(bufferName);
				using (var stream = file.CreateViewStream())
				{
					byte[] buffer = new byte[stream.Length];
					stream.Read(buffer, 0, (int)stream.Length);
					return serializer.Deserialize(buffer);
				}
			}
			catch { }
			return default(T);
		}

		public void SetBuffer(T entity)
		{
			byte[] entityBytes = serializer.Serialize(entity);
			MemoryMappedFile file = MemoryMappedFile.CreateOrOpen(bufferName, entityBytes.Length, MemoryMappedFileAccess.ReadWrite);
			using (var stream = file.CreateViewStream())
			{
				stream.Write(entityBytes, 0, entityBytes.Length);
			}
		}

	}
	
	// Kласс который может синхронизировать потоки в отдельных процессах
	internal class CrossProcessSignal<T> : ISignal<T>
	{
		private volatile object sync = new object();

		private bool isDisposabled = false;

		private volatile bool isLocalSignalFlag = false;

		private Signal<T> localSignal = new Signal<T>();

		private EventWaitHandle handle;

		private IBuffer<T> buffer;

		private string mutexName;

		public CrossProcessSignal(string signalName)
		{
			handle = CreateSignal(signalName);

			buffer = new SharedBuffer<T>(mutexName);

			this.mutexName = signalName;

			Task.Factory.StartNew(() =>
			{
				CrossProcessWaiter();
			});
		}

		~CrossProcessSignal()
		{
			if (!isDisposabled)
			{
				Dispose();
			}
		}

		public T Receive()
		{
			return localSignal.Receive();
		}

		public T Receive(int timeOut)
		{
			return localSignal.Receive(timeOut);
		}

		public void Send(T signal)
		{
			buffer.SetBuffer(signal);
			isLocalSignalFlag = true;
			handle.Set();
		}

		public void Dispose()
		{
			localSignal.Dispose();
			handle.Dispose();
			isDisposabled = true;
		}

		private EventWaitHandle CreateSignal(string name)
		{
			EventWaitHandle result = null;

//			if (EventWaitHandle.TryOpenExisting(name, out result))
//			{
//				return result;
//			}
//			else
//			{
				// code from https://stackoverflow.com/questions/2590334/creating-a-cross-process-eventwaithandle
				// user https://stackoverflow.com/users/241462/dean-harding
				var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
				var rule = new EventWaitHandleAccessRule(users, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
										  AccessControlType.Allow);
				var security = new EventWaitHandleSecurity();
				security.AddAccessRule(rule);

				bool created;
				var wh = new EventWaitHandle(false, EventResetMode.ManualReset, name, out created, security);
				return wh;
//			}
		}

		private void CrossProcessWaiter()
		{
			while (!isDisposabled)
			{
				if (handle.WaitOne())
				{
					T entity = buffer.GetBuffer();
					localSignal.Send(entity);
				}
				if (isLocalSignalFlag)
				{
					Thread.Sleep(0);
					handle.Reset();
					isLocalSignalFlag = false;
				}
			}
		}
	}
	
	
	class Program
	{
		public static void Main(string[] args)
		{
			
			Console.ReadKey(true);
		}
	}
}
