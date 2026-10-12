using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitPartidas.Core;

/// <summary>Estado de un elemento frente al presupuesto (de más grave a menos grave).</summary>
public enum RowStatus
{
	/// <summary>Tiene varias partidas finales posibles: la decide el usuario.</summary>
	Conflicto,

	/// <summary>Su código no corresponde al nivel enlazado (p. ej. código de ETAPA 2 en un elemento de ETAPA 1).</summary>
	NoCoincide,

	/// <summary>El código no existe en el presupuesto.</summary>
	NoEnPresupuesto,

	/// <summary>El código es un título (no se metra), no una partida final.</summary>
	Titulo,

	/// <summary>Ya tiene los parámetros de nivel escritos, pero el origen dice otra partida.</summary>
	Difiere,

	/// <summary>Falta escribir (o completar) la partida propuesta.</summary>
	Pendiente,

	/// <summary>Sin partida, pero los enlaces dan los niveles superiores (p. ej. 03 / 03.01): se pueden escribir ya.</summary>
	Parcial,

	/// <summary>Ningún parámetro tiene un código de partida.</summary>
	SinPartida,

	/// <summary>Los niveles están escritos y la partida existe en el presupuesto.</summary>
	Ok
}

/// <summary>Un elemento del modelo con sus códigos de origen, los niveles actuales y la propuesta.</summary>
public sealed class ElementRow
{
	public long Id;

	public string Category;

	public string FamilyType;

	public string Level;

	/// <summary>Código más profundo leído de los parámetros de origen (null si no hay o hay conflicto).</summary>
	public string SourceCode;

	/// <summary>Dónde se leyó (p. ej. "DOC_N° PARTIDA 1 (tipo)").</summary>
	public string SourceParam;

	/// <summary>Las opciones cuando hay conflicto (o los códigos que no coinciden con el enlace).</summary>
	public List<string> Candidates = new List<string>();

	public bool SourceConflict;

	public bool LinkMismatch;

	/// <summary>Algún nivel salió de un parámetro de Revit del elemento (p. ej. su ETAPA), no solo de un valor fijo.</summary>
	public bool HasParameterLink;

	/// <summary>Código de cada nivel según los enlaces (fijo o parámetro de Revit); "" si el nivel se deriva del código.</summary>
	public string[] Linked;

	/// <summary>Valores actuales de los parámetros de nivel ("" si vacío; null si el elemento no tiene el parámetro).</summary>
	public string[] Current;

	/// <summary>Código más profundo escrito hoy en los parámetros de nivel.</summary>
	public string DycusaCode;

	public bool DycusaInconsistent;

	public string ProposedCode;

	public string[] Proposed;

	public RowStatus Status;

	public string Note;

	public bool HasDycusaParameters;

	/// <summary>Su categoría tiene elementos con partida (los "sin partida" de otras categorías no se muestran salvo en la selección).</summary>
	public bool InCodedCategory = true;

	public double Area;

	public double Length;

	public double Volume;

	public bool IsUpToDate => HasDycusaParameters && Current.Select(v => v ?? string.Empty).SequenceEqual(Proposed);

	/// <summary>Cadena de niveles enlazados contiguos desde el nivel 1 (p. ej. "03.01"), o null.</summary>
	public string LinkedChain
	{
		get
		{
			string chain = null;
			foreach (string code in Linked ?? Array.Empty<string>())
			{
				if (string.IsNullOrEmpty(code) || (chain != null && !PartidaCode.IsAncestorOrSelf(chain, code)))
				{
					break;
				}

				chain = code;
			}

			return chain;
		}
	}
}

public sealed class ScanResult
{
	public readonly List<ElementRow> Rows = new List<ElementRow>();

	public int ParametersBound;

	public readonly List<string> Notes = new List<string>();

	public TimeSpan Elapsed;
}

/// <summary>Sugerencia del paso 3: un parámetro cuyo valor coincide con las descripciones de un nivel.</summary>
public sealed class LinkSuggestion
{
	public int Level;

	public LinkMode Mode;

	public string ParameterName;

	public string FixedCode;

	public int Matches;

	public string Example;
}

/// <summary>
/// Recorre los elementos de modelo y busca códigos de partida en los parámetros de origen: primero los de ejemplar; si no
/// resuelven, los de tipo; y si tampoco, los de sus materiales. Con el presupuesto y los enlaces propone los niveles.
/// </summary>
public static class ElementScanner
{
	/// <summary>Nombres que contienen estas palabras no son de origen (descripciones, unidades, códigos OE…).</summary>
	private static readonly string[] Excluded = { "dycusa", "nom", "unidad", "alterno", "descrip" };

	private sealed class Shape
	{
		/// <summary>Parámetros de ejemplar de origen (de texto) que tienen los elementos de esta forma.</summary>
		public List<string> InstanceSources;

		public List<(string Code, string Name)> TypeCodes;

		public List<(string Code, string Name)> MaterialCodes;

		public string FamilyType;
	}

	public static ScanResult Run(Document doc, Budget budget, PartidasConfig config, ICollection<long> onlyIds = null)
	{
		DateTime start = DateTime.Now;
		string[] names = config.Names;
		Guid[] guids = names.Select(SharedParams.GuidFor).ToArray();
		var result = new ScanResult { ParametersBound = SharedParams.CountBound(doc, names) };
		string[] words = config.KeywordList.Select(BudgetReader.Simplify).Where(k => k.Length > 0).ToArray();
		var nameCache = new Dictionary<string, bool>(StringComparer.Ordinal);
		var shapes = new Dictionary<long, Shape>();
		var typeValues = new Dictionary<(long, string), string>();
		var levelNames = new Dictionary<long, string>();
		var linkMaps = BuildLinkMaps(budget, config);

		bool IsSource(string name)
		{
			if (!nameCache.TryGetValue(name, out bool isSource))
			{
				string simple = BudgetReader.Simplify(name);
				isSource = words.Any(w => simple.Contains(w)) && !Excluded.Any(x => simple.Contains(x)) && !names.Contains(name);
				nameCache[name] = isSource;
			}

			return isSource;
		}

		List<(string Code, string Name)> AllCodes(Element element, string where)
		{
			var codes = new List<(string, string)>();
			foreach (Parameter parameter in element.Parameters)
			{
				if (parameter.StorageType == StorageType.String && parameter.HasValue && parameter.Definition != null &&
					IsSource(parameter.Definition.Name))
				{
					AddCode(codes, parameter.AsString(), parameter.Definition.Name + where);
				}
			}

			return codes;
		}

		IEnumerable<Element> elements = onlyIds == null
			? new FilteredElementCollector(doc).WhereElementIsNotElementType()
			: onlyIds.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null);
		foreach (Element element in elements)
		{
			Category category = element.Category;
			if (category == null || category.CategoryType != CategoryType.Model || !category.AllowsBoundParameters ||
				element.ViewSpecific || element is ElementType)
			{
				continue;
			}

			// Los elementos del mismo tipo comparten los mismos parámetros: se analizan una vez por tipo.
			ElementId typeId = element.GetTypeId();
			long shapeKey = typeId != ElementId.InvalidElementId ? typeId.Value : -category.Id.Value;
			if (!shapes.TryGetValue(shapeKey, out Shape shape))
			{
				shape = new Shape
				{
					InstanceSources = element.Parameters.Cast<Parameter>()
						.Where(p => p.StorageType == StorageType.String && p.Definition != null && IsSource(p.Definition.Name))
						.Select(p => p.Definition.Name).Distinct().ToList()
				};
				Element type = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
				shape.TypeCodes = type == null ? new List<(string, string)>() : AllCodes(type, " (tipo)");
				shape.FamilyType = type is ElementType elementType ? elementType.FamilyName + " : " + elementType.Name : element.Name;
				shapes[shapeKey] = shape;
			}

			var row = new ElementRow
			{
				Id = element.Id.Value,
				Category = category.Name,
				FamilyType = typeId != ElementId.InvalidElementId ? shape.FamilyType : element.Name,
				Level = LevelName(doc, element, levelNames)
			};

			ReadCurrent(element, row, names, guids);

			var instanceCodes = new List<(string Code, string Name)>();
			foreach (string name in shape.InstanceSources)
			{
				Parameter parameter = element.LookupParameter(name);
				if (parameter != null && parameter.StorageType == StorageType.String && parameter.HasValue)
				{
					AddCode(instanceCodes, parameter.AsString(), name);
				}
			}

			row.Linked = ReadLinks(doc, element, typeId, config, linkMaps, typeValues, row);

			// Los materiales solo se consultan si el elemento y su tipo no tienen nada (es lo más lento).
			List<(string Code, string Name)> materialCodes = null;
			if (instanceCodes.Count == 0 && shape.TypeCodes.Count == 0)
			{
				if (shape.MaterialCodes == null)
				{
					shape.MaterialCodes = new List<(string, string)>();
					foreach (ElementId materialId in SafeMaterials(element))
					{
						Element material = doc.GetElement(materialId);
						if (material != null)
						{
							shape.MaterialCodes.AddRange(AllCodes(material, " (material " + material.Name + ")"));
						}
					}
				}

				materialCodes = shape.MaterialCodes;
			}

			Resolve(row, budget, instanceCodes, shape.TypeCodes, materialCodes);
			Evaluate(row, budget, config);
			ReadQuantities(element, row);
			result.Rows.Add(row);
		}

		// "Sin partida" solo interesa en las categorías que sí se están codificando (no en todo el modelo).
		var coded = new HashSet<string>(result.Rows.Where(r => r.Status != RowStatus.SinPartida && r.Status != RowStatus.Parcial).Select(r => r.Category));
		foreach (ElementRow row in result.Rows)
		{
			row.InCodedCategory = coded.Contains(row.Category);
		}

		result.Elapsed = DateTime.Now - start;
		return result;
	}

	private static void AddCode(List<(string Code, string Name)> codes, string value, string name)
	{
		// Un solo nivel ("62", "03") no identifica una partida: suele ser otro dato en un parámetro "ITEM".
		string code = PartidaCode.Normalize(value);
		if (code != null && PartidaCode.Depth(code) >= 2)
		{
			codes.Add((code, name));
		}
	}

	private static void ReadCurrent(Element element, ElementRow row, string[] names, Guid[] guids)
	{
		Parameter[] parameters = SharedParams.Get(element, names, guids);
		row.HasDycusaParameters = parameters.All(p => p != null);
		row.Current = parameters.Select(p => p == null ? null : (p.AsString() ?? string.Empty).Trim()).ToArray();
		row.DycusaCode = PartidaCode.Deepest(row.Current.Select(v => v ?? string.Empty).ToList(), out bool consistent);
		row.DycusaInconsistent = !consistent;
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
					if (map.TryGetValue(description, out string other) && other != item.Code)
					{
						map[description] = null;
					}
					else
					{
						map[description] = item.Code;
					}
				}
			}

			maps[level - 1] = map;
		}

		return maps;
	}

	private static string[] ReadLinks(Document doc, Element element, ElementId typeId, PartidasConfig config,
		Dictionary<string, string>[] maps, Dictionary<(long, string), string> typeValues, ElementRow row)
	{
		var linked = new string[config.Levels];
		for (int i = 0; i < config.Levels; i++)
		{
			linked[i] = string.Empty;
			LevelLink link = config.Link(i);
			if (link.Mode == LinkMode.Fijo)
			{
				linked[i] = PartidaCode.Normalize(link.FixedCode) ?? string.Empty;
			}
			else if (link.Mode == LinkMode.Parametro && !string.IsNullOrEmpty(link.ParameterName))
			{
				string value = Text(element.LookupParameter(link.ParameterName));
				if (value == null && typeId != ElementId.InvalidElementId)
				{
					if (!typeValues.TryGetValue((typeId.Value, link.ParameterName), out value))
					{
						value = Text(doc.GetElement(typeId)?.LookupParameter(link.ParameterName));
						typeValues[(typeId.Value, link.ParameterName)] = value;
					}
				}

				if (!string.IsNullOrWhiteSpace(value))
				{
					string key = PartidaCode.Normalize(value) ?? BudgetReader.Simplify(value);
					if (maps[i].TryGetValue(key, out string code) && code != null)
					{
						linked[i] = code;
						row.HasParameterLink = true;
					}
					else
					{
						AppendNote(row, $"{link.ParameterName} = \"{value.Trim()}\" no coincide con ningún nivel {i + 1} del presupuesto.");
					}
				}
			}
		}

		return linked;
	}

	private static string Text(Parameter parameter)
	{
		if (parameter == null || !parameter.HasValue)
		{
			return null;
		}

		return parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
	}

	/// <summary>
	/// Elige la partida de origen por niveles de prioridad (ejemplar → tipo → materiales). En cada nivel se quedan los códigos
	/// más profundos que coinciden con los enlaces; si queda uno, es la partida; si quedan varios, se prefieren las partidas
	/// finales del presupuesto; si aún son varios, es un conflicto.
	/// </summary>
	internal static void Resolve(ElementRow row, Budget budget, params List<(string Code, string Name)>[] tiers)
	{
		List<(string Code, string Name)> mismatched = null;
		foreach (List<(string Code, string Name)> tier in tiers)
		{
			if (tier == null || tier.Count == 0)
			{
				continue;
			}

			var deepest = tier.Where(c => !tier.Any(o => o.Code != c.Code && PartidaCode.IsAncestorOrSelf(c.Code, o.Code)))
				.GroupBy(c => c.Code).Select(g => g.First()).ToList();
			var matching = deepest.Where(c => MatchesLinks(c.Code, row.Linked)).ToList();
			if (matching.Count == 0)
			{
				mismatched ??= deepest;
				continue;
			}

			if (matching.Count > 1 && budget != null)
			{
				var leaves = matching.Where(c => budget.Find(c.Code)?.IsLeaf == true).ToList();
				if (leaves.Count >= 1)
				{
					matching = leaves;
				}
			}

			if (matching.Count > 1)
			{
				row.SourceConflict = true;
				row.Candidates = matching.Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
				row.SourceParam = string.Join(" / ", matching.Select(c => c.Name).Distinct());
				return;
			}

			row.SourceCode = matching[0].Code;
			row.SourceParam = matching[0].Name;
			if (mismatched != null)
			{
				AppendNote(row, "Ignorado por no coincidir con el enlace: " + string.Join(", ", mismatched.Select(c => $"{c.Code} ({c.Name})")));
			}

			return;
		}

		if (mismatched != null)
		{
			row.LinkMismatch = true;
			row.Candidates = mismatched.Select(c => c.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
			row.SourceParam = string.Join(" / ", mismatched.Select(c => c.Name).Distinct());
		}
	}

	private static bool MatchesLinks(string code, string[] linked)
	{
		foreach (string link in linked ?? Array.Empty<string>())
		{
			if (!string.IsNullOrEmpty(link) && !PartidaCode.IsAncestorOrSelf(link, code))
			{
				return false;
			}
		}

		return true;
	}

	private static void AppendNote(ElementRow row, string note) =>
		row.Note = string.IsNullOrEmpty(row.Note) ? note : row.Note + " " + note;

	/// <summary>Calcula la propuesta y el estado (al analizar, al cambiar la preferencia y tras escribir).</summary>
	public static void Evaluate(ElementRow row, Budget budget, PartidasConfig config)
	{
		int levels = config.Levels;
		row.ProposedCode = config.PreferSource ? row.SourceCode ?? row.DycusaCode : row.DycusaCode ?? row.SourceCode;
		if (row.ProposedCode == null)
		{
			row.Proposed = PartidaCode.Levels(row.HasParameterLink ? row.LinkedChain : null, levels);
			row.Status = row.SourceConflict ? RowStatus.Conflicto
				: row.LinkMismatch ? RowStatus.NoCoincide
				: row.HasParameterLink && row.LinkedChain != null && !row.IsUpToDate ? RowStatus.Parcial
				: RowStatus.SinPartida;
			return;
		}

		row.Proposed = PartidaCode.Levels(row.ProposedCode, levels);
		BudgetItem item = budget?.Find(row.ProposedCode);
		if (!MatchesLinks(row.ProposedCode, row.Linked))
		{
			row.Status = RowStatus.NoCoincide;
		}
		else if (budget != null && item == null)
		{
			row.Status = RowStatus.NoEnPresupuesto;
		}
		else if (item != null && !item.IsLeaf)
		{
			row.Status = RowStatus.Titulo;
		}
		else if (row.DycusaCode != null && row.SourceCode != null && row.DycusaCode != row.SourceCode)
		{
			row.Status = RowStatus.Difiere;
		}
		else
		{
			row.Status = row.IsUpToDate && !row.DycusaInconsistent ? RowStatus.Ok : RowStatus.Pendiente;
		}
	}

	/// <summary>Paso 3: busca qué parámetros del modelo tienen como valor las descripciones de cada nivel (p. ej. "ETAPA 1").</summary>
	public static List<LinkSuggestion> SuggestLinks(Document doc, Budget budget, PartidasConfig config)
	{
		var suggestions = new List<LinkSuggestion>();
		if (budget == null)
		{
			return suggestions;
		}

		// Nivel 1 con un solo título (p. ej. 03 ARQUITECTURA): vale para todo el modelo.
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

	private static IEnumerable<ElementId> SafeMaterials(Element element)
	{
		try
		{
			return element.GetMaterialIds(false);
		}
		catch (Exception)
		{
			return Enumerable.Empty<ElementId>();
		}
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
