using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMcp.Tools;

/// <summary>Lectura de argumentos JSON. Longitudes y coordenadas llegan en milímetros.</summary>
internal static class Args
{
	public static string Str(JsonObject args, string key, string fallback = null)
	{
		JsonNode node = args[key];
		if (node == null)
		{
			return fallback;
		}

		return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
	}

	public static string RequiredStr(JsonObject args, string key) =>
		Str(args, key) is { Length: > 0 } value ? value : throw new ArgumentException($"Falta el argumento '{key}'.");

	public static double? Dbl(JsonObject args, string key)
	{
		JsonNode node = args[key];
		if (node == null)
		{
			return null;
		}

		return node.GetValueKind() == JsonValueKind.String
			? double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture)
			: node.GetValue<double>();
	}

	public static double RequiredDbl(JsonObject args, string key) =>
		Dbl(args, key) ?? throw new ArgumentException($"Falta el argumento '{key}'.");

	public static int Int(JsonObject args, string key, int fallback) => (int)(Dbl(args, key) ?? fallback);

	public static bool Bool(JsonObject args, string key, bool fallback)
	{
		JsonNode node = args[key];
		return node == null ? fallback : node.GetValueKind() == JsonValueKind.True;
	}

	public static ElementId Id(JsonObject args, string key)
	{
		JsonNode node = args[key];
		return node == null ? null : ToId(node);
	}

	public static ElementId RequiredId(JsonObject args, string key) =>
		Id(args, key) ?? throw new ArgumentException($"Falta el argumento '{key}'.");

	public static List<ElementId> Ids(JsonObject args, string key)
	{
		if (args[key] is not JsonArray array)
		{
			JsonNode single = args[key];
			return single == null ? new List<ElementId>() : new List<ElementId> { ToId(single) };
		}

		return array.Where(n => n != null).Select(ToId).ToList();
	}

	public static List<ElementId> RequiredIds(JsonObject args, string key)
	{
		List<ElementId> ids = Ids(args, key);
		return ids.Count > 0 ? ids : throw new ArgumentException($"Falta el argumento '{key}' (lista de ids).");
	}

	private static ElementId ToId(JsonNode node)
	{
		long value = node.GetValueKind() == JsonValueKind.String
			? long.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture)
			: node.GetValue<long>();
		return new ElementId(value);
	}

	/// <summary>Punto {x, y, z} en mm (z opcional) a unidades internas.</summary>
	public static XYZ Point(JsonObject args, string key, bool required = true)
	{
		if (args[key] is not JsonObject p)
		{
			return required ? throw new ArgumentException($"Falta el punto '{key}' ({{x, y, z}} en mm).") : null;
		}

		return Units.FromMm(Dbl(p, "x") ?? 0, Dbl(p, "y") ?? 0, Dbl(p, "z") ?? 0);
	}

	public static List<XYZ> Points(JsonObject args, string key)
	{
		if (args[key] is not JsonArray array || array.Count == 0)
		{
			throw new ArgumentException($"Falta la lista de puntos '{key}'.");
		}

		return array.OfType<JsonObject>()
			.Select(p => Units.FromMm(Dbl(p, "x") ?? 0, Dbl(p, "y") ?? 0, Dbl(p, "z") ?? 0))
			.ToList();
	}
}
