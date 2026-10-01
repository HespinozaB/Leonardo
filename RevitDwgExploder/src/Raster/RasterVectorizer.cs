using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitDwgExploder.Raster;

/// <summary>Segmento recto vectorizado (píxeles, origen arriba a la izquierda como en la imagen).</summary>
internal sealed class RasterLine
{
	public double X1;
	public double Y1;
	public double X2;
	public double Y2;
	public byte R;
	public byte G;
	public byte B;

	/// <summary>Grosor estimado del trazo en píxeles.</summary>
	public double Thickness;
}

/// <summary>Mancha de color sólido vectorizada (contorno exterior en píxeles).</summary>
internal sealed class RasterFill
{
	public List<(double X, double Y)> Outline = new List<(double, double)>();
	public byte R;
	public byte G;
	public byte B;
}

internal sealed class RasterDrawing
{
	public int Width;
	public int Height;
	public List<RasterLine> Lines = new List<RasterLine>();
	public List<RasterFill> Fills = new List<RasterFill>();
}

/// <summary>Zona rectangular a ignorar al buscar líneas (p.ej. donde el OCR encontró texto).</summary>
internal readonly struct PixelRect
{
	public readonly int X1;
	public readonly int Y1;
	public readonly int X2;
	public readonly int Y2;

	public PixelRect(int x1, int y1, int x2, int y2)
	{
		X1 = Math.Min(x1, x2);
		Y1 = Math.Min(y1, y2);
		X2 = Math.Max(x1, x2);
		Y2 = Math.Max(y1, y2);
	}
}

/// <summary>
/// Vectoriza un plano en imagen (raster): separa el fondo, convierte las manchas grandes de color en rellenos y la
/// "tinta" en segmentos rectos (esqueletizado de Zhang-Suen, seguimiento de trazos y simplificación de
/// Douglas-Peucker). Trabaja sobre un array ARGB, sin dependencias de Windows ni de Revit.
/// </summary>
internal static class RasterVectorizer
{
	private const byte Background = 0;
	private const byte Ink = 1;
	private const byte FillPixel = 2;

	public static RasterDrawing Vectorize(int width, int height, uint[] argb, IEnumerable<PixelRect> ignore, double simplifyTolerance = 1.2)
	{
		var drawing = new RasterDrawing { Width = width, Height = height };
		int n = width * height;
		var luma = new byte[n];
		var colored = new bool[n];
		for (int i = 0; i < n; i++)
		{
			uint c = argb[i];
			int a = (int)(c >> 24), r = (int)((c >> 16) & 255), g = (int)((c >> 8) & 255), b = (int)(c & 255);
			if (a < 128)
			{
				r = g = b = 255; // transparente = fondo
			}

			luma[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
			int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
			colored[i] = max > 40 && (max - min) > 60; // saturado: rojo, amarillo, azul…
		}

		// Tinta = todo lo claramente más oscuro que el fondo (así también entran las líneas grises claras de muros o
		// rejillas, que el umbral de Otsu dejaría como fondo).
		int background = BackgroundLevel(luma);
		int threshold = Math.Max(Math.Max(90, Math.Min(200, OtsuThreshold(luma))), background - 45);
		var cls = new byte[n];
		for (int i = 0; i < n; i++)
		{
			cls[i] = luma[i] < threshold || colored[i] ? Ink : Background;
		}

		foreach (PixelRect rect in ignore ?? Enumerable.Empty<PixelRect>())
		{
			for (int y = Math.Max(0, rect.Y1); y <= Math.Min(height - 1, rect.Y2); y++)
			{
				for (int x = Math.Max(0, rect.X1); x <= Math.Min(width - 1, rect.X2); x++)
				{
					cls[y * width + x] = Background;
				}
			}
		}

		// Grosor del trazo en cada píxel de tinta (distancia al fondo).
		int[] distance = DistanceToBackground(cls, width, height);

		ExtractFills(drawing, cls, distance, argb, luma, threshold, width, height);
		ExtractLines(drawing, cls, distance, argb, width, height, simplifyTolerance);
		return drawing;
	}

	// ------------------------------------------------------------------ Umbral

	/// <summary>Luminosidad más frecuente entre los píxeles claros (el color del papel).</summary>
	private static int BackgroundLevel(byte[] luma)
	{
		var histogram = new long[256];
		foreach (byte v in luma)
		{
			histogram[v]++;
		}

		int best = 255;
		for (int v = 128; v < 256; v++)
		{
			if (histogram[v] > histogram[best])
			{
				best = v;
			}
		}

		return best;
	}

	private static int OtsuThreshold(byte[] luma)
	{
		var histogram = new long[256];
		foreach (byte v in luma)
		{
			histogram[v]++;
		}

		long total = luma.Length;
		double sum = 0;
		for (int i = 0; i < 256; i++)
		{
			sum += i * (double)histogram[i];
		}

		double sumB = 0, best = 0;
		long wB = 0;
		int threshold = 128;
		for (int t = 0; t < 256; t++)
		{
			wB += histogram[t];
			if (wB == 0)
			{
				continue;
			}

			long wF = total - wB;
			if (wF == 0)
			{
				break;
			}

			sumB += t * (double)histogram[t];
			double mB = sumB / wB, mF = (sum - sumB) / wF;
			double between = (double)wB * wF * (mB - mF) * (mB - mF);
			if (between > best)
			{
				best = between;
				threshold = t;
			}
		}

		return threshold;
	}

	/// <summary>Distancia (chaflán 3-4, en píxeles ×3) de cada píxel de tinta al fondo más cercano.</summary>
	private static int[] DistanceToBackground(byte[] cls, int w, int h)
	{
		var d = new int[w * h];
		const int inf = int.MaxValue / 4;
		for (int i = 0; i < d.Length; i++)
		{
			d[i] = cls[i] == Background ? 0 : inf;
		}

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int i = y * w + x;
				if (d[i] == 0)
				{
					continue;
				}

				int v = d[i];
				if (x > 0) v = Math.Min(v, d[i - 1] + 3);
				if (y > 0)
				{
					v = Math.Min(v, d[i - w] + 3);
					if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
					if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
				}

				d[i] = v;
			}
		}

		for (int y = h - 1; y >= 0; y--)
		{
			for (int x = w - 1; x >= 0; x--)
			{
				int i = y * w + x;
				if (d[i] == 0)
				{
					continue;
				}

				int v = d[i];
				if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
				if (y < h - 1)
				{
					v = Math.Min(v, d[i + w] + 3);
					if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
					if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
				}

				d[i] = v;
			}
		}

		return d;
	}

	// ------------------------------------------------------------------ Rellenos

	/// <summary>
	/// Manchas de tinta "gruesas" (el interior queda lejos del borde) y de color uniforme: se convierten en rellenos y
	/// se quitan de la tinta. Las líneas, aunque sean de color, son finas y siguen como líneas.
	/// </summary>
	private static void ExtractFills(RasterDrawing drawing, byte[] cls, int[] distance, uint[] argb, byte[] luma, int threshold, int w, int h)
	{
		int minArea = Math.Max(150, w * h / 4000);
		const int thickCore = 3 * 5; // al menos ~5 px hasta el borde: no es una línea
		var label = new int[w * h];
		var queue = new Queue<int>();
		int next = 0;
		for (int start = 0; start < label.Length; start++)
		{
			if (cls[start] != Ink || label[start] != 0 || distance[start] < thickCore)
			{
				continue;
			}

			// Región: píxeles de tinta conectados con color parecido al del núcleo.
			uint seed = argb[start];
			next++;
			var pixels = new List<int>();
			long sr = 0, sg = 0, sb = 0;
			queue.Enqueue(start);
			label[start] = next;
			while (queue.Count > 0)
			{
				int i = queue.Dequeue();
				pixels.Add(i);
				uint c = argb[i];
				sr += (c >> 16) & 255;
				sg += (c >> 8) & 255;
				sb += c & 255;
				int x = i % w, y = i / w;
				for (int k = 0; k < 4; k++)
				{
					int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
					if (nx < 0 || ny < 0 || nx >= w || ny >= h)
					{
						continue;
					}

					int j = ny * w + nx;
					if (label[j] == 0 && cls[j] == Ink && ColorDistance(argb[j], seed) < 90)
					{
						label[j] = next;
						queue.Enqueue(j);
					}
				}
			}

			if (pixels.Count < minArea)
			{
				continue;
			}

			// Grosor medio de la mancha: una línea larga y fina no es un relleno.
			double meanDistance = pixels.Average(i => distance[i]) / 3.0;
			if (meanDistance < 3.0)
			{
				continue;
			}

			byte r = (byte)(sr / pixels.Count), g = (byte)(sg / pixels.Count), b = (byte)(sb / pixels.Count);
			List<(double, double)> outline = TraceOutline(label, next, w, h, pixels[0]);
			if (outline.Count < 3)
			{
				continue;
			}

			drawing.Fills.Add(new RasterFill { Outline = Simplify(outline, 1.0, closed: true), R = r, G = g, B = b });
			foreach (int i in pixels)
			{
				cls[i] = FillPixel;
			}
		}

		// El borde que deja un relleno en la tinta (antialiasing) no debe convertirse en líneas; las líneas oscuras
		// pegadas a un relleno sí se conservan.
		int darkLimit = Math.Max(60, threshold - 60);
		for (int i = 0; i < cls.Length; i++)
		{
			if (cls[i] != Ink || luma[i] < darkLimit)
			{
				continue;
			}

			int x = i % w, y = i / w;
			int fillNeighbours = 0, inkNeighbours = 0;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					int nx = x + dx, ny = y + dy;
					if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h)
					{
						continue;
					}

					byte c = cls[ny * w + nx];
					if (c == FillPixel) fillNeighbours++;
					else if (c == Ink) inkNeighbours++;
				}
			}

			if (fillNeighbours >= 3 && inkNeighbours <= 2)
			{
				cls[i] = Background;
			}
		}
	}

	private static int ColorDistance(uint a, uint b)
	{
		int dr = (int)((a >> 16) & 255) - (int)((b >> 16) & 255);
		int dg = (int)((a >> 8) & 255) - (int)((b >> 8) & 255);
		int db = (int)(a & 255) - (int)(b & 255);
		return Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db);
	}

	/// <summary>Contorno exterior de una región (seguimiento de borde de Moore), en sentido horario.</summary>
	private static List<(double, double)> TraceOutline(int[] label, int id, int w, int h, int anyPixel)
	{
		// Punto de partida: el píxel más a la izquierda de la fila superior de la región.
		int start = -1;
		for (int i = 0; i < label.Length && start < 0; i++)
		{
			if (label[i] == id)
			{
				start = i;
			}
		}

		var outline = new List<(double, double)>();
		if (start < 0)
		{
			return outline;
		}

		int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
		int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
		bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && label[y * w + x] == id;

		int sx = start % w, sy = start / w;
		int cx = sx, cy = sy, dir = 7;
		int guard = 0, limit = 4 * (w + h) * 8 + 1000000;
		do
		{
			outline.Add((cx + 0.5, cy + 0.5));
			int searchStart = (dir + 6) % 8;
			bool moved = false;
			for (int k = 0; k < 8; k++)
			{
				int d = (searchStart + k) % 8;
				int nx = cx + dx[d], ny = cy + dy[d];
				if (Inside(nx, ny))
				{
					cx = nx;
					cy = ny;
					dir = d;
					moved = true;
					break;
				}
			}

			if (!moved)
			{
				break;
			}
		}
		while ((cx != sx || cy != sy) && ++guard < limit);

		return outline;
	}

	// ------------------------------------------------------------------ Líneas

	private static void ExtractLines(RasterDrawing drawing, byte[] cls, int[] distance, uint[] argb, int w, int h, double tolerance)
	{
		var skeleton = new bool[w * h];
		for (int i = 0; i < skeleton.Length; i++)
		{
			skeleton[i] = cls[i] == Ink;
		}

		Thin(skeleton, w, h);

		foreach (List<int> path in TracePaths(skeleton, w, h))
		{
			if (path.Count < 3)
			{
				continue;
			}

			var points = path.Select(i => ((double)(i % w), (double)(i / w))).ToList();
			List<(double X, double Y)> simplified = Simplify(points, tolerance, closed: false);

			// Color y grosor del trazo (medias sobre sus píxeles).
			long sr = 0, sg = 0, sb = 0;
			double thickness = 0;
			foreach (int i in path)
			{
				uint c = argb[i];
				sr += (c >> 16) & 255;
				sg += (c >> 8) & 255;
				sb += c & 255;
				thickness += Math.Max(1, distance[i]) * 2.0 / 3.0;
			}

			byte r = (byte)(sr / path.Count), g = (byte)(sg / path.Count), b = (byte)(sb / path.Count);
			thickness = Math.Max(1.0, thickness / path.Count - 1.0);
			for (int k = 0; k < simplified.Count - 1; k++)
			{
				var (x1, y1) = simplified[k];
				var (x2, y2) = simplified[k + 1];
				if (Math.Abs(x2 - x1) + Math.Abs(y2 - y1) < 2.0)
				{
					continue;
				}

				Straighten(ref x1, ref y1, ref x2, ref y2);
				drawing.Lines.Add(new RasterLine { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, R = r, G = g, B = b, Thickness = thickness });
			}
		}
	}

	/// <summary>Si el segmento es casi horizontal o vertical (±1.5°), se endereza.</summary>
	private static void Straighten(ref double x1, ref double y1, ref double x2, ref double y2)
	{
		double angle = Math.Atan2(y2 - y1, x2 - x1) * 180.0 / Math.PI;
		double a = Math.Abs(angle) % 180.0;
		if (a < 1.5 || a > 178.5)
		{
			double y = (y1 + y2) / 2.0;
			y1 = y2 = y;
		}
		else if (Math.Abs(a - 90.0) < 1.5)
		{
			double x = (x1 + x2) / 2.0;
			x1 = x2 = x;
		}
	}

	/// <summary>Adelgazamiento de Zhang-Suen: deja cada trazo con 1 píxel de ancho.</summary>
	private static void Thin(bool[] img, int w, int h)
	{
		var toClear = new List<int>();
		bool changed = true;
		int guard = 0;
		while (changed && guard++ < 200)
		{
			changed = false;
			for (int step = 0; step < 2; step++)
			{
				toClear.Clear();
				for (int y = 1; y < h - 1; y++)
				{
					for (int x = 1; x < w - 1; x++)
					{
						int i = y * w + x;
						if (!img[i])
						{
							continue;
						}

						bool p2 = img[i - w], p3 = img[i - w + 1], p4 = img[i + 1], p5 = img[i + w + 1];
						bool p6 = img[i + w], p7 = img[i + w - 1], p8 = img[i - 1], p9 = img[i - w - 1];
						int neighbours = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
						if (neighbours < 2 || neighbours > 6)
						{
							continue;
						}

						int transitions = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0)
							+ (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
						if (transitions != 1)
						{
							continue;
						}

						if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8))
						{
							continue;
						}

						toClear.Add(i);
					}
				}

				foreach (int i in toClear)
				{
					img[i] = false;
				}

				changed |= toClear.Count > 0;
			}
		}
	}

	/// <summary>
	/// Recorre el esqueleto en caminos: de extremo/cruce a extremo/cruce, y luego los anillos cerrados.
	/// Los extremos y cruces se detectan por el número de transiciones fondo→tinta alrededor del píxel (no por el
	/// número de vecinos), así los "escalones" de las líneas inclinadas no se confunden con cruces.
	/// </summary>
	private static List<List<int>> TracePaths(bool[] sk, int w, int h)
	{
		// Vecinos en orden circular: N, NE, E, SE, S, SO, O, NO (los pares son 4-vecinos).
		int[] ox = { 0, 1, 1, 1, 0, -1, -1, -1 };
		int[] oy = { -1, -1, 0, 1, 1, 1, 0, -1 };
		bool At(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && sk[y * w + x];

		bool IsNode(int i)
		{
			int x = i % w, y = i / w, transitions = 0, count = 0;
			for (int k = 0; k < 8; k++)
			{
				bool a = At(x + ox[k], y + oy[k]);
				bool b = At(x + ox[(k + 1) % 8], y + oy[(k + 1) % 8]);
				if (!a && b)
				{
					transitions++;
				}

				if (a)
				{
					count++;
				}
			}

			return transitions != 2 || count == 1;
		}

		List<int> Neighbours(int i)
		{
			int x = i % w, y = i / w;
			var list = new List<int>(8);
			// Primero los 4-vecinos: así el recorrido sigue la escalera sin saltarse píxeles.
			for (int pass = 0; pass < 2; pass++)
			{
				for (int k = pass; k < 8; k += 2)
				{
					if (At(x + ox[k], y + oy[k]))
					{
						list.Add((y + oy[k]) * w + x + ox[k]);
					}
				}
			}

			return list;
		}

		var isNode = new bool[sk.Length];
		var nodes = new List<int>();
		for (int i = 0; i < sk.Length; i++)
		{
			if (sk[i] && IsNode(i))
			{
				isNode[i] = true;
				nodes.Add(i);
			}
		}

		var used = new bool[sk.Length];
		var paths = new List<List<int>>();

		List<int> Walk(int from, int first)
		{
			var path = new List<int> { from, first };
			int previous = from, current = first;
			while (!isNode[current])
			{
				used[current] = true;
				int next = -1;
				foreach (int candidate in Neighbours(current))
				{
					if (candidate != previous && (isNode[candidate] ? candidate != from || path.Count > 3 : !used[candidate]))
					{
						next = candidate;
						break;
					}
				}

				if (next < 0)
				{
					break;
				}

				previous = current;
				current = next;
				path.Add(current);
			}

			return path;
		}

		foreach (int node in nodes)
		{
			foreach (int first in Neighbours(node))
			{
				if (isNode[first] || used[first])
				{
					continue;
				}

				paths.Add(Walk(node, first));
			}
		}

		// Anillos sin extremos ni cruces (p.ej. un círculo aislado): se parte de cualquier píxel libre.
		for (int i = 0; i < sk.Length; i++)
		{
			if (!sk[i] || used[i] || isNode[i])
			{
				continue;
			}

			used[i] = true;
			isNode[i] = true; // el punto de partida hace de nodo para cerrar el anillo
			List<int> neighbours = Neighbours(i).Where(j => !used[j]).ToList();
			if (neighbours.Count > 0)
			{
				paths.Add(Walk(i, neighbours[0]));
			}
		}

		return paths;
	}

	/// <summary>Douglas-Peucker.</summary>
	private static List<(double X, double Y)> Simplify(List<(double X, double Y)> points, double tolerance, bool closed)
	{
		if (points.Count < 3)
		{
			return points.ToList();
		}

		var keep = new bool[points.Count];
		keep[0] = keep[points.Count - 1] = true;
		var stack = new Stack<(int, int)>();
		stack.Push((0, points.Count - 1));
		while (stack.Count > 0)
		{
			var (a, b) = stack.Pop();
			double maxDistance = 0;
			int index = -1;
			for (int i = a + 1; i < b; i++)
			{
				double d = PointLineDistance(points[i], points[a], points[b]);
				if (d > maxDistance)
				{
					maxDistance = d;
					index = i;
				}
			}

			if (index >= 0 && maxDistance > tolerance)
			{
				keep[index] = true;
				stack.Push((a, index));
				stack.Push((index, b));
			}
		}

		var result = new List<(double, double)>();
		for (int i = 0; i < points.Count; i++)
		{
			if (keep[i])
			{
				result.Add(points[i]);
			}
		}

		if (closed && result.Count > 1 && result[0].Equals(result[result.Count - 1]))
		{
			result.RemoveAt(result.Count - 1);
		}

		return result;
	}

	private static double PointLineDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
	{
		double dx = b.X - a.X, dy = b.Y - a.Y;
		double len2 = dx * dx + dy * dy;
		if (len2 < 1E-12)
		{
			return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
		}

		double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
		double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
		return Math.Sqrt(ex * ex + ey * ey);
	}
}
