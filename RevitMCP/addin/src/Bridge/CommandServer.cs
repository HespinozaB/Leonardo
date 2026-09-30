using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitMCP.Bridge;

/// <summary>
/// Servidor TCP en 127.0.0.1. Protocolo: una petición JSON por línea
/// {"id":..,"method":"..","params":{..}} → una respuesta JSON por línea
/// {"id":..,"ok":true,"result":..} o {"id":..,"ok":false,"error":".."}.
/// Las peticiones se ejecutan en el hilo de Revit mediante un ExternalEvent.
/// </summary>
internal sealed class CommandServer
{
	public static readonly int Port = ReadPort();

	private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

	private readonly RequestHandler _handler;

	private readonly ExternalEvent _externalEvent;

	private TcpListener _listener;

	private Thread _acceptThread;

	private volatile bool _running;

	public CommandServer(RequestHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_externalEvent = externalEvent;
	}

	public bool IsRunning => _running;

	public string LastError { get; private set; }

	public void Start()
	{
		if (_running)
		{
			return;
		}
		try
		{
			_listener = new TcpListener(IPAddress.Loopback, Port);
			_listener.Start();
			_running = true;
			LastError = null;
			_acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RevitMCP-Accept" };
			_acceptThread.Start();
		}
		catch (Exception ex)
		{
			_running = false;
			LastError = ex.Message;
		}
	}

	public void Stop()
	{
		_running = false;
		try
		{
			_listener?.Stop();
		}
		catch
		{
			// Ignorar: ya cerrado.
		}
		_listener = null;
	}

	private static int ReadPort()
	{
		string value = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
		return int.TryParse(value, out int port) && port > 0 && port < 65536 ? port : 8765;
	}

	private void AcceptLoop()
	{
		while (_running)
		{
			TcpClient client;
			try
			{
				client = _listener.AcceptTcpClient();
			}
			catch
			{
				// Listener detenido.
				break;
			}
			var thread = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "RevitMCP-Client" };
			thread.Start();
		}
	}

	private void HandleClient(TcpClient client)
	{
		using (client)
		using (NetworkStream stream = client.GetStream())
		using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
		using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
		{
			try
			{
				string line;
				while (_running && (line = reader.ReadLine()) != null)
				{
					if (line.Trim().Length == 0)
					{
						continue;
					}
					writer.WriteLine(Process(line));
				}
			}
			catch (IOException)
			{
				// Cliente desconectado.
			}
		}
	}

	private string Process(string line)
	{
		object id = null;
		try
		{
			Dictionary<string, object> request = Json.ParseObject(line);
			request.TryGetValue("id", out id);
			string method = request.TryGetValue("method", out object m) ? m as string : null;
			if (string.IsNullOrEmpty(method))
			{
				return Json.Error(id, "Falta 'method'.");
			}
			var args = request.TryGetValue("params", out object p) && p is Dictionary<string, object> d
				? d
				: new Dictionary<string, object>();

			// "ping" responde sin pasar por el hilo de Revit.
			if (method == "ping")
			{
				return Json.Ok(id, new Dictionary<string, object> { ["pong"] = true, ["port"] = Port });
			}

			var pending = new PendingRequest(method, args);
			_handler.Enqueue(pending);
			_externalEvent.Raise();
			if (!pending.Done.Wait(RequestTimeout))
			{
				pending.Cancelled = true;
				return Json.Error(id, "Tiempo de espera agotado: Revit está ocupado (¿diálogo abierto o comando en curso?).");
			}
			return pending.Error != null ? Json.Error(id, pending.Error) : Json.Ok(id, pending.Result);
		}
		catch (Exception ex)
		{
			return Json.Error(id, ex.Message);
		}
	}
}

internal sealed class PendingRequest
{
	public PendingRequest(string method, Dictionary<string, object> args)
	{
		Method = method;
		Args = args;
	}

	public string Method { get; }

	public Dictionary<string, object> Args { get; }

	public object Result { get; set; }

	public string Error { get; set; }

	public volatile bool Cancelled;

	public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);
}
