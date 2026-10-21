using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitParamFill.Core;

/// <summary>Resultado del cálculo: todos los valores propuestos.</summary>
public sealed class FillPlan
{
	public readonly List<FillEntry> Entries = new List<FillEntry>();

	public readonly Dictionary<long, ElementInfo> Elements = new Dictionary<long, ElementInfo>();

	public readonly List<string> Notes = new List<string>();

	public TimeSpan Elapsed;
}

/// <summary>
/// Aplica las reglas a los elementos del modelo: lee el valor directo de cada origen, lo compara con el actual y, si no hay dato,
/// lo deduce (<see cref="Inference"/>). Escribe en una transacción los valores aceptados.
/// </summary>
public static class Filler
{
	public static FillPlan Compute(Document doc, RuleSet set)
	{
		DateTime start = DateTime.Now;
		var plan = new FillPlan();
		var reader = new ModelReader(doc);
		List<LevelInfo> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
			.Select(l => new LevelInfo { Name = l.Name, Elevation = l.Elevation }).ToList();

		// Sectores: se leen los conjuntos que usan las reglas (si aún no se leyeron).
		var sectorSets = new Dictionary<string, SectorSet>(StringComparer.OrdinalIgnoreCase);
		foreach (FillRule rule in set.Rules.Where(r => r.IsAutomatic && r.Source == SourceKind.Sector))
		{
			SectorSet sectors = set.SectorSets.FirstOrDefault(s => string.Equals(s.Name, rule.Detail, StringComparison.OrdinalIgnoreCase));
			if (sectors == null)
			{
				plan.Notes.Add($"{rule.Target}: no existe el conjunto de sectores \"{rule.Detail}\".");
				continue;
			}

			if (sectors.Sectors.Count == 0)
			{
				plan.Notes.Add(SectorReader.Read(doc, sectors));
			}

			sectorSets[sectors.Name] = sectors;
		}

		// Las reglas "Libre" también se listan (para ver su valor actual y llenarlas a mano), sin leer origen ni deducir.
		List<(FillRule Rule, int Index)> rules = set.Rules.Select((r, i) => (r, i))
			.Where(r => r.r.IsAutomatic || (r.r.Enabled && r.r.Source == SourceKind.Libre && r.r.Target.Length > 0)).ToList();
		var perRule = rules.ToDictionary(r => r.Index, r => new List<FillEntry>());
		var typeSeen = new HashSet<(int, long)>();
		foreach (Element element in ModelReader.ModelElements(doc))
		{
			ElementInfo info = null;
			foreach ((FillRule rule, int index) in rules)
			{
				Parameter target = reader.Target(element, rule.Target, out bool onType);
				if (target == null)
				{
					continue;
				}

				info ??= reader.Info(element);

				// Un parámetro de tipo se escribe una sola vez por tipo.
				if (onType && !typeSeen.Add((index, info.TypeId)))
				{
					continue;
				}

				var entry = new FillEntry
				{
					Element = info,
					RuleIndex = index,
					Target = rule.Target,
					OnType = onType,
					Current = reader.Value(target) ?? string.Empty
				};

				if (target.IsReadOnly || (target.StorageType != StorageType.String && target.StorageType != StorageType.Integer &&
						target.StorageType != StorageType.Double))
				{
					entry.Status = FillStatus.Bloqueado;
					entry.Reason = target.IsReadOnly ? "Parámetro de solo lectura." : "Tipo de dato no admitido.";
					perRule[index].Add(entry);
					continue;
				}

				if (rule.Source == SourceKind.Libre)
				{
					entry.NewValue = entry.Current.Length > 0 ? entry.Current : null;
					entry.Status = entry.Current.Length > 0 ? FillStatus.Conservado : FillStatus.SinDato;
					entry.Reason = "Libre: se llena en la pestaña Libre (masivo o personalizado).";
					perRule[index].Add(entry);
					continue;
				}

				string sectorReason = null;
				string value = rule.Source == SourceKind.Sector ? SectorValue(rule, sectorSets, info, doc, out sectorReason) : reader.Read(element, rule);
				value = rule.Transform(value);
				if (value != null)
				{
					entry.NewValue = value;
					if (value == entry.Current)
					{
						entry.Status = FillStatus.Igual;
					}
					else if (entry.Current.Length > 0 && !rule.Overwrite)
					{
						entry.Status = FillStatus.Conservado;
						entry.Reason = $"Ya tiene \"{entry.Current}\" (la regla no sobrescribe).";
					}
					else
					{
						entry.Status = FillStatus.Directo;
						entry.Accepted = true;
						entry.Reason = sectorReason ?? FillRule.SourceText(rule.Source) + (rule.Source == SourceKind.Parametro ? " " + rule.Detail : string.Empty);
					}
				}
				else if (entry.Current.Length > 0 && !rule.Overwrite)
				{
					entry.NewValue = entry.Current;
					entry.Status = FillStatus.Conservado;
					entry.Reason = "Sin dato de origen; se conserva el valor actual.";
				}
				else
				{
					entry.Status = FillStatus.SinDato;
					entry.Reason = "Sin dato de origen.";
				}

				perRule[index].Add(entry);
			}

			if (info != null)
			{
				plan.Elements[info.Id] = info;
			}
		}

		foreach ((FillRule rule, int index) in rules)
		{
			IList<Polygon> sectors = rule.Source == SourceKind.Sector && sectorSets.TryGetValue(rule.Detail, out SectorSet found) ? found.Sectors : null;
			if (rule.Source != SourceKind.Libre)
			{
				Inference.Run(rule, perRule[index], levels, sectors, rule.Overwrite);
			}

			plan.Entries.AddRange(perRule[index]);
			if (perRule[index].Count == 0)
			{
				plan.Notes.Add($"\"{rule.Target}\" no existe en ningún elemento (créalo o revisa el nombre).");
			}
		}

		plan.Elapsed = DateTime.Now - start;
		return plan;
	}

	/// <summary>El sector del elemento y el motivo (con el reparto si cruza varios sectores).</summary>
	private static string SectorValue(FillRule rule, Dictionary<string, SectorSet> sets, ElementInfo info, Document doc, out string reason)
	{
		reason = null;
		if (!info.HasLocation || !sets.TryGetValue(rule.Detail, out SectorSet set))
		{
			return null;
		}

		if (set.OnlyViewLevel && doc.GetElement(new ElementId(set.ViewId)) is View view && view.GenLevel != null && info.Level != view.GenLevel.Name)
		{
			return null;
		}

		if (set.CenterOnly)
		{
			return SectorIndex.Find(set.Sectors, info.X, info.Y)?.Name;
		}

		var (name, share, all) = SectorIndex.Best(set.Sectors, info);
		if (name != null && share < 0.995)
		{
			string others = string.Join(", ", all.Skip(1).Take(3).Select(a => $"{a.Name} {a.Share:P0}"));
			reason = $"Sector con mayor área: {name} ({share:P0}{(others.Length > 0 ? "; también " + others : string.Empty)}).";
		}

		return name;
	}

	/// <summary>Escribe los valores (elemento, parámetro, valor). Los de tipo se escriben una vez en el tipo. Dentro de una transacción.</summary>
	public static string Write(Document doc, IList<(long ElementId, string Target, string Value)> values)
	{
		DateTime start = DateTime.Now;
		var reader = new ModelReader(doc);
		var typesDone = new HashSet<(long, string)>();
		int written = 0;
		int same = 0;
		int failed = 0;
		int missing = 0;
		foreach (var (elementId, target, value) in values)
		{
			Element element = doc.GetElement(new ElementId(elementId));
			if (element == null)
			{
				failed++;
				continue;
			}

			Parameter parameter = reader.Target(element, target, out bool onType);
			if (parameter == null)
			{
				missing++;
				continue;
			}

			if (onType && !typesDone.Add((element.GetTypeId().Value, target)))
			{
				continue;
			}

			if ((reader.Value(parameter) ?? string.Empty) == (value ?? string.Empty))
			{
				same++;
				continue;
			}

			try
			{
				if (ModelReader.Set(parameter, value))
				{
					written++;
				}
				else
				{
					failed++;
				}
			}
			catch (Exception)
			{
				failed++;
			}
		}

		string text = $"{written} valor(es) escritos en {(DateTime.Now - start).TotalSeconds:0.0} s.";
		if (same > 0)
		{
			text += $" {same} ya tenían ese valor.";
		}

		if (missing > 0)
		{
			text += $" {missing} sin el parámetro.";
		}

		if (failed > 0)
		{
			text += $" {failed} no se pudieron escribir (solo lectura, grupos o formato del valor).";
		}

		return text;
	}
}
