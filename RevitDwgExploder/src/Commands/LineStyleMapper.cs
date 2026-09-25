using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Traduce el estilo gráfico de cada capa del DWG a un Line Style válido para Detail Lines.
/// Las capas de un CAD importado son subcategorías de la categoría del import, y Revit no permite
/// asignarlas directamente a una Detail Line; por eso se crea (una sola vez) un Line Style
/// "DWG-&lt;capa&gt;" con el mismo color, grosor y patrón que la capa.
/// </summary>
internal sealed class LineStyleMapper
{
	private const string Prefix = "DWG-";

	private static readonly char[] InvalidNameChars = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' };

	private readonly Document _doc;

	private readonly Category _linesCategory;

	private readonly Dictionary<ElementId, GraphicsStyle> _cache = new Dictionary<ElementId, GraphicsStyle>();

	private readonly Dictionary<string, Category> _lineSubcategoriesByName;

	private readonly Dictionary<(ElementId, string), GraphicsStyle> _patternCache = new Dictionary<(ElementId, string), GraphicsStyle>();

	private readonly Dictionary<string, ElementId> _linePatternIds = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

	public int CreatedPatterns { get; private set; }

	public int CreatedStyles { get; private set; }

	public LineStyleMapper(Document doc)
	{
		_doc = doc;
		_linesCategory = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
		_lineSubcategoriesByName = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
		if (_linesCategory != null)
		{
			foreach (Category sub in _linesCategory.SubCategories)
			{
				_lineSubcategoriesByName[sub.Name] = sub;
			}
		}
	}

	/// <summary>Devuelve el Line Style a usar, o null para dejar el estilo por defecto.</summary>
	public GraphicsStyle Resolve(ElementId sourceStyleId)
	{
		if (sourceStyleId == null || sourceStyleId == ElementId.InvalidElementId || _linesCategory == null)
		{
			return null;
		}

		if (_cache.TryGetValue(sourceStyleId, out GraphicsStyle cached))
		{
			return cached;
		}

		GraphicsStyle resolved = null;
		try
		{
			resolved = Map(_doc.GetElement(sourceStyleId) as GraphicsStyle);
		}
		catch (Exception)
		{
		}

		_cache[sourceStyleId] = resolved;
		return resolved;
	}

	/// <summary>
	/// Line Style para una curva cuyo tipo de línea se leyó del DWG original ("DWG-&lt;capa&gt;-&lt;tipo&gt;"):
	/// mismo color y grosor que la capa, pero con el patrón (continuo o segmentado) real de la entidad.
	/// </summary>
	public GraphicsStyle Resolve(ElementId sourceStyleId, DwgLinePattern pattern)
	{
		if (pattern == null)
		{
			return Resolve(sourceStyleId);
		}

		if (_linesCategory == null)
		{
			return null;
		}

		ElementId key = sourceStyleId ?? ElementId.InvalidElementId;
		if (_patternCache.TryGetValue((key, pattern.Key), out GraphicsStyle cached))
		{
			return cached;
		}

		GraphicsStyle resolved = null;
		try
		{
			GraphicsStyle source = key != ElementId.InvalidElementId ? _doc.GetElement(key) as GraphicsStyle : null;
			Category sourceCategory = source?.GraphicsStyleCategory;
			string layerName = sourceCategory?.Name ?? "0";
			string name = MakeValidName(Prefix + layerName + "-" + pattern.Key);
			if (!_lineSubcategoriesByName.TryGetValue(name, out Category target))
			{
				target = _doc.Settings.Categories.NewSubcategory(_linesCategory, name);
				if (sourceCategory != null)
				{
					CopyAppearance(sourceCategory, target);
				}

				_lineSubcategoriesByName[name] = target;
				CreatedStyles++;
			}

			ElementId patternId = GetOrCreateLinePattern(pattern);
			if (patternId != null)
			{
				target.SetLinePatternId(patternId, GraphicsStyleType.Projection);
			}

			resolved = target.GetGraphicsStyle(GraphicsStyleType.Projection);
		}
		catch (Exception)
		{
			resolved = Resolve(sourceStyleId);
		}

		_patternCache[(key, pattern.Key)] = resolved;
		return resolved;
	}

	private ElementId GetOrCreateLinePattern(DwgLinePattern pattern)
	{
		if (pattern.IsContinuous)
		{
			return LinePatternElement.GetSolidPatternId();
		}

		string name = MakeValidName(Prefix + pattern.Key);
		if (_linePatternIds.TryGetValue(name, out ElementId id))
		{
			return id;
		}

		LinePatternElement element = LinePatternElement.GetLinePatternElementByName(_doc, name);
		if (element == null)
		{
			try
			{
				var linePattern = new LinePattern(name);
				linePattern.SetSegments(pattern.Segments.Select(s => new LinePatternSegment(s.Type, s.Length)).ToList());
				element = LinePatternElement.Create(_doc, linePattern);
				CreatedPatterns++;
			}
			catch (Exception)
			{
				element = null;
			}
		}

		id = element?.Id;
		_linePatternIds[name] = id;
		return id;
	}

	private GraphicsStyle Map(GraphicsStyle source)
	{
		Category sourceCategory = source?.GraphicsStyleCategory;
		if (sourceCategory == null)
		{
			return null;
		}

		// Ya es un Line Style (subcategoría de "Líneas"): se usa tal cual.
		if (sourceCategory.Parent != null && sourceCategory.Parent.Id == _linesCategory.Id)
		{
			return source;
		}

		string name = MakeValidName(Prefix + sourceCategory.Name);
		if (!_lineSubcategoriesByName.TryGetValue(name, out Category target))
		{
			target = _doc.Settings.Categories.NewSubcategory(_linesCategory, name);
			CopyAppearance(sourceCategory, target);
			_lineSubcategoriesByName[name] = target;
			CreatedStyles++;
		}

		return target.GetGraphicsStyle(GraphicsStyleType.Projection);
	}

	private static void CopyAppearance(Category source, Category target)
	{
		try
		{
			Color color = source.LineColor;
			if (color != null && color.IsValid)
			{
				target.LineColor = color;
			}
		}
		catch (Exception)
		{
		}

		try
		{
			int? weight = source.GetLineWeight(GraphicsStyleType.Projection);
			if (weight.HasValue && weight.Value > 0)
			{
				target.SetLineWeight(weight.Value, GraphicsStyleType.Projection);
			}
		}
		catch (Exception)
		{
		}

		try
		{
			ElementId pattern = source.GetLinePatternId(GraphicsStyleType.Projection);
			if (pattern != null && pattern != ElementId.InvalidElementId)
			{
				target.SetLinePatternId(pattern, GraphicsStyleType.Projection);
			}
		}
		catch (Exception)
		{
		}
	}

	private static string MakeValidName(string name)
	{
		string clean = new string(name.Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
		return clean.Length == 0 ? Prefix + "0" : clean;
	}
}
