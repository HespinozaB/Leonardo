using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using Autodesk.Revit.DB;
using Arc = ACadSharp.Entities.Arc;
using Circle = ACadSharp.Entities.Circle;
using Ellipse = ACadSharp.Entities.Ellipse;
using Line = ACadSharp.Entities.Line;
using Spline = ACadSharp.Entities.Spline;
using CadLineType = ACadSharp.Tables.LineType;
using Matrix4 = CSMath.Matrix4;
using RvtXYZ = Autodesk.Revit.DB.XYZ;
using Transform = Autodesk.Revit.DB.Transform;
using XYZ = CSMath.XYZ;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Patrón de línea leído del DWG, ya convertido a longitudes de papel (pies) para la vista actual.
/// </summary>
internal sealed class DwgLinePattern
{
	/// <summary>Nombre del tipo de línea en el DWG (CENTER, HIDDEN, EJES…).</summary>
	public string LineTypeName;

	/// <summary>Segmentos alternados trazo/espacio en pies de papel. Vacío = continua.</summary>
	public List<(LinePatternSegmentType Type, double Length)> Segments;

	/// <summary>Clave única del patrón (nombre + escala) para reutilizarlo.</summary>
	public string Key;

	public bool IsContinuous => Segments == null || Segments.Count == 0;
}

/// <summary>
/// Índice espacial de las entidades del DWG cuyo tipo de línea efectivo es segmentado (o continuo sobre
/// una capa segmentada). Revit solo conserva el tipo de línea por capa, así que las líneas con tipo de
/// línea "por entidad" llegaban continuas: aquí se recupera leyendo el DWG original.
/// </summary>
internal sealed class DwgLinetypeIndex
{
	private const int MaxBlockDepth = 8;

	private const int ArcPrecision = 64;

	private const double MinPaperLength = 0.15 / 304.8;

	private readonly List<Primitive> _primitives = new List<Primitive>();

	private readonly Dictionary<(long, long), List<(int Primitive, int Segment)>> _grid = new Dictionary<(long, long), List<(int, int)>>();

	private readonly Dictionary<string, DwgLinePattern> _patterns = new Dictionary<string, DwgLinePattern>(StringComparer.OrdinalIgnoreCase);

	private readonly double _toFeet;

	private readonly double _paperFactor;

	private readonly double _ltScale;

	private readonly Transform _transform;

	private double _cellSize = 1.0;

	private sealed class Primitive
	{
		public List<RvtXYZ> Points;

		public double Tolerance;

		public DwgLinePattern Pattern;
	}

	public int Count => _primitives.Count;

	private DwgLinetypeIndex(double feetPerUnit, Transform transform, int viewScale, double ltScale)
	{
		_transform = transform;
		_toFeet = feetPerUnit;
		_ltScale = ltScale > 0.0 ? ltScale : 1.0;
		// Longitud en papel = longitud en modelo / escala de la vista.
		_paperFactor = feetPerUnit * Math.Abs(transform.Scale) / Math.Max(viewScale, 1);
	}

	/// <summary>Construye el índice para una instancia de CAD. Devuelve null si el DWG no tiene líneas segmentadas.</summary>
	public static DwgLinetypeIndex Build(CadDocument cad, double feetPerUnit, Transform instanceTransform, int viewScale)
	{
		if (cad?.Entities == null)
		{
			return null;
		}

		var index = new DwgLinetypeIndex(feetPerUnit, instanceTransform, viewScale, cad.Header?.LineTypeScale ?? 1.0);
		try
		{
			index.CollectFrom(cad.Entities, Matrix4.Identity, null, 1.0, 0);
		}
		catch (Exception)
		{
		}

		if (index._primitives.Count == 0)
		{
			return null;
		}

		index.BuildGrid();
		return index;
	}

	/// <summary>
	/// Busca el patrón del DWG para una curva de Revit: sus puntos al 25 %, 50 % y 75 % deben caer
	/// sobre la misma entidad del DWG.
	/// </summary>
	public DwgLinePattern Find(Curve curve)
	{
		RvtXYZ mid, q1, q3;
		try
		{
			mid = curve.Evaluate(0.5, true);
			q1 = curve.Evaluate(0.25, true);
			q3 = curve.Evaluate(0.75, true);
		}
		catch (Exception)
		{
			return null;
		}

		foreach (int candidate in Candidates(mid))
		{
			Primitive p = _primitives[candidate];
			if (DistanceTo(p, q1) <= p.Tolerance && DistanceTo(p, q3) <= p.Tolerance)
			{
				return p.Pattern;
			}
		}

		return null;
	}

	private IEnumerable<int> Candidates(RvtXYZ point)
	{
		var seen = new HashSet<int>();
		var (cx, cy) = Key(point.X, point.Y);
		for (long ix = cx - 1; ix <= cx + 1; ix++)
		{
			for (long iy = cy - 1; iy <= cy + 1; iy++)
			{
				if (!_grid.TryGetValue((ix, iy), out var list))
				{
					continue;
				}

				foreach (var (primitive, segment) in list)
				{
					Primitive p = _primitives[primitive];
					if (!seen.Contains(primitive) && SegmentDistance(p.Points[segment], p.Points[segment + 1], point) <= p.Tolerance)
					{
						seen.Add(primitive);
						yield return primitive;
					}
				}
			}
		}
	}

	private static double DistanceTo(Primitive p, RvtXYZ point)
	{
		double best = double.MaxValue;
		for (int i = 0; i < p.Points.Count - 1; i++)
		{
			best = Math.Min(best, SegmentDistance(p.Points[i], p.Points[i + 1], point));
			if (best <= p.Tolerance)
			{
				break;
			}
		}

		return best;
	}

	/// <summary>Distancia en planta (XY) de un punto a un segmento.</summary>
	private static double SegmentDistance(RvtXYZ a, RvtXYZ b, RvtXYZ p)
	{
		double dx = b.X - a.X;
		double dy = b.Y - a.Y;
		double len2 = dx * dx + dy * dy;
		double t = len2 < 1E-18 ? 0.0 : Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
		double ex = a.X + t * dx - p.X;
		double ey = a.Y + t * dy - p.Y;
		return Math.Sqrt(ex * ex + ey * ey);
	}

	private (long, long) Key(double x, double y) => ((long)Math.Floor(x / _cellSize), (long)Math.Floor(y / _cellSize));

	private void BuildGrid()
	{
		double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
		foreach (Primitive p in _primitives)
		{
			foreach (RvtXYZ pt in p.Points)
			{
				minX = Math.Min(minX, pt.X);
				minY = Math.Min(minY, pt.Y);
				maxX = Math.Max(maxX, pt.X);
				maxY = Math.Max(maxY, pt.Y);
			}
		}

		double extent = Math.Max(maxX - minX, maxY - minY);
		_cellSize = Math.Max(extent / 256.0, 0.05);
		double maxTolerance = _primitives.Max(p => p.Tolerance);
		_cellSize = Math.Max(_cellSize, maxTolerance * 2.0);

		for (int i = 0; i < _primitives.Count; i++)
		{
			List<RvtXYZ> points = _primitives[i].Points;
			for (int s = 0; s < points.Count - 1; s++)
			{
				RvtXYZ a = points[s];
				RvtXYZ b = points[s + 1];
				double length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
				int steps = Math.Max(1, (int)Math.Ceiling(length / (_cellSize * 0.5)));
				var keys = new HashSet<(long, long)>();
				for (int k = 0; k <= steps; k++)
				{
					double t = (double)k / steps;
					keys.Add(Key(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
				}

				foreach (var key in keys)
				{
					if (!_grid.TryGetValue(key, out var list))
					{
						list = new List<(int, int)>();
						_grid[key] = list;
					}

					list.Add((i, s));
				}
			}
		}
	}

	// ------------------------------------------------------------------ Lectura del DWG

	private void CollectFrom(IEnumerable<Entity> entities, Matrix4 transform, CadLineType byBlock, double blockLtScale, int depth)
	{
		foreach (Entity entity in entities)
		{
			if (entity == null || entity.IsInvisible)
			{
				continue;
			}

			if (entity is Insert insert)
			{
				if (depth >= MaxBlockDepth || insert.Block == null)
				{
					continue;
				}

				Matrix4 child;
				try
				{
					child = transform * insert.GetTransform().Matrix;
				}
				catch (Exception)
				{
					continue;
				}

				CadLineType insertLineType = ResolveLineType(entity, byBlock);
				CollectFrom(insert.Block.Entities, child, insertLineType, blockLtScale * entity.LineTypeScale, depth + 1);
				continue;
			}

			CadLineType lineType = ResolveLineType(entity, byBlock);
			CadLineType layerLineType = entity.Layer?.LineType;
			bool dashed = IsDashed(lineType);
			bool continuousOverDashedLayer = !dashed && IsDashed(layerLineType);
			if (!dashed && !continuousOverDashedLayer)
			{
				continue;
			}

			List<List<XYZ>> polylines = GetPolylines(entity, out double tolerance);
			if (polylines == null || polylines.Count == 0)
			{
				continue;
			}

			DwgLinePattern pattern = GetPattern(dashed ? lineType : null, entity.LineTypeScale * blockLtScale);
			foreach (List<XYZ> local in polylines)
			{
				if (local.Count < 2)
				{
					continue;
				}

				var points = new List<RvtXYZ>(local.Count);
				foreach (XYZ p in local)
				{
					XYZ w = transform * p;
					points.Add(_transform.OfPoint(new RvtXYZ(w.X * _toFeet, w.Y * _toFeet, w.Z * _toFeet)));
				}

				_primitives.Add(new Primitive
				{
					Points = points,
					Tolerance = Math.Max(0.002, tolerance * _toFeet * Math.Abs(_transform.Scale)),
					Pattern = pattern
				});
			}
		}
	}

	private static CadLineType ResolveLineType(Entity entity, CadLineType byBlock)
	{
		CadLineType own = null;
		try
		{
			own = entity.LineType;
		}
		catch (Exception)
		{
		}

		string name = own?.Name ?? "ByLayer";
		if (name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
		{
			return entity.Layer?.LineType;
		}

		if (name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
		{
			return byBlock ?? entity.Layer?.LineType;
		}

		return own;
	}

	private static bool IsDashed(CadLineType lineType)
	{
		if (lineType == null || !lineType.IsComplex)
		{
			return false;
		}

		string name = lineType.Name ?? string.Empty;
		if (name.Equals("Continuous", StringComparison.OrdinalIgnoreCase) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return lineType.Segments.Any(s => s.Length < 0.0);
	}

	/// <summary>Convierte la entidad en una o varias polilíneas (en coordenadas del bloque/DWG).</summary>
	private static List<List<XYZ>> GetPolylines(Entity entity, out double tolerance)
	{
		tolerance = 0.0;
		switch (entity)
		{
			case Line line:
				return new List<List<XYZ>> { new List<XYZ> { line.StartPoint, line.EndPoint } };
			case Arc arc:
				tolerance = arc.Radius * (1.0 - Math.Cos(Math.PI / ArcPrecision)) * 2.0;
				return new List<List<XYZ>> { arc.PolygonalVertexes(ArcPrecision) };
			case Circle circle:
			{
				tolerance = circle.Radius * (1.0 - Math.Cos(Math.PI / ArcPrecision)) * 2.0;
				List<XYZ> pts = circle.PolygonalVertexes(ArcPrecision);
				if (pts.Count > 1)
				{
					pts.Add(pts[0]);
				}

				return new List<List<XYZ>> { pts };
			}
			case Ellipse ellipse:
			{
				List<XYZ> pts = ellipse.PolygonalVertexes(ArcPrecision);
				tolerance = ellipse.MajorAxis * 0.01;
				return new List<List<XYZ>> { pts };
			}
			case Spline spline:
			{
				if (!spline.TryPolygonalVertexes(128, out List<XYZ> pts))
				{
					return null;
				}

				tolerance = Diagonal(pts) * 0.01;
				return new List<List<XYZ>> { pts };
			}
			case IPolyline polyline:
			{
				List<XYZ> pts = polyline.GetPoints<XYZ>(ArcPrecision).ToList();
				if (polyline.IsClosed && pts.Count > 1 && !pts[0].Equals(pts[pts.Count - 1]))
				{
					pts.Add(pts[0]);
				}

				tolerance = Diagonal(pts) * 0.002;
				return new List<List<XYZ>> { pts };
			}
			default:
				return null;
		}
	}

	private static double Diagonal(List<XYZ> pts)
	{
		if (pts == null || pts.Count == 0)
		{
			return 0.0;
		}

		double w = pts.Max(p => p.X) - pts.Min(p => p.X);
		double h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
		return Math.Sqrt(w * w + h * h);
	}

	// ------------------------------------------------------------------ Patrones

	private DwgLinePattern GetPattern(CadLineType lineType, double entityLtScale)
	{
		if (lineType == null)
		{
			return GetOrAdd("Continuous", "Continuous", null);
		}

		double scale = _ltScale * (entityLtScale > 0.0 ? entityLtScale : 1.0) * _paperFactor;
		string key = lineType.Name + "|" + scale.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
		if (_patterns.TryGetValue(key, out DwgLinePattern cached))
		{
			return cached;
		}

		return GetOrAdd(key, lineType.Name, BuildSegments(lineType, scale));
	}

	private DwgLinePattern GetOrAdd(string key, string name, List<(LinePatternSegmentType, double)> segments)
	{
		if (!_patterns.TryGetValue(key, out DwgLinePattern pattern))
		{
			pattern = new DwgLinePattern
			{
				LineTypeName = name,
				Segments = segments ?? new List<(LinePatternSegmentType, double)>(),
			};
			pattern.Key = pattern.IsContinuous ? "Continuous" : name + " " + PatternSignature(pattern.Segments);
			_patterns[key] = pattern;
		}

		return pattern;
	}

	/// <summary>Firma corta (longitud total en mm de papel) para distinguir el mismo tipo de línea a distinta escala.</summary>
	private static string PatternSignature(List<(LinePatternSegmentType Type, double Length)> segments)
	{
		double totalMm = segments.Sum(s => s.Length) * 304.8;
		return totalMm.ToString(totalMm >= 10 ? "0.#" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + "mm";
	}

	/// <summary>
	/// Convierte los segmentos del DWG (positivo = trazo, 0 = punto, negativo = espacio) en una secuencia
	/// válida para Revit: empieza con trazo/punto, alterna con espacios y tiene un número par de elementos.
	/// </summary>
	internal static List<(LinePatternSegmentType, double)> BuildSegments(CadLineType lineType, double scale)
	{
		var raw = new List<(bool IsMark, bool IsDot, double Length)>();
		foreach (CadLineType.Segment s in lineType.Segments)
		{
			double len = Math.Abs(s.Length) * scale;
			if (s.Length > 0.0)
			{
				raw.Add((true, false, len));
			}
			else if (s.Length == 0.0)
			{
				raw.Add((true, true, 0.0));
			}
			else
			{
				raw.Add((false, false, len));
			}
		}

		// Unir elementos consecutivos del mismo tipo (trazo+trazo, espacio+espacio).
		var merged = new List<(bool IsMark, bool IsDot, double Length)>();
		foreach (var item in raw)
		{
			if (merged.Count > 0 && merged[merged.Count - 1].IsMark == item.IsMark)
			{
				var last = merged[merged.Count - 1];
				double length = last.Length + item.Length;
				merged[merged.Count - 1] = (last.IsMark, last.IsDot && item.IsDot && length <= 0.0, length);
			}
			else
			{
				merged.Add(item);
			}
		}

		if (!merged.Any(m => !m.IsMark) || !merged.Any(m => m.IsMark))
		{
			return new List<(LinePatternSegmentType, double)>();
		}

		// Rotar para que empiece con trazo.
		int firstMark = merged.FindIndex(m => m.IsMark);
		merged = merged.Skip(firstMark).Concat(merged.Take(firstMark)).ToList();

		// Si termina en trazo, se une con el primero (mismo patrón, desfasado).
		if (merged.Count > 1 && merged[merged.Count - 1].IsMark)
		{
			var last = merged[merged.Count - 1];
			var first = merged[0];
			double length = first.Length + last.Length;
			merged[0] = (true, first.IsDot && last.IsDot && length <= 0.0, length);
			merged.RemoveAt(merged.Count - 1);
		}

		var result = new List<(LinePatternSegmentType, double)>();
		foreach (var m in merged)
		{
			if (!m.IsMark)
			{
				result.Add((LinePatternSegmentType.Space, Math.Max(m.Length, MinPaperLength)));
			}
			else if (m.IsDot || m.Length < MinPaperLength)
			{
				result.Add((LinePatternSegmentType.Dot, 0.0));
			}
			else
			{
				result.Add((LinePatternSegmentType.Dash, m.Length));
			}
		}

		return result.Count >= 2 && result.Count % 2 == 0 ? result : new List<(LinePatternSegmentType, double)>();
	}
}
