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

	/// <summary>
	/// Planos candidatos donde crear las regiones. Revit exige que el contorno esté en el plano de la vista,
	/// y según el tipo de vista ese plano es el plano de trabajo, el del nivel o el del origen de la vista;
	/// el último candidato (null) usa la cota original del DWG. El que funciona pasa a ser el primero.
	/// </summary>
	private readonly List<Plane> _planes = new List<Plane>();

	public int Created { get; private set; }

	public int CreatedPatterns { get; private set; }

	public int Failed { get; private set; }

	private ElementId _lastCreated;

	public FilledRegionBuilder(Document doc, View view, double shortCurveTolerance)
	{
		_doc = doc;
		_view = view;
		_shortCurve = shortCurveTolerance;

		XYZ normal = view.ViewDirection;
		void AddPlane(Func<Plane> factory)
		{
			try
			{
				Plane plane = factory();
				if (plane != null && !_planes.Any(p => p != null && Math.Abs((plane.Origin - p.Origin).DotProduct(p.Normal)) < 1E-06))
				{
					_planes.Add(plane);
				}
			}
			catch (Exception)
			{
			}
		}

		AddPlane(() => view.SketchPlane?.GetPlane());
		AddPlane(() => view is ViewPlan plan && plan.GenLevel != null
			? Plane.CreateByNormalAndOrigin(normal, new XYZ(0, 0, plan.GenLevel.ProjectElevation))
			: null);
		AddPlane(() => Plane.CreateByNormalAndOrigin(normal, view.Origin));
		_planes.Add(null);
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

		return hatch.OuterFirst ? CreateWithHoles(typeId, hatch.Loops) : CreateRegion(typeId, hatch.Loops);
	}

	/// <summary>
	/// Contorno exterior + huecos. Si Revit no los acepta juntos, se crea el exterior solo y se le van añadiendo los
	/// huecos que acepte (recreando la región); un hueco nunca se crea como relleno aparte.
	/// </summary>
	private bool CreateWithHoles(ElementId typeId, List<List<XYZ>> pointLoops)
	{
		if (pointLoops.Count == 0)
		{
			Failed++;
			return false;
		}

		foreach (Plane plane in _planes.ToList())
		{
			List<CurveLoop> all = pointLoops.Select(p => ToLoop(p, plane)).ToList();
			if (all[0] == null)
			{
				continue;
			}

			if (all.Count > 1 && all.All(l => l != null) && TryCreate(typeId, all))
			{
				Prefer(plane);
				return true;
			}

			ElementId current = TryCreateId(typeId, new List<CurveLoop> { all[0] });
			if (current == null)
			{
				continue;
			}

			Prefer(plane);
			var accepted = new List<CurveLoop> { all[0] };
			foreach (CurveLoop hole in all.Skip(1).Where(l => l != null))
			{
				var attempt = new List<CurveLoop>(accepted) { hole };
				ElementId replaced = TryCreateId(typeId, attempt);
				if (replaced == null)
				{
					continue;
				}

				try
				{
					_doc.Delete(current);
					Created--;
				}
				catch (Exception)
				{
				}

				current = replaced;
				accepted = attempt;
			}

			return true;
		}

		Failed++;
		return false;
	}

	private ElementId TryCreateId(ElementId typeId, IList<CurveLoop> loops)
	{
		int before = Created;
		if (!TryCreate(typeId, loops))
		{
			return null;
		}

		return Created > before ? _lastCreated : null;
	}

	/// <summary>Relleno sólido a partir de contornos de Revit (mallas o láminas del CAD), con el color dado.</summary>
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

		var pointLoops = new List<List<XYZ>>();
		foreach (CurveLoop loop in loops)
		{
			var points = new List<XYZ>();
			foreach (Curve curve in loop)
			{
				IList<XYZ> tessellated = curve.Tessellate();
				points.AddRange(tessellated.Take(tessellated.Count - 1));
			}

			pointLoops.Add(points);
		}

		return CreateRegion(typeId, pointLoops);
	}

	private bool CreateRegion(ElementId typeId, List<List<XYZ>> pointLoops)
	{
		if (pointLoops.Count == 0)
		{
			Failed++;
			return false;
		}

		// Primero todos los contornos juntos (así las islas quedan como huecos), probando cada plano.
		foreach (Plane plane in _planes.ToList())
		{
			List<CurveLoop> loops = pointLoops.Select(p => ToLoop(p, plane)).Where(l => l != null).ToList();
			if (loops.Count > 0 && TryCreate(typeId, loops))
			{
				Prefer(plane);
				return true;
			}
		}

		// Si Revit los rechaza juntos (contornos que se cruzan), cada contorno por separado.
		bool any = false;
		foreach (List<XYZ> points in pointLoops)
		{
			foreach (Plane plane in _planes.ToList())
			{
				CurveLoop loop = ToLoop(points, plane);
				if (loop != null && TryCreate(typeId, new List<CurveLoop> { loop }))
				{
					Prefer(plane);
					any = true;
					break;
				}
			}
		}

		if (!any)
		{
			Failed++;
		}

		return any;
	}

	private void Prefer(Plane plane)
	{
		int index = _planes.IndexOf(plane);
		if (index > 0)
		{
			_planes.RemoveAt(index);
			_planes.Insert(0, plane);
		}
	}

	private bool TryCreate(ElementId typeId, IList<CurveLoop> loops)
	{
		try
		{
			FilledRegion region = FilledRegion.Create(_doc, typeId, _view.Id, loops);
			_lastCreated = region.Id;
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

	/// <summary>Polígono cerrado proyectado al plano indicado, sin tramos más cortos de lo que Revit admite.</summary>
	private CurveLoop ToLoop(List<XYZ> points, Plane plane)
	{
		if (points == null || points.Count < 3)
		{
			return null;
		}

		var clean = new List<XYZ>();
		foreach (XYZ raw in points)
		{
			XYZ p = Project(raw, plane);
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

	private static XYZ Project(XYZ p, Plane plane)
	{
		if (plane == null)
		{
			return p;
		}

		double d = (p - plane.Origin).DotProduct(plane.Normal);
		return p - plane.Normal.Multiply(d);
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
