using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitParamFill.Core;

/// <summary>Estado de un valor propuesto.</summary>
public enum FillStatus
{
	/// <summary>No hay dato ni deducción posible.</summary>
	SinDato,

	/// <summary>Deducido (mismo tipo, elemento cercano, palabras clave…): se escribe solo si el usuario lo acepta.</summary>
	Inferido,

	/// <summary>Sale directamente del origen de la regla.</summary>
	Directo,

	/// <summary>Escrito a mano (libre personalizado o masivo).</summary>
	Manual,

	/// <summary>Ya tiene ese valor.</summary>
	Igual,

	/// <summary>Ya tiene otro valor y la regla no sobrescribe.</summary>
	Conservado,

	/// <summary>El parámetro no existe en el elemento o es de solo lectura.</summary>
	Bloqueado
}

/// <summary>Lo que se necesita de un elemento para deducir (sin Revit).</summary>
public sealed class ElementInfo
{
	public long Id;

	public long TypeId = -1;

	public string Category = string.Empty;

	public string Family = string.Empty;

	public string TypeName = string.Empty;

	public string Level = string.Empty;

	public double X, Y, Z;

	/// <summary>Cota inferior de su caja (para deducir el nivel por altura).</summary>
	public double MinZ;

	public bool HasLocation;

	public string FamilyType => Family.Length > 0 ? Family + " : " + TypeName : TypeName;
}

/// <summary>Un valor a escribir en un parámetro de un elemento.</summary>
public sealed class FillEntry
{
	public ElementInfo Element;

	public int RuleIndex;

	public string Target;

	/// <summary>El parámetro destino es de tipo: se escribe una vez en el tipo.</summary>
	public bool OnType;

	public string Current = string.Empty;

	public string NewValue;

	public FillStatus Status;

	public string Reason = string.Empty;

	/// <summary>Se escribirá (los directos sí; los inferidos cuando el usuario los acepta).</summary>
	public bool Accepted;

	public bool Writable => Status == FillStatus.Directo || Status == FillStatus.Inferido || Status == FillStatus.Manual;
}

/// <summary>Un nivel del modelo (para deducir por altura).</summary>
public sealed class LevelInfo
{
	public string Name;

	public double Elevation;
}

/// <summary>
/// Deduce los valores que faltan, en este orden (según la regla): sector más cercano (sectorización), nivel por altura (nivel),
/// el valor más frecuente de su tipo, el del elemento más cercano con valor, palabras clave / parecido de nombre y el valor por
/// defecto. Cada deducción guarda su motivo.
/// </summary>
public static class Inference
{
	/// <summary>Tamaño de celda del índice espacial (pies, ≈ 6 m).</summary>
	private const double Cell = 20;

	/// <summary>Distancia máxima para copiar el valor de un elemento cercano (pies, ≈ 15 m).</summary>
	private const double NearestLimit = 50;

	public static void Run(FillRule rule, List<FillEntry> entries, IList<LevelInfo> levels, IList<Polygon> sectors, bool overwrite)
	{
		List<FillEntry> missing = entries.Where(e => e.Status == FillStatus.SinDato).ToList();
		if (missing.Count == 0)
		{
			return;
		}

		List<FillEntry> known = entries.Where(e => e.NewValue != null && (e.Status == FillStatus.Directo || e.Status == FillStatus.Igual ||
			e.Status == FillStatus.Conservado)).ToList();
		var byType = known.Where(e => e.Element.TypeId >= 0).GroupBy(e => e.Element.TypeId)
			.ToDictionary(g => g.Key, g => g.GroupBy(e => e.NewValue).OrderByDescending(v => v.Count()).Select(v => (Value: v.Key, Count: v.Count(), Total: g.Count())).First());
		Dictionary<(int, int), List<FillEntry>> grid = BuildGrid(known);
		var valueTokens = known.Select(e => e.NewValue).Distinct().Select(v => (Value: v, Tokens: Text.Tokens(v))).Where(v => v.Tokens.Count > 0).ToList();
		var keywords = rule.Keywords.Where(k => k.Key.Length > 0).Select(k => (Key: Text.Simplify(k.Key), k.Value)).ToList();

		foreach (FillEntry entry in missing)
		{
			ElementInfo element = entry.Element;
			string value = null;
			string reason = null;

			if (rule.Source == SourceKind.Sector && sectors != null && sectors.Count > 0 && element.HasLocation)
			{
				(Polygon sector, double distance) = SectorIndex.Nearest(sectors, element.X, element.Y);
				if (sector != null)
				{
					value = rule.Transform(sector.Name);
					reason = $"Fuera de los sectores; el más cercano es \"{sector.Name}\" a {Meters(distance)}.";
				}
			}

			if (value == null && rule.Source == SourceKind.Nivel && levels.Count > 0 && element.HasLocation)
			{
				LevelInfo level = levels.Where(l => l.Elevation <= element.MinZ + 0.1).OrderByDescending(l => l.Elevation).FirstOrDefault() ??
					levels.OrderBy(l => l.Elevation).First();
				value = rule.Transform(level.Name);
				reason = $"Sin nivel asignado; por su altura ({Meters(element.MinZ)}) queda en {level.Name}.";
			}

			if (value == null && rule.InferSameType && element.TypeId >= 0 && byType.TryGetValue(element.TypeId, out var common))
			{
				value = common.Value;
				reason = $"Valor de su tipo: {common.Count} de {common.Total} elementos de {element.FamilyType} tienen \"{common.Value}\".";
			}

			if (value == null && rule.InferNearest && element.HasLocation)
			{
				(FillEntry near, double distance) = Nearest(grid, element);
				if (near != null)
				{
					value = near.NewValue;
					reason = $"Del elemento más cercano con dato ({near.Element.Id}, {near.Element.FamilyType}) a {Meters(distance)}.";
				}
			}

			if (value == null && rule.InferKeywords)
			{
				string names = Text.Simplify(element.Family + " " + element.TypeName + " " + element.Category);
				foreach (var keyword in keywords)
				{
					if (names.Contains(keyword.Key))
					{
						value = keyword.Value;
						reason = $"Palabra clave \"{keyword.Key}\" en el nombre.";
						break;
					}
				}

				if (value == null && valueTokens.Count > 0)
				{
					HashSet<string> tokens = Text.Tokens(element.Family + " " + element.TypeName);
					var best = valueTokens.Select(v => (v.Value, Score: Text.Similarity(tokens, v.Tokens))).OrderByDescending(v => v.Score).First();
					if (best.Score >= 0.5)
					{
						value = best.Value;
						reason = $"Su nombre se parece al valor \"{best.Value}\" ({best.Score:P0}).";
					}
				}
			}

			if (value == null && rule.Default.Length > 0)
			{
				value = rule.Default;
				reason = "Valor por defecto de la regla.";
			}

			if (value == null)
			{
				continue;
			}

			entry.NewValue = value;
			entry.Reason = reason;
			entry.Status = value == entry.Current ? FillStatus.Igual : FillStatus.Inferido;
			entry.Accepted = false;
		}
	}

	private static Dictionary<(int, int), List<FillEntry>> BuildGrid(List<FillEntry> known)
	{
		var grid = new Dictionary<(int, int), List<FillEntry>>();
		foreach (FillEntry entry in known.Where(e => e.Element.HasLocation))
		{
			var key = ((int)Math.Floor(entry.Element.X / Cell), (int)Math.Floor(entry.Element.Y / Cell));
			if (!grid.TryGetValue(key, out List<FillEntry> list))
			{
				grid[key] = list = new List<FillEntry>();
			}

			list.Add(entry);
		}

		return grid;
	}

	/// <summary>El elemento con dato más cercano (en planta; los del mismo nivel cuentan como más cerca).</summary>
	private static (FillEntry Entry, double Distance) Nearest(Dictionary<(int, int), List<FillEntry>> grid, ElementInfo element)
	{
		int cx = (int)Math.Floor(element.X / Cell);
		int cy = (int)Math.Floor(element.Y / Cell);
		int rings = (int)Math.Ceiling(NearestLimit / Cell);
		FillEntry best = null;
		double bestScore = double.MaxValue;
		double bestDistance = 0;
		for (int dx = -rings; dx <= rings; dx++)
		{
			for (int dy = -rings; dy <= rings; dy++)
			{
				if (!grid.TryGetValue((cx + dx, cy + dy), out List<FillEntry> list))
				{
					continue;
				}

				foreach (FillEntry other in list)
				{
					if (other.Element.Id == element.Id)
					{
						continue;
					}

					double distance = Math.Sqrt(Math.Pow(other.Element.X - element.X, 2) + Math.Pow(other.Element.Y - element.Y, 2));
					double score = distance + (other.Element.Level == element.Level ? 0 : NearestLimit);
					if (distance <= NearestLimit && score < bestScore)
					{
						best = other;
						bestScore = score;
						bestDistance = distance;
					}
				}
			}
		}

		return (best, bestDistance);
	}

	private static string Meters(double feet) => (feet * 0.3048).ToString("0.0", CultureInfo.InvariantCulture) + " m";
}
