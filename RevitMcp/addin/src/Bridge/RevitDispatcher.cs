using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace RevitMcp.Bridge;

/// <summary>
/// La API de Revit solo se puede usar desde su propio hilo. Las peticiones que llegan por red se
/// encolan aquí y se ejecutan cuando Revit atiende el ExternalEvent (en cuanto está libre).
/// </summary>
internal sealed class RevitDispatcher : IExternalEventHandler
{
	private readonly ConcurrentQueue<(Func<UIApplication, object> Work, TaskCompletionSource<object> Result)> _queue = new();
	private readonly ExternalEvent _event;

	/// <summary>Debe crearse en un contexto de API válido (OnStartup).</summary>
	public RevitDispatcher()
	{
		_event = ExternalEvent.Create(this);
	}

	public Task<object> Run(Func<UIApplication, object> work)
	{
		var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
		_queue.Enqueue((work, tcs));
		_event.Raise();
		return tcs.Task;
	}

	public void Execute(UIApplication app)
	{
		while (_queue.TryDequeue(out var item))
		{
			if (item.Result.Task.IsCompleted)
			{
				// El cliente ya se cansó de esperar (timeout): no se ejecuta tarde.
				continue;
			}

			try
			{
				item.Result.TrySetResult(item.Work(app));
			}
			catch (Exception ex)
			{
				item.Result.TrySetException(ex);
			}
		}
	}

	public void Cancel()
	{
		while (_queue.TryDequeue(out var item))
		{
			item.Result.TrySetCanceled();
		}
	}

	public string GetName() => "EMASY MCP";
}
