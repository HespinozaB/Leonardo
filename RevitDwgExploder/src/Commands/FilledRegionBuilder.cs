using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Crea Filled Regions nativos (con su patrón de relleno y color) a partir de los hatch del DWG,
/// o regiones sólidas a partir de los rellenos que Revit muestra en el CAD importado.
/// </summary>
internal sealed class FilledRegionBuilder
{
	private const string Prefix = "DWG-";

	private readonly Document _doc;

	private readonly View _view;

	private readonly double _shortCurve;

	private readonly Dictionary<string, ElementId> _patternIds = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, ElementId> _typeIds = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

	private Dictionary<string, FilledRegionType> _existingTypes;

	private ElementId _invisibleLineStyle;

	private bool _invisibleResolved;

	private Plane _viewPlane;

	public int Created { get; private set; }

	public int CreatedPatterns { get; private set; }

	public int Failed { get; private set; }

	public FilledRegionBuilder(Document doc, View view, double shortCurveTolerance)
	{
		_doc = doc;
		_view = view;
		_shortCurve = shortCurveTolerance;
		try
		{
			_viewPlane = view.SketchPlane?.GetPlane() ?? Plane.CreateByNormalAndOrigin(view.ViewDirection, view.Origin);
		}
		catch (Exception)
		{
			_viewPlane = null;
		}
	}

	/// <summary>Crea el Filled Region de un hatch del DWG. Devuelve true si se creó.</summary>
	public bool Create(HatchRegion hatch)
	{
		ElementId pattern = null;
		if (!hatch.IsSolid)
		{
			pattern = GetHatchPattern(hatch);
			if (pattern == null)
			{
				// Sin patrón equivalente no se crea la región (se conservan las líneas que Revit ya tenía).
				Failed++;
				return false;
			}
		}

		ElementId typeId = GetRegionType(pattern, hatch.R, hatch.G, hatch.B);
		if (typeId == null)
		{
			Failed++;
			return false;
		}

		List<CurveLoop> loops = hatch.Loops.Select(ToLoop).Where(l => l != null).ToList();
		return CreateRegion(typeId, loops);
	}

	/// <summary>Relleno sólido (sin archivo DWG): contornos ya como CurveLoop, color de la capa.</summary>
	public bool CreateSolid(IList<CurveLoop> loops, Color color)
	{
		byte r = color != null && color.IsValid ? color.Red : (byte)0;
		byte g = color != null && color.IsValid ? color.Green : (byte)0;
		byte b = color != null && color.IsValid ? color.Blue : (byte)0;
		ElementId typeId = GetRegionType(null, r, g, b);
		if (typeId == null)
		{
			Failed++;
			return false;
		}

		var projected = new List<CurveLoop>();
		foreach (CurveLoop loop in loops)
		{
			var points = new List<XYZ>();
			foreach (Curve curve in loop)
			{
				IList<XYZ> tessellated = curve.Tessellate();
				points.AddRange(tessellated.Take(tessellated.Count - 1));
			}

			CurveLoop clean = ToLoop(points);
			if (clean != null)
			{
				projected.Add(clean);
			}
		}

		return CreateRegion(typeId, projected);
	}

	private bool CreateRegion(ElementId typeId, List<CurveLoop> loops)
	{
		if (loops.Count == 0)
		{
			Failed++;
			return false;
		}

		// Primero todos los contornos juntos (así las islas quedan como huecos); si Revit los rechaza,
		// se intenta cada contorno por separado.
		if (TryCreate(typeId, loops))
		{
			return true;
		}

		bool any = false;
		foreach (CurveLoop loop in loops)
		{
			any |= TryCreate(typeId, new List<CurveLoop> { loop });
		}

		if (!any)
		{
			Failed++;
		}

		return any;
	}

	private bool TryCreate(ElementId typeId, IList<CurveLoop> loops)
	{
		try
		{
			FilledRegion region = FilledRegion.Create(_doc, typeId, _view.Id, loops);
			ElementId invisible = GetInvisibleLineStyle();
			if (invisible != null)
			{
				try
				{
					region.SetLineStyleId(invisible);
				}
				catch (Exception)
				{
				}
			}

			Created++;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Polígono cerrado proyectado al plano de la vista, sin tramos más cortos de lo que Revit admite.</summary>
	private CurveLoop ToLoop(List<XYZ> points)
	{
		if (points == null || points.Count < 3)
		{
			return null;
		}

		var clean = new List<XYZ>();
		foreach (XYZ p in points.Select(Project))
		{
			if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > _shortCurve * 1.5)
			{
				clean.Add(p);
			}
		}

		while (clean.Count > 2 && clean[0].DistanceTo(clean[clean.Count - 1]) <= _shortCurve * 1.5)
		{
			clean.RemoveAt(clean.Count - 1);
		}

		if (clean.Count < 3)
		{
			return null;
		}

		try
		{
			var loop = new CurveLoop();
			for (int i = 0; i < clean.Count; i++)
			{
				loop.Append(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
			}

			return loop;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private XYZ Project(XYZ p)
	{
		if (_viewPlane == null)
		{
			return p;
		}

		double d = (p - _viewPlane.Origin).DotProduct(_viewPlane.Normal);
		return p - _viewPlane.Normal.Multiply(d);
	}

	// ------------------------------------------------------------------ Patrones de relleno

	private ElementId GetHatchPattern(HatchRegion hatch)
	{
		string signature = string.Join(";", hatch.Grids.Select(g =>
			string.Join(",", new[] { g.Angle, g.Offset, g.Shift }.Concat(g.Segments).Select(v => v.ToString("G5", CultureInfo.InvariantCulture)))));
		string key = hatch.PatternName + "|" + signature;
		if (_patternIds.TryGetValue(key, out ElementId cached))
		{
			return cached;
		}

		double spacingMm = hatch.Grids.Min(g => g.Offset) * 304.8;
		string name = MakeValidName($"{Prefix}{hatch.PatternName} {spacingMm.ToString(spacingMm >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture)}mm");
		ElementId id = FindPattern(name)
			?? TryCreatePattern(name, hatch.Grids, withDashes: true)
			?? TryCreatePattern(name + " (simple)", hatch.Grids, withDashes: false)
			?? FindPattern("IMPORT-" + hatch.PatternName)
			?? FindPattern(hatch.PatternName);

		_patternIds[key] = id;
		return id;
	}

	private ElementId FindPattern(string name)
	{
		try
		{
			return (FillPatternElement.GetFillPatternElementByName(_doc, FillPatternTarget.Model, name)
				?? FillPatternElement.GetFillPatternElementByName(_doc, FillPatternTarget.Drafting, name))?.Id;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Patrón de modelo: queda fijo al dibujo (misma escala que el hatch del DWG) sea cual sea la escala de la vista.</summary>
	private ElementId TryCreatePattern(string name, List<HatchGrid> grids, bool withDashes)
	{
		try
		{
			var fillGrids = new List<FillGrid>();
			foreach (HatchGrid g in grids)
			{
				var grid = new FillGrid
				{
					Angle = g.Angle,
					Origin = new UV(g.OriginX, g.OriginY),
					Offset = g.Offset,
					Shift = withDashes ? g.Shift : 0.0
				};
				if (withDashes && g.Segments.Count > 0)
				{
					grid.SetSegments(g.Segments);
				}

				fillGrids.Add(grid);
			}

			var pattern = new FillPattern(name, FillPatternTarget.Model, FillPatternHostOrientation.ToView);
			pattern.SetFillGrids(fillGrids);
			FillPatternElement element = FillPatternElement.Create(_doc, pattern);
			CreatedPatterns++;
			return element.Id;
		}
		catch (Exception)
		{
			return null;
		}
	}

	// ------------------------------------------------------------------ Tipos de Filled Region

	/// <summary>Tipo "DWG-&lt;patrón&gt; R-G-B" (patrón null = relleno sólido), transparente y sin fondo.</summary>
	private ElementId GetRegionType(ElementId patternId, byte r, byte g, byte b)
	{
		ElementId fill = patternId ?? GetSolidFill();
		if (fill == null)
		{
			return null;
		}

		string label = patternId == null ? "Sólido" : (_doc.GetElement(patternId)?.Name ?? "Patrón");
		if (label.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
		{
			label = label.Substring(Prefix.Length);
		}

		string name = MakeValidName($"{Prefix}{label} {r:D3}-{g:D3}-{b:D3}");
		if (_typeIds.TryGetValue(name, out ElementId cached))
		{
			return cached;
		}

		_existingTypes ??= new FilteredElementCollector(_doc)
			.OfClass(typeof(FilledRegionType))
			.Cast<FilledRegionType>()
			.GroupBy(t => t.Name)
			.ToDictionary(t => t.Key, t => t.First());

		ElementId id = null;
		try
		{
			if (!_existingTypes.TryGetValue(name, out FilledRegionType type))
			{
				FilledRegionType template = _existingTypes.Values.FirstOrDefault();
				type = template?.Duplicate(name) as FilledRegionType;
				if (type != null)
				{
					_existingTypes[name] = type;
				}
			}

			if (type != null)
			{
				type.ForegroundPatternId = fill;
				type.ForegroundPatternColor = new Color(r, g, b);
				type.BackgroundPatternId = ElementId.InvalidElementId;
				type.IsMasking = false;
				id = type.Id;
			}
		}
		catch (Exception)
		{
			id = null;
		}

		_typeIds[name] = id;
		return id;
	}

	private ElementId GetSolidFill()
	{
		if (_patternIds.TryGetValue("<solid>", out ElementId cached))
		{
			return cached;
		}

		ElementId id = null;
		try
		{
			id = new FilteredElementCollector(_doc)
				.OfClass(typeof(FillPatternElement))
				.Cast<FillPatternElement>()
				.FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id;
		}
		catch (Exception)
		{
		}

		_patternIds["<solid>"] = id;
		return id;
	}

	private ElementId GetInvisibleLineStyle()
	{
		if (_invisibleResolved)
		{
			return _invisibleLineStyle;
		}

		_invisibleResolved = true;
		try
		{
			foreach (ElementId id in FilledRegion.GetValidLineStyleIdsForFilledRegion(_doc))
			{
				if (_doc.GetElement(id) is GraphicsStyle style
					&& style.GraphicsStyleCategory?.Id.Value == (long)BuiltInCategory.OST_InvisibleLines)
				{
					_invisibleLineStyle = id;
					break;
				}
			}
		}
		catch (Exception)
		{
		}

		return _invisibleLineStyle;
	}

	private static string MakeValidName(string name)
	{
		char[] invalid = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' };
		return new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
	}
}
