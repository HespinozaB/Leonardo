using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitParamFill.Core;

/// <summary>Un sector: polígono en planta (coordenadas de Revit, pies) con su nombre.</summary>
public sealed class Polygon
{
	public string Name;

	public List<(double X, double Y)> Points;

	public double Area;

	public double MinX, MinY, MaxX, MaxY;

	/// <summary>Elemento de Revit que lo define (región, línea o DWG), para mostrarlo.</summary>
	public long SourceId = -1;

	public Polygon(string name, List<(double X, double Y)> points, long sourceId = -1)
	{
		Name = name;
		Points = points;
		SourceId = sourceId;
		MinX = points.Min(p => p.X);
		MinY = points.Min(p => p.Y);
		MaxX = points.Max(p => p.X);
		MaxY = points.Max(p => p.Y);
		double sum = 0;
		for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
		{
			sum += (points[j].X * points[i].Y) - (points[i].X * points[j].Y);
		}

		Area = Math.Abs(sum) / 2;
	}

	/// <summary>Punto dentro del polígono (regla par-impar; el borde cuenta como dentro).</summary>
	public bool Contains(double x, double y, double tolerance = 1e-6)
	{
		if (x < MinX - tolerance || x > MaxX + tolerance || y < MinY - tolerance || y > MaxY + tolerance)
		{
			return false;
		}

		if (DistanceToEdge(x, y) <= tolerance)
		{
			return true;
		}

		bool inside = false;
		for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
		{
			(double xi, double yi) = Points[i];
			(double xj, double yj) = Points[j];
			if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
			{
				inside = !inside;
			}
		}

		return inside;
	}

	/// <summary>Distancia del punto al borde más cercano.</summary>
	public double DistanceToEdge(double x, double y)
	{
		double best = double.MaxValue;
		for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
		{
			best = Math.Min(best, SegmentDistance(x, y, Points[j], Points[i]));
		}

		return best;
	}

	private static double SegmentDistance(double x, double y, (double X, double Y) a, (double X, double Y) b)
	{
		double dx = b.X - a.X;
		double dy = b.Y - a.Y;
		double length = (dx * dx) + (dy * dy);
		double t = length == 0 ? 0 : Math.Max(0, Math.Min(1, (((x - a.X) * dx) + ((y - a.Y) * dy)) / length));
		double px = a.X + (t * dx) - x;
		double py = a.Y + (t * dy) - y;
		return Math.Sqrt((px * px) + (py * py));
	}
}

/// <summary>Busca en qué sector cae un punto.</summary>
public static class SectorIndex
{
	/// <summary>El sector que contiene el punto; si hay varios (solapados), el de menor área.</summary>
	public static Polygon Find(IEnumerable<Polygon> sectors, double x, double y) =>
		sectors.Where(s => s.Contains(x, y, 0.01)).OrderBy(s => s.Area).FirstOrDefault();

	/// <summary>
	/// El sector con la mayor parte del elemento: en los lineales, la fracción de su longitud dentro de cada sector; en el resto,
	/// el área de su caja en planta recortada por cada sector. Varios polígonos con el mismo nombre suman. Si no toca ninguno,
	/// se usa su punto central. Devuelve el nombre, su porcentaje (0–1) y el reparto entre sectores.
	/// </summary>
	public static (string Name, double Share, List<(string Name, double Share)> All) Best(IList<Polygon> sectors, ElementInfo element)
	{
		var shares = new Dictionary<string, double>(StringComparer.Ordinal);
		if (element.CurvePoints != null && element.CurvePoints.Count > 1)
		{
			foreach ((double x, double y) in element.CurvePoints)
			{
				Polygon sector = Find(sectors, x, y);
				if (sector != null)
				{
					shares[sector.Name] = (shares.TryGetValue(sector.Name, out double n) ? n : 0) + 1.0 / element.CurvePoints.Count;
				}
			}
		}
		else if (element.HasBox && element.BoxMaxX - element.BoxMinX > 1e-6 && element.BoxMaxY - element.BoxMinY > 1e-6)
		{
			double boxArea = (element.BoxMaxX - element.BoxMinX) * (element.BoxMaxY - element.BoxMinY);
			foreach (Polygon sector in sectors)
			{
				if (sector.MaxX < element.BoxMinX || sector.MinX > element.BoxMaxX || sector.MaxY < element.BoxMinY || sector.MinY > element.BoxMaxY)
				{
					continue;
				}

				double overlap = ClippedArea(sector.Points, element.BoxMinX, element.BoxMinY, element.BoxMaxX, element.BoxMaxY);
				if (overlap > 1e-9)
				{
					shares[sector.Name] = (shares.TryGetValue(sector.Name, out double a) ? a : 0) + overlap / boxArea;
				}
			}
		}

		if (shares.Count == 0)
		{
			Polygon center = element.HasLocation ? Find(sectors, element.X, element.Y) : null;
			return center == null ? (null, 0, new List<(string, double)>()) : (center.Name, 1, new List<(string, double)> { (center.Name, 1) });
		}

		List<(string Name, double Share)> ordered = shares.Select(p => (p.Key, Math.Min(1, p.Value))).OrderByDescending(p => p.Item2).ToList();
		return (ordered[0].Name, ordered[0].Share, ordered);
	}

	/// <summary>Área del polígono recortado por un rectángulo (Sutherland–Hodgman: vale para polígonos cóncavos).</summary>
	public static double ClippedArea(List<(double X, double Y)> polygon, double minX, double minY, double maxX, double maxY)
	{
		List<(double X, double Y)> points = polygon;
		points = Clip(points, p => p.X >= minX, (a, b) => Cross(a, b, minX, true));
		points = Clip(points, p => p.X <= maxX, (a, b) => Cross(a, b, maxX, true));
		points = Clip(points, p => p.Y >= minY, (a, b) => Cross(a, b, minY, false));
		points = Clip(points, p => p.Y <= maxY, (a, b) => Cross(a, b, maxY, false));
		if (points.Count < 3)
		{
			return 0;
		}

		double sum = 0;
		for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
		{
			sum += (points[j].X * points[i].Y) - (points[i].X * points[j].Y);
		}

		return Math.Abs(sum) / 2;
	}

	private static List<(double X, double Y)> Clip(List<(double X, double Y)> input, Func<(double X, double Y), bool> inside,
		Func<(double X, double Y), (double X, double Y), (double X, double Y)> cross)
	{
		var output = new List<(double X, double Y)>();
		if (input.Count == 0)
		{
			return output;
		}

		(double X, double Y) previous = input[input.Count - 1];
		foreach ((double X, double Y) current in input)
		{
			if (inside(current))
			{
				if (!inside(previous))
				{
					output.Add(cross(previous, current));
				}

				output.Add(current);
			}
			else if (inside(previous))
			{
				output.Add(cross(previous, current));
			}

			previous = current;
		}

		return output;
	}

	/// <summary>Punto donde el segmento a-b cruza la recta x = value (vertical) o y = value.</summary>
	private static (double X, double Y) Cross((double X, double Y) a, (double X, double Y) b, double value, bool vertical)
	{
		if (vertical)
		{
			double t = Math.Abs(b.X - a.X) < 1e-12 ? 0 : (value - a.X) / (b.X - a.X);
			return (value, a.Y + (t * (b.Y - a.Y)));
		}

		double u = Math.Abs(b.Y - a.Y) < 1e-12 ? 0 : (value - a.Y) / (b.Y - a.Y);
		return (a.X + (u * (b.X - a.X)), value);
	}

	/// <summary>El sector más cercano (para deducir cuando el punto cae fuera de todos).</summary>
	public static (Polygon Sector, double Distance) Nearest(IEnumerable<Polygon> sectors, double x, double y)
	{
		Polygon best = null;
		double distance = double.MaxValue;
		foreach (Polygon sector in sectors)
		{
			double d = sector.DistanceToEdge(x, y);
			if (d < distance)
			{
				distance = d;
				best = sector;
			}
		}

		return (best, distance);
	}
}

/// <summary>Comparar textos sin tildes, mayúsculas ni signos.</summary>
public static class Text
{
	private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
	{
		"de", "del", "la", "las", "el", "los", "con", "sin", "y", "en", "para", "por", "tipo", "familia", "generico", "basico", "mm", "cm"
	};

	public static string Simplify(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		string decomposed = text.Replace(' ', ' ').Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(decomposed.Length);
		bool space = false;
		foreach (char c in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			if (char.IsLetterOrDigit(c))
			{
				builder.Append(c);
				space = false;
			}
			else if (!space && builder.Length > 0)
			{
				builder.Append(' ');
				space = true;
			}
		}

		return builder.ToString().TrimEnd();
	}

	/// <summary>Palabras significativas, sin plurales simples.</summary>
	public static HashSet<string> Tokens(string text)
	{
		var tokens = new HashSet<string>(StringComparer.Ordinal);
		foreach (string word in Simplify(text).Split(' '))
		{
			if (word.Length < 3 || StopWords.Contains(word) || char.IsDigit(word[0]))
			{
				continue;
			}

			tokens.Add(word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) && "rlnd".IndexOf(word[word.Length - 3]) >= 0
				? word.Substring(0, word.Length - 2)
				: word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal) ? word.Substring(0, word.Length - 1) : word);
		}

		return tokens;
	}

	/// <summary>Parecido 0–1 entre dos textos por palabras en común.</summary>
	public static double Similarity(HashSet<string> a, HashSet<string> b)
	{
		if (a.Count == 0 || b.Count == 0)
		{
			return 0;
		}

		int shared = a.Count(b.Contains);
		return 2.0 * shared / (a.Count + b.Count);
	}
}

/// <summary>Preferencias del usuario en %AppData%\EMASY\Rellenar.ini (clave=valor).</summary>
public static class Settings
{
	private static readonly object Gate = new object();

	private static Dictionary<string, string> _cache;

	private static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "Rellenar.ini");

	private static Dictionary<string, string> Values()
	{
		if (_cache != null)
		{
			return _cache;
		}

		_cache = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			if (File.Exists(FilePath))
			{
				foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
				{
					int equals = line.IndexOf('=');
					if (equals > 0)
					{
						_cache[line.Substring(0, equals)] = line.Substring(equals + 1);
					}
				}
			}
		}
		catch (Exception)
		{
		}

		return _cache;
	}

	public static string Get(string key)
	{
		lock (Gate)
		{
			return Values().TryGetValue(key, out string value) ? value : null;
		}
	}

	public static void Set(string key, string value)
	{
		lock (Gate)
		{
			Values()[key] = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
				File.WriteAllLines(FilePath, _cache.Select(p => p.Key + "=" + p.Value), Encoding.UTF8);
			}
			catch (Exception)
			{
			}
		}
	}
}
