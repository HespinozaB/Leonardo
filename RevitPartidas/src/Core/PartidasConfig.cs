using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitPartidas.Core;

/// <summary>De dónde sale el código de un nivel.</summary>
public enum LinkMode
{
	/// <summary>Se deriva del código de partida más profundo (lo normal).</summary>
	Codigo,

	/// <summary>Un código fijo para todo el modelo (p. ej. nivel 1 = 03 ARQUITECTURA).</summary>
	Fijo,

	/// <summary>Un parámetro de Revit cuyo valor coincide con la descripción del nivel (p. ej. PR_Elemento_Etapa = "ETAPA 1" → 03.01).</summary>
	Parametro
}

public sealed class LevelLink
{
	public LinkMode Mode = LinkMode.Codigo;

	/// <summary>Para Fijo: el código (p. ej. "03").</summary>
	public string FixedCode = string.Empty;

	/// <summary>Para Parámetro: nombre del parámetro de Revit (de ejemplar o de tipo).</summary>
	public string ParameterName = string.Empty;

	/// <summary>
	/// Regla del filtro: valor del parámetro (simplificado) → código del nivel ("" = ignorar ese valor). Los valores que no están
	/// aquí se enlazan solos si coinciden con la descripción o el código de un nivel del presupuesto.
	/// </summary>
	public Dictionary<string, string> ValueMap = new Dictionary<string, string>(StringComparer.Ordinal);

	public LevelLink Clone() => new LevelLink
	{
		Mode = Mode,
		FixedCode = FixedCode,
		ParameterName = ParameterName,
		ValueMap = new Dictionary<string, string>(ValueMap, StringComparer.Ordinal)
	};
}

/// <summary>
/// Los pasos 1 a 3: cuántos niveles tiene la jerarquía, cómo se llaman los parámetros y qué niveles se enlazan a datos
/// que ya existen en el modelo. Se guarda en %AppData%\EMASY\Partidas.ini.
/// </summary>
public sealed class PartidasConfig
{
	public const string DefaultPrefix = "DYCUSA_PARTIDA ";

	/// <summary>Paso 1: número de niveles (órdenes) de la jerarquía.</summary>
	public int Levels = 6;

	/// <summary>Paso 2: prefijo del nombre (el número de nivel va detrás).</summary>
	public string Prefix = DefaultPrefix;

	/// <summary>Paso 2: número con dos cifras ("DYCUSA_01") o una ("DYCUSA_PARTIDA 1").</summary>
	public bool TwoDigits;

	/// <summary>Paso 3: enlace de cada nivel (índice 0 = nivel 1).</summary>
	public List<LevelLink> Links = new List<LevelLink>();

	/// <summary>El código de montaje manda sobre lo ya escrito en los parámetros de nivel.</summary>
	public bool PreferSource = true;

	public string[] Names => Enumerable.Range(1, Levels).Select(Name).ToArray();

	public string Name(int level) => Prefix + (TwoDigits ? level.ToString("00", CultureInfo.InvariantCulture) : level.ToString(CultureInfo.InvariantCulture));

	public LevelLink Link(int index)
	{
		while (Links.Count <= index)
		{
			Links.Add(new LevelLink());
		}

		return Links[index];
	}

	public PartidasConfig Clone()
	{
		var copy = (PartidasConfig)MemberwiseClone();
		copy.Links = Links.Select(l => l.Clone()).ToList();
		return copy;
	}

	public static PartidasConfig Load()
	{
		var config = new PartidasConfig();
		if (int.TryParse(Settings.Get("levels"), out int levels) && levels >= 1 && levels <= PartidaCode.MaxLevels)
		{
			config.Levels = levels;
		}

		config.Prefix = Settings.Get("prefix") ?? config.Prefix;
		config.TwoDigits = Settings.Get("twoDigits") == "1";
		config.PreferSource = Settings.Get("preferMontaje") != "0";
		for (int i = 0; i < PartidaCode.MaxLevels; i++)
		{
			string value = Settings.Get("link" + (i + 1));
			if (string.IsNullOrEmpty(value))
			{
				continue;
			}

			// Formato: modo|código fijo|parámetro
			string[] parts = value.Split('|');
			LevelLink link = config.Link(i);
			if (Enum.TryParse(parts[0], out LinkMode mode))
			{
				link.Mode = mode;
			}

			link.FixedCode = parts.Length > 1 ? parts[1] : string.Empty;
			link.ParameterName = parts.Length > 2 ? parts[2] : string.Empty;

			// Regla: valor=>código separados por ¦
			foreach (string pair in (Settings.Get("rule" + (i + 1)) ?? string.Empty).Split(new[] { '¦' }, StringSplitOptions.RemoveEmptyEntries))
			{
				int arrow = pair.IndexOf("=>", StringComparison.Ordinal);
				if (arrow > 0)
				{
					link.ValueMap[pair.Substring(0, arrow)] = pair.Substring(arrow + 2);
				}
			}
		}

		return config;
	}

	public void Save()
	{
		Settings.Set("levels", Levels.ToString(CultureInfo.InvariantCulture));
		Settings.Set("prefix", Prefix);
		Settings.Set("twoDigits", TwoDigits ? "1" : "0");
		Settings.Set("preferMontaje", PreferSource ? "1" : "0");
		for (int i = 0; i < PartidaCode.MaxLevels; i++)
		{
			LevelLink link = i < Links.Count ? Links[i] : new LevelLink();
			Settings.Set("link" + (i + 1), $"{link.Mode}|{link.FixedCode?.Replace("|", string.Empty)}|{link.ParameterName?.Replace("|", string.Empty)}");
			Settings.Set("rule" + (i + 1), string.Join("¦", link.ValueMap.Select(p => p.Key.Replace("¦", string.Empty) + "=>" + p.Value)));
		}
	}
}

/// <summary>Preferencias del usuario en %AppData%\EMASY\Partidas.ini (clave=valor).</summary>
public static class Settings
{
	private static readonly object Gate = new object();

	private static Dictionary<string, string> _cache;

	private static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "Partidas.ini");

	private static Dictionary<string, string> Values()
	{
		if (_cache != null)
		{
			return _cache;
		}

		_cache = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			if (File.Exists(FilePath))
			{
				foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
				{
					int equals = line.IndexOf('=');
					if (equals > 0)
					{
						_cache[line.Substring(0, equals)] = line.Substring(equals + 1);
					}
				}
			}
		}
		catch (Exception)
		{
			// Las preferencias son una comodidad: si no se pueden leer, se sigue sin ellas.
		}

		return _cache;
	}

	public static string Get(string key)
	{
		lock (Gate)
		{
			return Values().TryGetValue(key, out string value) ? value : null;
		}
	}

	public static void Set(string key, string value)
	{
		lock (Gate)
		{
			Values()[key] = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
				File.WriteAllLines(FilePath, _cache.Select(p => p.Key + "=" + p.Value), Encoding.UTF8);
			}
			catch (Exception)
			{
			}
		}
	}
}
