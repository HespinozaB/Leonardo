using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Áreas de los hatch ya convertidos en Filled Region, por capa. Sirve para descartar la geometría con la
/// que Revit representa esos hatch (líneas del patrón y rellenos), que si no quedaría duplicada.
/// </summary>
internal sealed class HatchAreaIndex
{
	private readonly Dictionary<string, List<List<XYZ>>> _loopsByLayer = new Dictionary<string, List<List<XYZ>>>(StringComparer.OrdinalIgnoreCase);

	private readonly double _tolerance;

	public HatchAreaIndex(double tolerance)
	{
		_tolerance = Math.Max(tolerance, 1E-04);
	}

	public bool IsEmpty => _loopsByLayer.Count == 0;

	public void Add(HatchRegion region)
	{
		if (!_loopsByLayer.TryGetValue(region.Layer, out var loops))
		{
			loops = new List<List<XYZ>>();
			_loopsByLayer[region.Layer] = loops;
		}

		loops.AddRange(region.Loops);
	}

	public bool HasLayer(string layer) => layer != null && _loopsByLayer.ContainsKey(layer);

	/// <summary>True si el punto está dentro de algún hatch ya convertido (de cualquier capa).</summary>
	public bool Contains(XYZ point)
	{
		foreach (List<List<XYZ>> loops in _loopsByLayer.Values)
		{
			if (IsInside(loops, point))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// True si la curva está en una capa con hatch y queda dentro del área del hatch sin apoyarse en su
	/// contorno (es decir, es una línea del patrón y no un borde real del dibujo).
	/// </summary>
	public bool IsPatternLine(string layer, Curve curve)
	{
		if (layer == null || !_loopsByLayer.TryGetValue(layer, out var loops))
		{
			return false;
		}

		try
		{
			foreach (double t in new[] { 0.25, 0.5, 0.75 })
			{
				XYZ p = curve.Evaluate(t, true);
				if (!IsInside(loops, p) || IsOnBoundary(loops, p))
				{
					return false;
				}
			}

			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Regla par-impar sobre todos los contornos (las islas cuentan como huecos).</summary>
	private static bool IsInside(List<List<XYZ>> loops, XYZ p)
	{
		bool inside = false;
		foreach (List<XYZ> loop in loops)
		{
			for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
			{
				XYZ a = loop[i];
				XYZ b = loop[j];
				if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
				{
					inside = !inside;
				}
			}
		}

		return inside;
	}

	private bool IsOnBoundary(List<List<XYZ>> loops, XYZ p)
	{
		foreach (List<XYZ> loop in loops)
		{
			for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
			{
				if (SegmentDistance(loop[j], loop[i], p) <= _tolerance)
				{
					return true;
				}
			}
		}

		return false;
	}

	private static double SegmentDistance(XYZ a, XYZ b, XYZ p)
	{
		double dx = b.X - a.X;
		double dy = b.Y - a.Y;
		double len2 = dx * dx + dy * dy;
		double t = len2 < 1E-18 ? 0.0 : Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
		double ex = a.X + t * dx - p.X;
		double ey = a.Y + t * dy - p.Y;
		return Math.Sqrt(ex * ex + ey * ey);
	}

	/// <summary>
	/// Divide una malla en piezas conectadas (triángulos que comparten vértices). Revit a veces junta en una
	/// sola malla varios rellenos que se solapan; tratarlos por separado evita contornos mezclados.
	/// Devuelve el contorno y un punto interior de cada pieza.
	/// </summary>
	public static List<(List<CurveLoop> Loops, XYZ Sample)> MeshPieces(Mesh mesh, double shortCurve)
	{
		int n = mesh.NumTriangles;
		var parent = Enumerable.Range(0, n).ToArray();
		int Find(int x)
		{
			while (parent[x] != x)
			{
				parent[x] = parent[parent[x]];
				x = parent[x];
			}

			return x;
		}

		var firstByVertex = new Dictionary<(long, long), int>();
		var triangles = new List<MeshTriangle>(n);
		for (int i = 0; i < n; i++)
		{
			MeshTriangle tri = mesh.get_Triangle(i);
			triangles.Add(tri);
			for (int k = 0; k < 3; k++)
			{
				var key = VertexKey(tri.get_Vertex(k));
				if (firstByVertex.TryGetValue(key, out int other))
				{
					parent[Find(i)] = Find(other);
				}
				else
				{
					firstByVertex[key] = i;
				}
			}
		}

		var result = new List<(List<CurveLoop>, XYZ)>();
		foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
		{
			List<MeshTriangle> tris = group.Select(i => triangles[i]).ToList();
			MeshTriangle first = tris[0];
			XYZ sample = (first.get_Vertex(0) + first.get_Vertex(1) + first.get_Vertex(2)) / 3.0;
			result.Add((Outline(tris, shortCurve), sample));
		}

		return result;
	}

	private static (long, long) VertexKey(XYZ p) => ((long)Math.Round(p.X * 1E5), (long)Math.Round(p.Y * 1E5));

	/// <summary>Contornos exteriores de un conjunto de triángulos (aristas que pertenecen a un solo triángulo), encadenados.</summary>
	private static List<CurveLoop> Outline(List<MeshTriangle> triangles, double shortCurve)
	{
		var edges = new Dictionary<(long, long, long, long), (XYZ A, XYZ B, int Count)>();
		(long, long) Key(XYZ p) => VertexKey(p);

		foreach (MeshTriangle tri in triangles)
		{
			for (int k = 0; k < 3; k++)
			{
				XYZ a = tri.get_Vertex(k);
				XYZ b = tri.get_Vertex((k + 1) % 3);
				var ka = Key(a);
				var kb = Key(b);
				if (ka == kb)
				{
					continue;
				}

				var key = ka.CompareTo(kb) < 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2) : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
				edges[key] = edges.TryGetValue(key, out var e) ? (e.A, e.B, e.Count + 1) : (a, b, 1);
			}
		}

		// Aristas de borde, indexadas por su punto inicial para encadenarlas.
		var byStart = new Dictionary<(long, long), List<(XYZ A, XYZ B)>>();
		foreach (var e in edges.Values.Where(e => e.Count == 1))
		{
			var k = Key(e.A);
			if (!byStart.TryGetValue(k, out var list))
			{
				list = new List<(XYZ, XYZ)>();
				byStart[k] = list;
			}

			list.Add((e.A, e.B));
		}

		var loops = new List<CurveLoop>();
		while (byStart.Count > 0)
		{
			var startKey = byStart.Keys.First();
			var points = new List<XYZ>();
			var currentKey = startKey;
			while (byStart.TryGetValue(currentKey, out var list) && list.Count > 0)
			{
				var (a, b) = list[list.Count - 1];
				list.RemoveAt(list.Count - 1);
				if (list.Count == 0)
				{
					byStart.Remove(currentKey);
				}

				points.Add(a);
				currentKey = Key(b);
				if (currentKey == startKey)
				{
					break;
				}
			}

			if (points.Count >= 3 && currentKey == startKey)
			{
				CurveLoop loop = BuildLoop(points, shortCurve);
				if (loop != null)
				{
					loops.Add(loop);
				}
			}
		}

		return loops;
	}

	private static CurveLoop BuildLoop(List<XYZ> points, double shortCurve)
	{
		var clean = new List<XYZ>();
		foreach (XYZ p in points)
		{
			if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > shortCurve * 1.5)
			{
				clean.Add(p);
			}
		}

		if (clean.Count < 3 || clean[0].DistanceTo(clean[clean.Count - 1]) <= shortCurve * 1.5)
		{
			return clean.Count >= 4 ? BuildLoop(clean.Take(clean.Count - 1).ToList(), shortCurve) : null;
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
}
