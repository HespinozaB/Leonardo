using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RevitMCP.Tools;

namespace RevitMCP.Bridge;

/// <summary>Ejecuta en el hilo de la API de Revit las peticiones encoladas por <see cref="CommandServer"/>.</summary>
internal sealed class RequestHandler : IExternalEventHandler
{
	private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();

	private readonly Dictionary<string, Func<UIApplication, Args, object>> _methods = ToolRegistry.Build();

	public void Enqueue(PendingRequest request) => _queue.Enqueue(request);

	public string GetName() => "RevitMCP";

	public void Execute(UIApplication app)
	{
		while (_queue.TryDequeue(out PendingRequest request))
		{
			if (request.Cancelled)
			{
				continue;
			}
			try
			{
				if (!_methods.TryGetValue(request.Method, out Func<UIApplication, Args, object> method))
				{
					request.Error = "Método desconocido: " + request.Method;
				}
				else
				{
					request.Result = method(app, new Args(request.Args));
				}
			}
			catch (Exception ex)
			{
				request.Error = ex.GetType().Name + ": " + ex.Message;
			}
			finally
			{
				request.Done.Set();
			}
		}
	}
}
