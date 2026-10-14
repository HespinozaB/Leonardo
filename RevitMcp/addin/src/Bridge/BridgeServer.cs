using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitMcp.Tools;

namespace RevitMcp.Bridge;

/// <summary>
/// Servidor TCP en 127.0.0.1 (solo accesible desde este equipo). Protocolo: una línea JSON por
/// conexión, <c>{"token","tool","args"}</c>, y una línea JSON de respuesta,
/// <c>{"ok":true,"result":...}</c> o <c>{"ok":false,"error":"..."}</c>.
/// </summary>
internal sealed class BridgeServer : IDisposable
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
	private const int MaxRequestChars = 4 * 1024 * 1024;

	private readonly BridgeConfig _config;
	private readonly RevitDispatcher _dispatcher;
	private readonly CancellationTokenSource _cts = new();
	private TcpListener _listener;

	public BridgeServer(BridgeConfig config, RevitDispatcher dispatcher)
	{
		_config = config;
		_dispatcher = dispatcher;
	}

	public bool IsRunning { get; private set; }
	public string LastError { get; private set; }
	public int RequestCount;

	public void Start()
	{
		try
		{
			_listener = new TcpListener(IPAddress.Loopback, _config.Port);
			_listener.Start();
			IsRunning = true;
			_ = Task.Run(AcceptLoop);
		}
		catch (Exception ex)
		{
			// Normalmente: el puerto lo usa otra instancia de Revit.
			LastError = ex.Message;
			IsRunning = false;
		}
	}

	private async Task AcceptLoop()
	{
		while (!_cts.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await _listener.AcceptTcpClientAsync(_cts.Token);
			}
			catch (Exception)
			{
				return;
			}

			_ = Task.Run(() => Handle(client));
		}
	}

	private async Task Handle(TcpClient client)
	{
		using (client)
		{
			NetworkStream stream = client.GetStream();
			var reader = new StreamReader(stream, new UTF8Encoding(false));
			var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

			JsonObject response;
			try
			{
				string line = await ReadLine(reader);
				response = await Process(line);
			}
			catch (Exception ex)
			{
				response = Error(ex);
			}

			try
			{
				await writer.WriteLineAsync(response.ToJsonString(Json.Options));
			}
			catch (Exception)
			{
				// El cliente cerró la conexión.
			}
		}
	}

	private static async Task<string> ReadLine(StreamReader reader)
	{
		var sb = new StringBuilder();
		var buffer = new char[8192];
		while (true)
		{
			int read = await reader.ReadAsync(buffer, 0, buffer.Length);
			if (read == 0)
			{
				return sb.ToString();
			}

			int newline = Array.IndexOf(buffer, '\n', 0, read);
			sb.Append(buffer, 0, newline >= 0 ? newline : read);
			if (newline >= 0)
			{
				return sb.ToString();
			}

			if (sb.Length > MaxRequestChars)
			{
				throw new InvalidOperationException("Petición demasiado grande.");
			}
		}
	}

	private async Task<JsonObject> Process(string line)
	{
		JsonObject request = JsonNode.Parse(line) as JsonObject
			?? throw new InvalidOperationException("La petición debe ser un objeto JSON.");

		string token = request["token"]?.GetValue<string>() ?? "";
		if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_config.Token)))
		{
			return new JsonObject { ["ok"] = false, ["error"] = "Token inválido. Revisa %AppData%\\EMASY\\RevitMcp\\bridge.json." };
		}

		string tool = request["tool"]?.GetValue<string>() ?? throw new InvalidOperationException("Falta 'tool'.");
		JsonObject args = request["args"] as JsonObject ?? new JsonObject();
		Interlocked.Increment(ref RequestCount);

		if (tool == "ping")
		{
			return Ok(new JsonObject { ["pong"] = true });
		}

		if (!ToolRegistry.TryGet(tool, out ToolHandler handler))
		{
			return new JsonObject { ["ok"] = false, ["error"] = $"Herramienta desconocida: {tool}" };
		}

		Task<object> work = _dispatcher.Run(app => handler(app, args));
		if (await Task.WhenAny(work, Task.Delay(Timeout)) != work)
		{
			return new JsonObject
			{
				["ok"] = false,
				["error"] = "Revit no respondió a tiempo. Puede tener un diálogo abierto o estar ocupado en un comando; " +
					"ciérralo y vuelve a intentarlo."
			};
		}

		try
		{
			return Ok(Json.ToNode(await work));
		}
		catch (Exception ex)
		{
			return Error(ex);
		}
	}

	private static JsonObject Ok(JsonNode result) => new() { ["ok"] = true, ["result"] = result };

	private static JsonObject Error(Exception ex)
	{
		while (ex is AggregateException { InnerException: not null } || ex is System.Reflection.TargetInvocationException { InnerException: not null })
		{
			ex = ex.InnerException;
		}

		return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
	}

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			_listener?.Stop();
		}
		catch (Exception)
		{
		}

		_dispatcher.Cancel();
		IsRunning = false;
	}
}
