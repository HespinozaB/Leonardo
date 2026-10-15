using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitPartidas.Core;

/// <summary>
/// Lee del modelo lo que necesita <see cref="Classifier"/>: el Código y la Descripción de montaje del tipo, los parámetros del
/// filtro (p. ej. la etapa) y, para los que no tienen código de montaje, todos los parámetros de texto de ejemplar y de tipo
/// (códigos del presupuesto y textos para comparar por nombre).
/// </summary>
public static class ElementScanner
{
	private sealed class TypeInfo
	{
		public string FamilyType;

		public string AssemblyCode = string.Empty;

		public string AssemblyDescription = string.Empty;

		public List<(string Code, string Source)> CodeHits;

		public List<(string Text, string Source)> Texts;
	}

	public static ScanResult Run(Document doc, Budget budget, PartidasConfig config)
	{
		DateTime start = DateTime.Now;
		string[] names = config.Names;
		Guid[] guids = names.Select(SharedParams.GuidFor).ToArray();
		var skip = new HashSet<string>(names, StringComparer.Ordinal);
		foreach (LevelLink link in config.Links.Where(l => l.Mode == LinkMode.Parametro && !string.IsNullOrEmpty(l.ParameterName)))
		{
			skip.Add(link.ParameterName);
		}

		var result = new ScanResult { ParametersBound = SharedParams.CountBound(doc, names) };
		var matcher = budget == null ? null : new BudgetMatcher(budget);
		var types = new Dictionary<long, TypeInfo>();
		var typeValues = new Dictionary<(long, string), string>();
		var levelNames = new Dictionary<long, string>();
		var linkMaps = BuildLinkMaps(budget, config);

		foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
		{
			Category category = element.Category;
			if (category == null || category.CategoryType != CategoryType.Model || !category.AllowsBoundParameters ||
				element.ViewSpecific || element is ElementType)
			{
				continue;
			}

			ElementId typeId = element.GetTypeId();
			TypeInfo type = null;
			if (typeId != ElementId.InvalidElementId && !types.TryGetValue(typeId.Value, out type))
			{
				Element typeElement = doc.GetElement(typeId);
				type = new TypeInfo
				{
					FamilyType = typeElement is ElementType elementType ? elementType.FamilyName + " : " + elementType.Name : element.Name,
					AssemblyCode = AssemblyCodes.Code(typeElement),
					AssemblyDescription = AssemblyCodes.Description(typeElement)
				};
				types[typeId.Value] = type;
			}

			var row = new ElementRow
			{
				Id = element.Id.Value,
				TypeId = typeId != ElementId.InvalidElementId ? typeId.Value : -1,
				Category = category.Name,
				FamilyType = type?.FamilyType ?? element.Name,
				Level = LevelName(doc, element, levelNames),
				AssemblyCode = type?.AssemblyCode ?? string.Empty,
				AssemblyDescription = type?.AssemblyDescription ?? string.Empty
			};

			ReadCurrent(element, row, names, guids);
			ReadLinks(doc, element, typeId, config, linkMaps, typeValues, row);

			// Sin un código de montaje válido para su rama, se leen todos sus parámetros (lo más lento, solo cuando hace falta).
			if (!MontajeResolves(row, budget, matcher))
			{
				if (type != null && type.CodeHits == null)
				{
					Element typeElement = doc.GetElement(typeId);
					type.CodeHits = new List<(string, string)>();
					type.Texts = new List<(string, string)>();
					if (typeElement is ElementType elementType)
					{
						AddText(type.Texts, elementType.FamilyName, "Familia");
						AddText(type.Texts, elementType.Name, "Tipo");
						AddText(type.Texts, elementType.FamilyName + " " + elementType.Name, "Familia : Tipo");
					}

					ReadParameters(typeElement, " (tipo)", skip, budget, type.CodeHits, type.Texts);
				}

				ReadParameters(element, string.Empty, skip, budget, row.CodeHits, row.Texts);
				if (type != null)
				{
					row.CodeHits.AddRange(type.CodeHits);
					row.Texts.AddRange(type.Texts);
				}
				else
				{
					AddText(row.Texts, element.Name, "Nombre");
				}
			}

			Classifier.Classify(row, budget, matcher, config);
			ReadQuantities(element, row);
			result.Rows.Add(row);
		}

		MarkCodedCategories(result.Rows);
		result.Elapsed = DateTime.Now - start;
		return result;
	}

	/// <summary>"Sin partida" solo interesa en las categorías que sí se están codificando (no en todo el modelo).</summary>
	public static void MarkCodedCategories(List<ElementRow> rows)
	{
		var coded = new HashSet<string>(rows.Where(r => r.Status != RowStatus.SinPartida && r.Status != RowStatus.Parcial).Select(r => r.Category));
		foreach (ElementRow row in rows)
		{
			row.InCodedCategory = coded.Contains(row.Category);
		}
	}

	private static bool MontajeResolves(ElementRow row, Budget budget, BudgetMatcher matcher)
	{
		string code = PartidaCode.Normalize(row.AssemblyCode);
		BudgetItem item = code == null ? null : budget?.Find(code);
		if (code == null || (budget != null && (item == null || !item.IsLeaf)))
		{
			return false;
		}

		return Classifier.MatchesLinks(code, row.Linked) ||
			matcher?.Equivalent(code, row.LinkedChain ?? row.Linked?.LastOrDefault(l => !string.IsNullOrEmpty(l))) != null;
	}

	/// <summary>Todos los parámetros de texto: los valores que son códigos del presupuesto y los textos con letras.</summary>
	private static void ReadParameters(Element element, string where, HashSet<string> skip, Budget budget,
		List<(string Code, string Source)> codes, List<(string Text, string Source)> texts)
	{
		if (element == null)
		{
			return;
		}

		foreach (Parameter parameter in element.Parameters)
		{
			if (parameter.StorageType != StorageType.String || !parameter.HasValue || parameter.Definition == null ||
				skip.Contains(parameter.Definition.Name))
			{
				continue;
			}

			if (parameter.Definition is InternalDefinition internalDefinition &&
				(internalDefinition.BuiltInParameter == BuiltInParameter.UNIFORMAT_CODE || internalDefinition.BuiltInParameter == BuiltInParameter.UNIFORMAT_DESCRIPTION))
			{
				continue;
			}

			string value = parameter.AsString();
			if (string.IsNullOrWhiteSpace(value))
			{
				continue;
			}

			string source = parameter.Definition.Name + where;
			string code = PartidaCode.Normalize(value);
			if (code != null)
			{
				// Códigos de al menos 3 niveles que existen en el presupuesto (un "03.01" o un "62" sueltos no dicen la partida).
				if (PartidaCode.Depth(code) >= 3 && (budget == null || budget.Find(code) != null))
				{
					codes.Add((code, source));
				}
			}
			else
			{
				AddText(texts, value, source);
			}
		}
	}

	private static void AddText(List<(string Text, string Source)> texts, string value, string source)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length < 4 || value.Length > 250 || !value.Any(char.IsLetter))
		{
			return;
		}

		texts.Add((value.Trim(), source));
	}

	private static void ReadCurrent(Element element, ElementRow row, string[] names, Guid[] guids)
	{
		Parameter[] parameters = SharedParams.Get(element, names, guids);
		row.HasDycusaParameters = parameters.All(p => p != null);
		row.Current = parameters.Select(p => p == null ? null : (p.AsString() ?? string.Empty).Trim()).ToArray();
	}

	/// <summary>Por nivel: descripción simplificada (y código) → código del presupuesto.</summary>
	private static Dictionary<string, string>[] BuildLinkMaps(Budget budget, PartidasConfig config)
	{
		var maps = new Dictionary<string, string>[config.Levels];
		for (int level = 1; level <= config.Levels; level++)
		{
			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			if (budget != null)
			{
				foreach (BudgetItem item in budget.All.Where(i => i.Depth == level))
				{
					map[item.Code] = item.Code;
					string description = BudgetReader.Simplify(item.Description);
					// Si dos partidas del nivel tienen la misma descripción, no sirve para enlazar.
					map[description] = map.TryGetValue(description, out string other) && other != item.Code ? null : item.Code;
				}
			}

			maps[level - 1] = map;
		}

		return maps;
	}

	/// <summary>El código del nivel para un valor del parámetro del filtro: primero la regla del usuario, luego la descripción.</summary>
	public static string MapValue(LevelLink link, Dictionary<string, string> map, string value, out bool ruled)
	{
		ruled = false;
		string key = BudgetReader.Simplify(value);
		if (link.ValueMap.TryGetValue(key, out string chosen))
		{
			ruled = true;
			return PartidaCode.Normalize(chosen) ?? string.Empty;
		}

		string lookup = PartidaCode.Normalize(value) ?? key;
		return map != null && map.TryGetValue(lookup, out string code) && code != null ? code : null;
	}

	private static void ReadLinks(Document doc, Element element, ElementId typeId, PartidasConfig config,
		Dictionary<string, string>[] maps, Dictionary<(long, string), string> typeValues, ElementRow row)
	{
		row.Linked = new string[config.Levels];
		row.LinkValues = new string[config.Levels];
		for (int i = 0; i < config.Levels; i++)
		{
			row.Linked[i] = string.Empty;
			row.LinkValues[i] = string.Empty;
			LevelLink link = config.Link(i);
			if (link.Mode == LinkMode.Fijo)
			{
				row.Linked[i] = PartidaCode.Normalize(link.FixedCode) ?? string.Empty;
				continue;
			}

			if (link.Mode != LinkMode.Parametro || string.IsNullOrEmpty(link.ParameterName))
			{
				continue;
			}

			string value = LinkValue(doc, element, typeId, link.ParameterName, typeValues);
			if (string.IsNullOrWhiteSpace(value))
			{
				continue;
			}

			row.LinkValues[i] = value.Trim();
			string code = MapValue(link, maps[i], value, out bool ruled);
			if (code == null)
			{
				row.AppendNote($"{link.ParameterName} = \"{value.Trim()}\" no tiene regla en el filtro del nivel {i + 1}.");
			}
			else if (code.Length > 0)
			{
				row.Linked[i] = code;
				row.HasParameterLink = true;
			}
		}
	}

	private static string LinkValue(Document doc, Element element, ElementId typeId, string name, Dictionary<(long, string), string> typeValues)
	{
		string value = Text(element.LookupParameter(name));
		if (value == null && typeId != ElementId.InvalidElementId)
		{
			if (!typeValues.TryGetValue((typeId.Value, name), out value))
			{
				value = Text(doc.GetElement(typeId)?.LookupParameter(name));
				typeValues[(typeId.Value, name)] = value;
			}
		}

		return value;
	}

	private static string Text(Parameter parameter)
	{
		if (parameter == null || !parameter.HasValue)
		{
			return null;
		}

		return parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
	}

	/// <summary>Paso 2: los valores distintos que tiene cada parámetro del filtro en el modelo, con cuántos elementos.</summary>
	public static Dictionary<int, Dictionary<string, int>> LinkValueCounts(Document doc, PartidasConfig config)
	{
		var counts = new Dictionary<int, Dictionary<string, int>>();
		var typeValues = new Dictionary<(long, string), string>();
		var links = Enumerable.Range(0, config.Levels)
			.Where(i => config.Link(i).Mode == LinkMode.Parametro && !string.IsNullOrEmpty(config.Link(i).ParameterName)).ToList();
		foreach (int i in links)
		{
			counts[i + 1] = new Dictionary<string, int>(StringComparer.Ordinal);
		}

		if (links.Count == 0)
		{
			return counts;
		}

		foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
		{
			Category category = element.Category;
			if (category == null || category.CategoryType != CategoryType.Model || element.ViewSpecific)
			{
				continue;
			}

			foreach (int i in links)
			{
				string value = LinkValue(doc, element, element.GetTypeId(), config.Link(i).ParameterName, typeValues);
				if (!string.IsNullOrWhiteSpace(value))
				{
					value = value.Trim();
					Dictionary<string, int> levelCounts = counts[i + 1];
					levelCounts[value] = levelCounts.TryGetValue(value, out int n) ? n + 1 : 1;
				}
			}
		}

		return counts;
	}

	/// <summary>Paso 2: propone el nivel 1 fijo y busca parámetros cuyos valores coinciden con las descripciones de los niveles 2 y 3.</summary>
	public static List<LinkSuggestion> SuggestLinks(Document doc, Budget budget, PartidasConfig config)
	{
		var suggestions = new List<LinkSuggestion>();
		if (budget == null)
		{
			return suggestions;
		}

		List<BudgetItem> first = budget.All.Where(i => i.Depth == 1).ToList();
		if (first.Count == 1)
		{
			suggestions.Add(new LinkSuggestion { Level = 1, Mode = LinkMode.Fijo, FixedCode = first[0].Code, Example = first[0].Description });
		}

		var maps = BuildLinkMaps(budget, config);
		var counts = new Dictionary<(int Level, string Name), int>();
		var examples = new Dictionary<(int, string), string>();
		var seenTypes = new HashSet<long>();
		int sampled = 0;
		foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
		{
			Category category = element.Category;
			if (category == null || category.CategoryType != CategoryType.Model || element.ViewSpecific)
			{
				continue;
			}

			if (++sampled > 30000)
			{
				break;
			}

			var owners = new List<Element> { element };
			ElementId typeId = element.GetTypeId();
			if (typeId != ElementId.InvalidElementId && seenTypes.Add(typeId.Value) && doc.GetElement(typeId) is Element type)
			{
				owners.Add(type);
			}

			foreach (Element owner in owners)
			{
				foreach (Parameter parameter in owner.Parameters)
				{
					if (parameter.StorageType != StorageType.String || !parameter.HasValue || parameter.Definition == null)
					{
						continue;
					}

					string value = parameter.AsString();
					if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
					{
						continue;
					}

					string key = BudgetReader.Simplify(value);
					for (int level = 2; level <= Math.Min(3, config.Levels); level++)
					{
						if (maps[level - 1].TryGetValue(key, out string code) && code != null)
						{
							var k = (level, parameter.Definition.Name);
							counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
							examples[k] = $"\"{value.Trim()}\" → {code}";
						}
					}
				}
			}
		}

		foreach (var best in counts.GroupBy(c => c.Key.Level).Select(g => g.OrderByDescending(c => c.Value).First()))
		{
			suggestions.Add(new LinkSuggestion
			{
				Level = best.Key.Level,
				Mode = LinkMode.Parametro,
				ParameterName = best.Key.Name,
				Matches = best.Value,
				Example = examples[best.Key]
			});
		}

		return suggestions.OrderBy(s => s.Level).ToList();
	}

	private static string LevelName(Document doc, Element element, Dictionary<long, string> cache)
	{
		ElementId levelId = element.LevelId;
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			Parameter parameter = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM) ??
				element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
				element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
			levelId = parameter?.AsElementId();
		}

		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			return string.Empty;
		}

		if (!cache.TryGetValue(levelId.Value, out string name))
		{
			name = doc.GetElement(levelId)?.Name ?? string.Empty;
			cache[levelId.Value] = name;
		}

		return name;
	}

	private static void ReadQuantities(Element element, ElementRow row)
	{
		row.Area = Value(element, BuiltInParameter.HOST_AREA_COMPUTED, UnitTypeId.SquareMeters);
		row.Volume = Value(element, BuiltInParameter.HOST_VOLUME_COMPUTED, UnitTypeId.CubicMeters);
		row.Length = Value(element, BuiltInParameter.CURVE_ELEM_LENGTH, UnitTypeId.Meters);
		if (row.Length == 0)
		{
			row.Length = Value(element, BuiltInParameter.INSTANCE_LENGTH_PARAM, UnitTypeId.Meters);
		}
	}

	private static double Value(Element element, BuiltInParameter id, ForgeTypeId unit)
	{
		Parameter parameter = element.get_Parameter(id);
		return parameter != null && parameter.StorageType == StorageType.Double && parameter.HasValue
			? UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), unit)
			: 0;
	}
}
