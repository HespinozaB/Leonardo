using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace RevitMcp.Bridge;

/// <summary>
/// Puerto y token del puente. Se guardan en %AppData%\EMASY\RevitMcp\bridge.json, que el
/// servidor MCP lee para saber dónde conectarse. El token evita que otro programa del equipo
/// mande órdenes a Revit.
/// </summary>
internal sealed class BridgeConfig
{
	public const int DefaultPort = 8765;

	public int Port { get; set; } = DefaultPort;
	public string Token { get; set; }

	public static string FolderPath =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "RevitMcp");

	public static string FilePath => Path.Combine(FolderPath, "bridge.json");

	public static BridgeConfig LoadOrCreate()
	{
		BridgeConfig config = null;
		try
		{
			if (File.Exists(FilePath))
			{
				config = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(FilePath));
			}
		}
		catch (Exception)
		{
			// Archivo corrupto: se regenera.
		}

		config ??= new BridgeConfig();
		if (config.Port <= 0 || config.Port > 65535)
		{
			config.Port = DefaultPort;
		}

		if (string.IsNullOrWhiteSpace(config.Token))
		{
			config.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
			Directory.CreateDirectory(FolderPath);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
		}

		return config;
	}
}
