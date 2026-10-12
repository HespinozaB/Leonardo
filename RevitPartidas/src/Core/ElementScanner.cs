using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitPartidas.Core;

/// <summary>Estado de un elemento frente al presupuesto (de más grave a menos grave).</summary>
public enum RowStatus
{
	/// <summary>Tiene códigos distintos que no se pueden resolver solos.</summary>
	Conflicto,

	/// <summary>El código no existe en el presupuesto.</summary>
	NoEnPresupuesto,

	/// <summary>El código es un título (no se metra), no una partida final.</summary>
	Titulo,

	/// <summary>Ya tiene DYCUSA, pero los parámetros de origen dicen otra partida.</summary>
	Difiere,

	/// <summary>Falta escribir (o completar) los DYCUSA con la partida propuesta.</summary>
	Pendiente,

	/// <summary>Ningún parámetro tiene un código de partida (en una categoría donde otros elementos sí lo tienen).</summary>
	SinPartida,

	/// <summary>DYCUSA 1–6 completos y la partida existe en el presupuesto.</summary>
	Ok
}

/// <summary>Un elemento del modelo con sus códigos de origen, sus DYCUSA actuales y la partida propuesta.</summary>
public sealed class ElementRow
{
	public long Id;

	public string Category;

	public string FamilyType;

	public string Level;

	/// <summary>Código más profundo leído de los parámetros de origen (null si no hay).</summary>
	public string SourceCode;

	/// <summary>Dónde se leyó (p. ej. "DOC_N° PARTIDA 1 (tipo)").</summary>
	public string SourceParam;

	/// <summary>Valores actuales de DYCUSA_PARTIDA 1…6 ("" si vacío; null si el elemento no tiene el parámetro).</summary>
	public string[] Current = new string[PartidaCode.MaxLevels];

	/// <summary>Código más profundo escrito hoy en los DYCUSA.</summary>
	public string DycusaCode;

	/// <summary>Los DYCUSA escritos no forman una cadena (p. ej. PARTIDA 2 = 03.02 y PARTIDA 5 = 03.01.…).</summary>
	public bool DycusaInconsistent;

	/// <summary>Los parámetros de origen tienen varias partidas finales distintas.</summary>
	public bool SourceConflict;

	public string ProposedCode;

	public string[] Proposed = PartidaCode.Levels(null);

	public RowStatus Status;

	public string Note;

	public bool HasDycusaParameters;

	/// <summary>Su categoría tiene elementos con partida (los "sin partida" de otras categorías no se muestran salvo en la selección).</summary>
	public bool InCodedCategory = true;

	public double Area;

	public double Length;

	public double Volume;

	public bool IsUpToDate => HasDycusaParameters && Current.SequenceEqual(Proposed);
}

public sealed class ScanResult
{
	public readonly List<ElementRow> Rows = new List<ElementRow>();

	public int DycusaBound;

	public readonly List<string> Notes = new List<string>();
}

/// <summary>
/// Recorre los elementos de modelo y busca códigos de partida en los parámetros de origen: primero los de ejemplar, luego los
/// de tipo y, si no hay ninguno, los de sus materiales. Con el presupuesto, propone los DYCUSA_PARTIDA 1…6.
/// </summary>
public static class ElementScanner
{
	/// <summary>Palabras que identifican un parámetro de origen (en el nombre, sin tildes ni mayúsculas).</summary>
	public const string DefaultKeywords = "partida, item";

	/// <summary>Nombres que contienen estas palabras no son de origen (descripciones, unidades, códigos OE…).</summary>
	private static readonly string[] Excluded = { "dycusa", "nom", "unidad", "alterno", "descrip" };

	public static ScanResult Run(Document doc, Budget budget, IList<string> keywords, bool preferSource)
	{
		var result = new ScanResult { DycusaBound = SharedParams.CountBound(doc) };
		var typeCache = new Dictionary<long, List<(string Code, string Name)>>();
		var materialCache = new Dictionary<long, List<(string Code, string Name)>>();
		var nameCache = new Dictionary<string, bool>(StringComparer.Ordinal);
		string[] words = keywords.Select(BudgetReader.Simplify).Where(k => k.Length > 0).ToArray();

		bool IsSource(string name)
		{
			if (!nameCache.TryGetValue(name, out bool isSource))
			{
				string simple = BudgetReader.Simplify(name);
				isSource = words.Any(w => simple.Contains(w)) && !Excluded.Any(x => simple.Contains(x));
				nameCache[name] = isSource;
			}

			return isSource;
		}

		List<(string Code, string Name)> Codes(Element element, string where)
		{
			var codes = new List<(string, string)>();
			foreach (Parameter parameter in element.Parameters)
			{
				if (parameter.StorageType != StorageType.String || !parameter.HasValue || parameter.Definition == null ||
					!IsSource(parameter.Definition.Name))
				{
					continue;
				}

				// Un solo nivel ("62", "03") no identifica una partida: suele ser otro dato en un parámetro "ITEM".
				string code = PartidaCode.Normalize(parameter.AsString());
				if (code != null && PartidaCode.Depth(code) >= 2)
				{
					codes.Add((code, parameter.Definition.Name + where));
				}
			}

			return codes;
		}

		var categoriesWithCodes = new HashSet<string>(StringComparer.Ordinal);
		var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
		foreach (Element element in collector)
		{
			Category category = element.Category;
			if (category == null || category.CategoryType != CategoryType.Model || !category.AllowsBoundParameters ||
				element.ViewSpecific || element is ElementType)
			{
				continue;
			}

			var row = new ElementRow
			{
				Id = element.Id.Value,
				Category = category.Name,
				FamilyType = FamilyTypeName(doc, element),
				Level = LevelName(doc, element)
			};

			Parameter[] dycusa = SharedParams.Get(element);
			row.HasDycusaParameters = dycusa.All(p => p != null);
			for (int i = 0; i < dycusa.Length; i++)
			{
				row.Current[i] = dycusa[i] == null ? null : (dycusa[i].AsString() ?? string.Empty).Trim();
			}

			row.DycusaCode = PartidaCode.Deepest(row.Current.Select(v => v ?? string.Empty).ToList(), out bool consistent);
			row.DycusaInconsistent = !consistent;

			// Orígenes: ejemplar → tipo → materiales (solo si los anteriores no tienen nada).
			List<(string Code, string Name)> codes = Codes(element, string.Empty);
			ElementId typeId = element.GetTypeId();
			if (typeId != ElementId.InvalidElementId)
			{
				if (!typeCache.TryGetValue(typeId.Value, out List<(string, string)> typeCodes))
				{
					Element type = doc.GetElement(typeId);
					typeCodes = type == null ? new List<(string, string)>() : Codes(type, " (tipo)");
					typeCache[typeId.Value] = typeCodes;
				}

				codes.AddRange(typeCodes);
			}

			if (codes.Count == 0)
			{
				foreach (ElementId materialId in SafeMaterials(element))
				{
					if (!materialCache.TryGetValue(materialId.Value, out List<(string, string)> materialCodes))
					{
						Element material = doc.GetElement(materialId);
						materialCodes = material == null ? new List<(string, string)>() : Codes(material, " (material " + material.Name + ")");
						materialCache[materialId.Value] = materialCodes;
					}

					codes.AddRange(materialCodes);
				}
			}

			ResolveSource(row, codes, budget);
			Evaluate(row, budget, preferSource);
			if (row.Status != RowStatus.SinPartida)
			{
				categoriesWithCodes.Add(row.Category);
			}

			ReadQuantities(element, row);
			result.Rows.Add(row);
		}

		// "Sin partida" solo interesa en las categorías que sí se están codificando (no en todo el modelo).
		foreach (ElementRow row in result.Rows)
		{
			row.InCodedCategory = categoriesWithCodes.Contains(row.Category);
		}
		return result;
	}

	/// <summary>Vuelve a calcular la propuesta y el estado (al cambiar la preferencia o tras asignar).</summary>
	public static void Evaluate(ElementRow row, Budget budget, bool preferSource)
	{
		row.ProposedCode = preferSource ? row.SourceCode ?? row.DycusaCode : row.DycusaCode ?? row.SourceCode;
		row.Proposed = PartidaCode.Levels(row.ProposedCode);
		if (row.ProposedCode == null)
		{
			row.Status = row.SourceConflict ? RowStatus.Conflicto : RowStatus.SinPartida;
			return;
		}

		BudgetItem item = budget?.Find(row.ProposedCode);
		if (budget != null && item == null)
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
			row.Status = row.IsUpToDate ? RowStatus.Ok : RowStatus.Pendiente;
		}

		if (row.DycusaInconsistent && row.Status == RowStatus.Ok)
		{
			row.Status = RowStatus.Pendiente;
		}
	}

	/// <summary>
	/// Elige el código de origen: el más profundo de cada cadena. Si hay códigos de ramas distintas, se queda con el único que es
	/// partida final del presupuesto; si hay varios, es un conflicto que debe resolver el usuario.
	/// </summary>
	private static void ResolveSource(ElementRow row, List<(string Code, string Name)> codes, Budget budget)
	{
		if (codes.Count == 0)
		{
			return;
		}

		// Se descartan los códigos que son ancestros de otro (03.01.04 frente a 03.01.04.01.01).
		var deepest = codes.Where(c => !codes.Any(o => o.Code != c.Code && PartidaCode.IsAncestorOrSelf(c.Code, o.Code)))
			.GroupBy(c => c.Code)
			.Select(g => g.First())
			.ToList();
		if (deepest.Count > 1 && budget != null)
		{
			var leaves = deepest.Where(c => budget.Find(c.Code)?.IsLeaf == true).ToList();
			if (leaves.Count >= 1)
			{
				if (leaves.Count < deepest.Count)
				{
					row.Note = "Otros códigos ignorados: " + string.Join(", ", deepest.Except(leaves).Select(c => $"{c.Code} ({c.Name})"));
				}

				deepest = leaves;
			}
		}

		if (deepest.Count > 1)
		{
			row.SourceConflict = true;
			row.Note = "Varias partidas: " + string.Join(", ", deepest.Select(c => $"{c.Code} ({c.Name})"));
			row.SourceParam = string.Join(" / ", deepest.Select(c => c.Name));
			return;
		}

		row.SourceCode = deepest[0].Code;
		row.SourceParam = deepest[0].Name;
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

	private static string FamilyTypeName(Document doc, Element element)
	{
		Element type = doc.GetElement(element.GetTypeId());
		if (type is FamilySymbol symbol)
		{
			return symbol.FamilyName + " : " + symbol.Name;
		}

		return type is ElementType elementType ? elementType.FamilyName + " : " + elementType.Name : element.Name;
	}

	private static string LevelName(Document doc, Element element)
	{
		ElementId levelId = element.LevelId;
		if (levelId == null || levelId == ElementId.InvalidElementId)
		{
			Parameter parameter = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM) ??
				element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
				element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
			levelId = parameter?.AsElementId();
		}

		return levelId != null && levelId != ElementId.InvalidElementId ? doc.GetElement(levelId)?.Name ?? string.Empty : string.Empty;
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
