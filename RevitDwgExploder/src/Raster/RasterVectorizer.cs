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

	/// <summary>Huecos (contornos interiores).</summary>
	public List<List<(double X, double Y)>> Holes = new List<List<(double X, double Y)>>();
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

	/// <summary>Clasificación final de cada píxel (0 fondo, 1 tinta/línea, 2 relleno).</summary>
	public byte[] Classes;
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
	/// <summary>Escala de píxel de la vectorización en curso (ver <c>pixelScale</c> en <see cref="Vectorize"/>).</summary>
	[ThreadStatic]
	private static double S;

	private const byte Background = 0;
	private const byte Ink = 1;
	private const byte FillPixel = 2;

	/// <param name="minSegment">Largo mínimo (píxeles) de un lado de relleno: el tramo más corto que admite Revit.</param>
	/// <param name="pixelScale">
	/// Ampliación con la que se trabaja respecto a la imagen original (2 si se amplió una imagen de baja resolución): los
	/// tamaños mínimos (grosores, motas, rachas) se escalan para que el resultado no dependa de la ampliación.
	/// </param>
	public static RasterDrawing Vectorize(int width, int height, uint[] argb, IEnumerable<PixelRect> ignore, double simplifyTolerance = 1.2,
		double minSegment = 0.0, double pixelScale = 1.0)
	{
		S = Math.Max(1.0, pixelScale);
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

		var ignored = new bool[n];
		foreach (PixelRect rect in ignore ?? Enumerable.Empty<PixelRect>())
		{
			for (int y = Math.Max(0, rect.Y1); y <= Math.Min(height - 1, rect.Y2); y++)
			{
				for (int x = Math.Max(0, rect.X1); x <= Math.Min(width - 1, rect.X2); x++)
				{
					cls[y * width + x] = Background;
					ignored[y * width + x] = true;
				}
			}
		}

		// Grosor del trazo en cada píxel de tinta (distancia al fondo).
		int[] distance = DistanceToBackground(cls, width, height);

		ExtractTints(drawing, cls, ignored, argb, luma, background, width, height, minSegment);
		ExtractFills(drawing, cls, distance, argb, luma, threshold, width, height, minSegment);
		RemoveSpecks(cls, width, height, (int)Math.Round(6 * S * S));
		drawing.Classes = cls;
		ExtractStraightRuns(drawing, cls, argb, width, height);
		ExtractLines(drawing, cls, distance, argb, width, height, simplifyTolerance * S);
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
	private static void ExtractFills(RasterDrawing drawing, byte[] cls, int[] distance, uint[] argb, byte[] luma, int threshold, int w, int h, double minSegment)
	{
		// Núcleos "gruesos" (≥5 px al borde) de cualquier color, y núcleos medianos (≥2.3 px) de color o gris no oscuro:
		// así entran también las muestras de color pequeñas (leyendas) sin convertir en relleno las líneas negras.
		int minAreaThick = Math.Max((int)(150 * S * S), w * h / 4000);
		int minAreaMedium = Math.Max((int)(40 * S * S), w * h / 60000);
		int thickCore = (int)Math.Round(3 * 5 * S);
		int mediumCore = (int)Math.Round(7 * S);
		int maxGap = (int)Math.Round(3 * S);
		var label = new int[w * h];
		var gapDepth = new byte[w * h];
		var component = new int[w * h];
		var queue = new Queue<int>();
		int next = 0, nextComponent = 0;
		for (int start = 0; start < label.Length; start++)
		{
			if (cls[start] != Ink || label[start] != 0 || distance[start] < mediumCore)
			{
				continue;
			}

			uint seed = argb[start];
			bool dark = luma[start] < 80 && !IsColored(seed);
			bool thick = distance[start] >= thickCore;
			if (!thick && luma[start] < 140 && !IsColored(seed))
			{
				continue;
			}

			// Región: píxeles de tinta conectados con color parecido al de la semilla. Si la mancha no es oscura, se
			// puede cruzar una línea fina de otro color (rayado de una muestra, juntas de un muro): esos píxeles
			// "puente" unen la región pero siguen siendo líneas.
			next++;
			var similar = new List<int>();
			var gaps = new List<int>();
			queue.Enqueue(start);
			label[start] = next;
			gapDepth[start] = 0;
			while (queue.Count > 0)
			{
				int i = queue.Dequeue();
				if (gapDepth[i] == 0)
				{
					similar.Add(i);
				}
				else
				{
					gaps.Add(i);
				}

				int x = i % w, y = i / w;
				for (int k = 0; k < 4; k++)
				{
					int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
					if (nx < 0 || ny < 0 || nx >= w || ny >= h)
					{
						continue;
					}

					int j = ny * w + nx;
					if (label[j] != 0 || cls[j] != Ink)
					{
						continue;
					}

					if (ColorDistance(argb[j], seed) < 64)
					{
						label[j] = next;
						gapDepth[j] = 0;
						queue.Enqueue(j);
					}
					else if (!dark && gapDepth[i] < maxGap)
					{
						label[j] = next;
						gapDepth[j] = (byte)(gapDepth[i] + 1);
						queue.Enqueue(j);
					}
				}
			}

			// Un puente solo vale si tiene la región a ambos lados (si no, es el borde de la mancha).
			foreach (int i in gaps)
			{
				if (!Bridged(label, gapDepth, next, i, w, h))
				{
					label[i] = 0;
					gapDepth[i] = 0;
				}
			}

			int minArea = thick ? minAreaThick : minAreaMedium;
			if (similar.Count < minArea)
			{
				foreach (int i in gaps)
				{
					label[i] = 0;
				}

				continue;
			}

			// Cada parte conectada de la región es un relleno (contorno exterior + huecos).
			foreach (int s in similar)
			{
				if (component[s] != 0)
				{
					continue;
				}

				nextComponent++;
				List<int> part = FloodComponent(label, next, component, nextComponent, s, w, h);
				var partSimilar = part.Where(i => gapDepth[i] == 0).ToList();
				if (partSimilar.Count < minArea)
				{
					continue;
				}

				double meanDistance = partSimilar.Average(i => distance[i]) / 3.0;
				if (meanDistance < (thick ? 3.0 : 2.0) * S)
				{
					continue;
				}

				// Las manchas medianas deben ser compactas (muestras, bloques): las letras o los trazos gruesos ocupan
				// poco de su rectángulo.
				if (!thick)
				{
					int bx0 = part.Min(i => i % w), bx1 = part.Max(i => i % w);
					int by0 = part[0] / w, by1 = part.Max(i => i / w);
					by0 = part.Min(i => i / w);
					double solidity = part.Count / (double)((bx1 - bx0 + 1) * (by1 - by0 + 1));

					// Una mancha mediana de menos de 8 px de lado suele ser texto pequeño borroso, no una muestra.
					if (solidity < 0.55 || Math.Min(bx1 - bx0, by1 - by0) + 1 < 8 * S)
					{
						continue;
					}
				}

				long sr = 0, sg = 0, sb = 0;
				foreach (int i in partSimilar)
				{
					uint c = argb[i];
					sr += (c >> 16) & 255;
					sg += (c >> 8) & 255;
					sb += c & 255;
				}

				EmitShapes(drawing, part, w, minArea, minSegment,
					(byte)(sr / partSimilar.Count), (byte)(sg / partSimilar.Count), (byte)(sb / partSimilar.Count));
				foreach (int i in partSimilar)
				{
					cls[i] = FillPixel;
				}
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

	/// <summary>Quita las motas de tinta aisladas (ruido, compresión JPG) de menos de <paramref name="minPixels"/> píxeles.</summary>
	private static void RemoveSpecks(byte[] cls, int w, int h, int minPixels)
	{
		var seen = new bool[cls.Length];
		var stack = new Stack<int>();
		var part = new List<int>();
		for (int start = 0; start < cls.Length; start++)
		{
			if (cls[start] != Ink || seen[start])
			{
				continue;
			}

			part.Clear();
			stack.Push(start);
			seen[start] = true;
			while (stack.Count > 0)
			{
				int i = stack.Pop();
				part.Add(i);
				int x = i % w, y = i / w;
				for (int dy = -1; dy <= 1; dy++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						int nx = x + dx, ny = y + dy;
						if (nx < 0 || ny < 0 || nx >= w || ny >= h)
						{
							continue;
						}

						int j = ny * w + nx;
						if (!seen[j] && cls[j] == Ink)
						{
							seen[j] = true;
							stack.Push(j);
						}
					}
				}
			}

			if (part.Count < minPixels)
			{
				foreach (int i in part)
				{
					cls[i] = Background;
				}
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

	private static bool IsColored(uint c)
	{
		int r = (int)((c >> 16) & 255), g = (int)((c >> 8) & 255), b = (int)(c & 255);
		int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
		return max > 40 && max - min > 60;
	}

	/// <summary>
	/// Sombreados claros: zonas amplias de gris o color pálido algo más oscuras que el papel (quedan por encima del
	/// umbral de tinta). Se convierten en rellenos debajo de todo; las líneas no se tocan.
	/// </summary>
	private static void ExtractTints(RasterDrawing drawing, byte[] cls, bool[] ignored, uint[] argb, byte[] luma, int background, int w, int h, double minSegment)
	{
		int n = w * h;
		var mask = new byte[n];
		for (int i = 0; i < n; i++)
		{
			mask[i] = cls[i] == Background && !ignored[i] && luma[i] >= 90 && luma[i] <= background - 12 ? Ink : Background;
		}

		int[] distance = DistanceToBackground(mask, w, h);
		int minArea = Math.Max((int)(150 * S * S), n / 8000);
		var label = new int[n];
		var stack = new Stack<int>();
		int next = 0;
		for (int start = 0; start < n; start++)
		{
			if (mask[start] != Ink || label[start] != 0 || distance[start] < 9 * S)
			{
				continue;
			}

			next++;
			uint seed = argb[start];
			var part = new List<int>();
			stack.Push(start);
			label[start] = next;
			long sr = 0, sg = 0, sb = 0, sumDistance = 0;
			while (stack.Count > 0)
			{
				int i = stack.Pop();
				part.Add(i);
				uint c = argb[i];
				sr += (c >> 16) & 255;
				sg += (c >> 8) & 255;
				sb += c & 255;
				sumDistance += distance[i];
				int x = i % w, y = i / w;
				if (x > 0) TryAdd(i - 1);
				if (x < w - 1) TryAdd(i + 1);
				if (y > 0) TryAdd(i - w);
				if (y < h - 1) TryAdd(i + w);
			}

			void TryAdd(int j)
			{
				if (label[j] == 0 && mask[j] == Ink && ColorDistance(argb[j], seed) < 54)
				{
					label[j] = next;
					stack.Push(j);
				}
			}

			if (part.Count < minArea || sumDistance / 3.0 / part.Count < 2.2 * S)
			{
				continue;
			}

			EmitShapes(drawing, part, w, minArea, minSegment, (byte)(sr / part.Count), (byte)(sg / part.Count), (byte)(sb / part.Count));
		}
	}

	/// <summary>¿El píxel puente tiene píxeles de la región (no puentes) a ambos lados, en alguna dirección?</summary>
	private static bool Bridged(int[] label, byte[] gapDepth, int id, int i, int w, int h)
	{
		int x = i % w, y = i / w;
		bool Member(int px, int py) => px >= 0 && py >= 0 && px < w && py < h && label[py * w + px] == id && gapDepth[py * w + px] == 0;
		int[,] dirs = { { 1, 0 }, { 0, 1 }, { 1, 1 }, { 1, -1 } };
		for (int d = 0; d < 4; d++)
		{
			int dx = dirs[d, 0], dy = dirs[d, 1];
			bool plus = false, minus = false;
			for (int s = 1; s <= 4 && !plus; s++)
			{
				plus = Member(x + dx * s, y + dy * s);
			}

			for (int s = 1; s <= 4 && !minus; s++)
			{
				minus = Member(x - dx * s, y - dy * s);
			}

			if (plus && minus)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Píxeles conectados (8 vecinos) de la región <paramref name="id"/>, marcados en <paramref name="component"/>.</summary>
	private static List<int> FloodComponent(int[] label, int id, int[] component, int componentId, int start, int w, int h)
	{
		var result = new List<int>();
		var stack = new Stack<int>();
		stack.Push(start);
		component[start] = componentId;
		while (stack.Count > 0)
		{
			int i = stack.Pop();
			result.Add(i);
			int x = i % w, y = i / w;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					int nx = x + dx, ny = y + dy;
					if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h)
					{
						continue;
					}

					int j = ny * w + nx;
					if (component[j] == 0 && label[j] == id)
					{
						component[j] = componentId;
						stack.Push(j);
					}
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Convierte una mancha en rellenos válidos para Revit: quita las partes de 1 px de ancho (apertura 2×2), cierra
	/// los "pellizcos" diagonales (dos píxeles que solo se tocan por la esquina harían que el contorno se toque a sí
	/// mismo), separa las partes conectadas y traza el contorno y los huecos de cada una. Los polígonos simplificados
	/// se comprueban (sin cruces ni toques) y, si hace falta, se usa el contorno sin simplificar.
	/// </summary>
	private static void EmitShapes(RasterDrawing drawing, List<int> part, int w, int minArea, double minSegment, byte r, byte g, byte b)
	{
		int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
		foreach (int i in part)
		{
			int x = i % w, y = i / w;
			x0 = Math.Min(x0, x);
			y0 = Math.Min(y0, y);
			x1 = Math.Max(x1, x);
			y1 = Math.Max(y1, y);
		}

		const int m = 2;
		int lw = x1 - x0 + 1 + 2 * m, lh = y1 - y0 + 1 + 2 * m;
		var mask = new bool[lw * lh];
		foreach (int i in part)
		{
			mask[(i / w - y0 + m) * lw + (i % w - x0 + m)] = true;
		}

		// 1) Apertura 2×2: solo quedan los píxeles que forman parte de algún bloque 2×2 lleno.
		var opened = new bool[mask.Length];
		for (int y = 0; y < lh - 1; y++)
		{
			for (int x = 0; x < lw - 1; x++)
			{
				int i = y * lw + x;
				if (mask[i] && mask[i + 1] && mask[i + lw] && mask[i + lw + 1])
				{
					opened[i] = opened[i + 1] = opened[i + lw] = opened[i + lw + 1] = true;
				}
			}
		}

		mask = opened;

		// 2) Pellizcos diagonales: se rellena uno de los dos píxeles que faltan.
		for (int pass = 0; pass < 6; pass++)
		{
			bool changed = false;
			for (int y = 0; y < lh - 1; y++)
			{
				for (int x = 0; x < lw - 1; x++)
				{
					int i = y * lw + x;
					bool a = mask[i], bR = mask[i + 1], c = mask[i + lw], d = mask[i + lw + 1];
					if (a && d && !bR && !c)
					{
						mask[i + 1] = true;
						changed = true;
					}
					else if (bR && c && !a && !d)
					{
						mask[i] = true;
						changed = true;
					}
				}
			}

			if (!changed)
			{
				break;
			}
		}

		// 3) Cada parte (4 vecinos) con su contorno y sus huecos.
		var comp = new int[mask.Length];
		int next = 0;
		for (int start = 0; start < mask.Length; start++)
		{
			if (!mask[start] || comp[start] != 0)
			{
				continue;
			}

			next++;
			int id = next;
			int count = Flood4(start, lw, lh, j => mask[j] && comp[j] == 0, j => comp[j] = id);
			if (count < minArea)
			{
				continue;
			}

			List<(double X, double Y)> outline = FinalizeLoop(
				TraceOutline((x, y) => x >= 0 && y >= 0 && x < lw && y < lh && comp[y * lw + x] == id, start % lw, start / lw),
				x0 - m, y0 - m, minSegment);
			if (outline == null)
			{
				continue;
			}

			var fill = new RasterFill { Outline = outline, R = r, G = g, B = b };

			// Exterior: todo lo que no es la parte y se alcanza desde el borde de la rejilla.
			var outside = new bool[mask.Length];
			Flood4(0, lw, lh, j => comp[j] != id && !outside[j], j => outside[j] = true);
			var holeId = new int[mask.Length];
			int nextHole = 0;
			for (int hs = 0; hs < mask.Length; hs++)
			{
				if (comp[hs] == id || outside[hs] || holeId[hs] != 0)
				{
					continue;
				}

				nextHole++;
				int hid = nextHole;
				int holeCount = Flood4(hs, lw, lh, j => comp[j] != id && !outside[j] && holeId[j] == 0, j => holeId[j] = hid);
				if (holeCount < Math.Max(4, minArea / 4))
				{
					continue;
				}

				List<(double X, double Y)> hole = FinalizeLoop(
					TraceOutline((x, y) => x >= 0 && y >= 0 && x < lw && y < lh && holeId[y * lw + x] == hid, hs % lw, hs / lw),
					x0 - m, y0 - m, minSegment);
				if (hole != null && !Crosses(hole, fill.Outline) && fill.Holes.All(other => !Crosses(hole, other)))
				{
					fill.Holes.Add(hole);
				}
			}

			drawing.Fills.Add(fill);
		}
	}

	/// <summary>Relleno por 4 vecinos desde <paramref name="start"/>; devuelve cuántos píxeles marcó.</summary>
	private static int Flood4(int start, int lw, int lh, Func<int, bool> accept, Action<int> mark)
	{
		if (!accept(start))
		{
			return 0;
		}

		var stack = new Stack<int>();
		mark(start);
		stack.Push(start);
		int count = 0;
		while (stack.Count > 0)
		{
			int i = stack.Pop();
			count++;
			int x = i % lw, y = i / lw;
			if (x > 0 && accept(i - 1)) { mark(i - 1); stack.Push(i - 1); }
			if (x < lw - 1 && accept(i + 1)) { mark(i + 1); stack.Push(i + 1); }
			if (y > 0 && accept(i - lw)) { mark(i - lw); stack.Push(i - lw); }
			if (y < lh - 1 && accept(i + lw)) { mark(i + lw); stack.Push(i + lw); }
		}

		return count;
	}

	/// <summary>
	/// Contorno trazado → polígono final en coordenadas de la imagen: simplificado si sigue siendo simple (sin cruces),
	/// con lados de al menos <paramref name="minSegment"/>; si no, el contorno sin simplificar; null si no sirve.
	/// </summary>
	private static List<(double X, double Y)> FinalizeLoop(List<(double, double)> raw, int dx, int dy, double minSegment)
	{
		if (raw.Count < 4)
		{
			return null;
		}

		List<(double X, double Y)> points = raw.Select(p => (p.Item1 + dx, p.Item2 + dy)).ToList();
		foreach (double tolerance in new[] { 1.0, 0.6, 0.0 })
		{
			List<(double X, double Y)> candidate = tolerance > 0 ? Simplify(points, tolerance, closed: true) : RemoveCollinear(points);
			candidate = EnforceMinSegment(candidate, minSegment);
			if (candidate.Count >= 3 && Math.Abs(SignedArea(candidate)) > 1.0 && IsSimple(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	private static List<(double X, double Y)> RemoveCollinear(List<(double X, double Y)> points)
	{
		var result = new List<(double X, double Y)>();
		int n = points.Count;
		for (int i = 0; i < n; i++)
		{
			var prev = points[(i - 1 + n) % n];
			var cur = points[i];
			var next = points[(i + 1) % n];
			double cross = (cur.X - prev.X) * (next.Y - cur.Y) - (cur.Y - prev.Y) * (next.X - cur.X);
			if (Math.Abs(cross) > 1E-9)
			{
				result.Add(cur);
			}
		}

		return result;
	}

	private static List<(double X, double Y)> EnforceMinSegment(List<(double X, double Y)> points, double minSegment)
	{
		if (minSegment <= 1.0 || points.Count < 3)
		{
			return points;
		}

		var result = new List<(double X, double Y)> { points[0] };
		for (int i = 1; i < points.Count; i++)
		{
			var last = result[result.Count - 1];
			if (Math.Sqrt((points[i].X - last.X) * (points[i].X - last.X) + (points[i].Y - last.Y) * (points[i].Y - last.Y)) >= minSegment)
			{
				result.Add(points[i]);
			}
		}

		while (result.Count > 2)
		{
			var a = result[result.Count - 1];
			var b = result[0];
			if (Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) >= minSegment)
			{
				break;
			}

			result.RemoveAt(result.Count - 1);
		}

		return result;
	}

	private static double SignedArea(List<(double X, double Y)> points)
	{
		double area = 0;
		for (int i = 0; i < points.Count; i++)
		{
			var a = points[i];
			var b = points[(i + 1) % points.Count];
			area += a.X * b.Y - b.X * a.Y;
		}

		return area / 2.0;
	}

	/// <summary>¿El polígono cerrado no se cruza ni se toca a sí mismo (ni tiene "picos" que vuelven atrás)?</summary>
	private static bool IsSimple(List<(double X, double Y)> p)
	{
		int n = p.Count;
		for (int i = 0; i < n; i++)
		{
			var a = p[(i - 1 + n) % n];
			var b = p[i];
			var c = p[(i + 1) % n];
			double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
			double dot = (b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y);
			if (Math.Abs(cross) < 1E-9 && dot < 0)
			{
				return false;
			}
		}

		for (int i = 0; i < n; i++)
		{
			var a1 = p[i];
			var a2 = p[(i + 1) % n];
			for (int j = i + 2; j < n; j++)
			{
				if (i == 0 && j == n - 1)
				{
					continue;
				}

				if (SegmentsTouch(a1, a2, p[j], p[(j + 1) % n]))
				{
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>¿Algún lado de un polígono toca o cruza algún lado del otro?</summary>
	private static bool Crosses(List<(double X, double Y)> a, List<(double X, double Y)> b)
	{
		for (int i = 0; i < a.Count; i++)
		{
			var a1 = a[i];
			var a2 = a[(i + 1) % a.Count];
			for (int j = 0; j < b.Count; j++)
			{
				if (SegmentsTouch(a1, a2, b[j], b[(j + 1) % b.Count]))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static bool SegmentsTouch((double X, double Y) p1, (double X, double Y) p2, (double X, double Y) q1, (double X, double Y) q2)
	{
		if (Math.Max(p1.X, p2.X) < Math.Min(q1.X, q2.X) - 1E-9 || Math.Max(q1.X, q2.X) < Math.Min(p1.X, p2.X) - 1E-9
			|| Math.Max(p1.Y, p2.Y) < Math.Min(q1.Y, q2.Y) - 1E-9 || Math.Max(q1.Y, q2.Y) < Math.Min(p1.Y, p2.Y) - 1E-9)
		{
			return false;
		}

		double d1 = Orient(q1, q2, p1), d2 = Orient(q1, q2, p2), d3 = Orient(p1, p2, q1), d4 = Orient(p1, p2, q2);
		if (((d1 > 1E-9 && d2 < -1E-9) || (d1 < -1E-9 && d2 > 1E-9)) && ((d3 > 1E-9 && d4 < -1E-9) || (d3 < -1E-9 && d4 > 1E-9)))
		{
			return true;
		}

		return (Math.Abs(d1) <= 1E-9 && OnSegment(q1, q2, p1)) || (Math.Abs(d2) <= 1E-9 && OnSegment(q1, q2, p2))
			|| (Math.Abs(d3) <= 1E-9 && OnSegment(p1, p2, q1)) || (Math.Abs(d4) <= 1E-9 && OnSegment(p1, p2, q2));
	}

	private static double Orient((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
		(b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

	private static bool OnSegment((double X, double Y) a, (double X, double Y) b, (double X, double Y) p) =>
		p.X >= Math.Min(a.X, b.X) - 1E-9 && p.X <= Math.Max(a.X, b.X) + 1E-9 && p.Y >= Math.Min(a.Y, b.Y) - 1E-9 && p.Y <= Math.Max(a.Y, b.Y) + 1E-9;

	/// <summary>
	/// Contorno exterior de una región (seguimiento de borde de Moore), empezando en su píxel superior izquierdo
	/// (<paramref name="sx"/>, <paramref name="sy"/>: el primero en orden de filas).
	/// </summary>
	private static List<(double, double)> TraceOutline(Func<int, int, bool> inside, int sx, int sy)
	{
		var outline = new List<(double, double)>();
		int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
		int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
		int cx = sx, cy = sy, dir = 7;
		int firstDir = -1;
		const int limit = 4000000;
		for (int guard = 0; guard < limit; guard++)
		{
			int searchStart = (dir + 6) % 8;
			int moveDir = -1;
			for (int k = 0; k < 8; k++)
			{
				int d = (searchStart + k) % 8;
				if (inside(cx + dx[d], cy + dy[d]))
				{
					moveDir = d;
					break;
				}
			}

			if (moveDir < 0)
			{
				outline.Add((cx + 0.5, cy + 0.5)); // píxel aislado
				break;
			}

			// Criterio de Jacob: se termina al volver al inicio saliendo en la misma dirección que la primera vez (si
			// solo se mirara el píxel de inicio, un cuello de 1 px cortaría el contorno a la mitad).
			if (cx == sx && cy == sy)
			{
				if (firstDir < 0)
				{
					firstDir = moveDir;
				}
				else if (moveDir == firstDir)
				{
					break;
				}
			}

			outline.Add((cx + 0.5, cy + 0.5));
			cx += dx[moveDir];
			cy += dy[moveDir];
			dir = moveDir;
		}

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
			if (path.Count < 4 * S)
			{
				continue;
			}

			var points = path.Select(i => ((double)(i % w), (double)(i / w))).ToList();
			List<(double X, double Y)> simplified = Simplify(points, tolerance, closed: false);

			// Color y grosor del trazo. El color de cada punto es el del píxel más oscuro a su alrededor: en una imagen
			// comprimida o de baja resolución una línea fina se ve más clara que la tinta real.
			long sr = 0, sg = 0, sb = 0;
			double thickness = 0;
			foreach (int i in path)
			{
				uint c = DarkestAround(argb, cls, i, w, h);
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

		drawing.Lines = RemoveStubs(MergeCollinear(drawing.Lines));
	}

	/// <summary>
	/// Líneas rectas a 0°, 90°, 45° y 135° (las de casi todo plano: muros, cotas, rayados) detectadas directamente
	/// como rachas largas de tinta: las rachas paralelas contiguas forman una banda (el grosor de la línea) y cada banda
	/// es un segmento limpio. Sus píxeles se quitan de la tinta; el resto (curvas, otros ángulos) sigue al esqueleto.
	/// Así los cruces de un rayado no parten las líneas en zigzag.
	/// </summary>
	private static void ExtractStraightRuns(RasterDrawing drawing, byte[] cls, uint[] argb, int w, int h)
	{
		// Largo mínimo: más que el trazo de una letra (las diagonales, más aún: las letras redondas tienen tramos a 45°).
		int minRunAxis = Math.Max(12, Math.Max(w, h) / 120);
		int minRunDiagonal = Math.Max(18, Math.Max(w, h) / 80);
		int maxThickness = (int)Math.Round(6 * S);
		// Píxeles ya usados por una línea de otra dirección: cuentan como tinta para que una línea siga a través de un
		// cruce, pero no se vuelven a usar.
		var taken = new bool[w * h];
		// (dx, dy): dirección de la racha. Primero horizontales y verticales.
		foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
		{
			var remove = new List<int>();
			int minRun = dx != 0 && dy != 0 ? minRunDiagonal : minRunAxis;
			int maxGap = dx != 0 && dy != 0 ? 1 : 0;

			// Posición (x, y) de un punto de la recta K en T, y comprobación de que la racha es de una línea fina: en una
			// fila que pasa por lo alto de unas letras o por el borde de una mancha, la tinta sigue en perpendicular
			// (los trazos de las letras) en buena parte del recorrido.
			(int X, int Y) At(int k, int t) => dy == 0 ? (t, k) : dx == 0 ? (k, t) : dy == 1 ? (t, t - k) : (t, k - t);
			int px = dy == 0 ? 0 : dx == 0 ? 1 : 1, py = dy == 0 ? 1 : dx == 0 ? 0 : (dy == 1 ? -1 : 1);
			bool InkXY(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && (cls[y * w + x] == Ink || taken[y * w + x]);
			bool IsThinRun(int k, int t0, int t1)
			{
				int thick = 0, count = 0;
				for (int t = t0; t <= t1; t++)
				{
					var (x, y) = At(k, t);
					if (!InkXY(x, y))
					{
						continue;
					}

					int extent = 1;
					for (int s2 = 1; s2 <= maxThickness + 2 && InkXY(x + px * s2, y + py * s2); s2++)
					{
						extent++;
					}

					for (int s2 = 1; s2 <= maxThickness + 2 && InkXY(x - px * s2, y - py * s2); s2++)
					{
						extent++;
					}

					count++;
					if (extent > maxThickness + 1)
					{
						thick++;
					}
				}

				return count > 0 && thick <= count * 0.3;
			}
			var runs = new List<(int K, int T0, int T1)>();
			// Recorre cada recta de la familia: se identifica por K y se avanza en T.
			int kMin, kMax;
			if (dy == 0) { kMin = 0; kMax = h - 1; }
			else if (dx == 0) { kMin = 0; kMax = w - 1; }
			else if (dy == 1) { kMin = -(h - 1); kMax = w - 1; }
			else { kMin = 0; kMax = w + h - 2; }

			for (int k = kMin; k <= kMax; k++)
			{
				int runStart = int.MinValue, lastInk = int.MinValue;
				for (int t = 0; t < Math.Max(w, h); t++)
				{
					int x, y;
					if (dy == 0) { x = t; y = k; }
					else if (dx == 0) { x = k; y = t; }
					else if (dy == 1) { x = t; y = t - k; }
					else { x = t; y = k - t; }

					if (x < 0 || y < 0 || x >= w || y >= h)
					{
						continue;
					}

					bool ink = cls[y * w + x] == Ink || taken[y * w + x];
					if (ink)
					{
						// Horizontales y verticales: sin huecos (una fila que pasa por lo alto de varias letras tiene huecos
						// de 1 px entre ellas); diagonales: hueco de 1 px (escalonado del antialiasing).
						if (runStart == int.MinValue || t - lastInk > maxGap + 1)
						{
							if (runStart != int.MinValue && lastInk - runStart + 1 >= minRun && IsThinRun(k, runStart, lastInk))
							{
								runs.Add((k, runStart, lastInk));
							}

							runStart = t;
						}

						lastInk = t;
					}
				}

				if (runStart != int.MinValue && lastInk - runStart + 1 >= minRun && IsThinRun(k, runStart, lastInk))
				{
					runs.Add((k, runStart, lastInk));
				}
			}

			// Bandas: rachas en rectas vecinas (K ±1) que se solapan al menos un 70 %.
			runs.Sort((a, b) => a.K != b.K ? a.K.CompareTo(b.K) : a.T0.CompareTo(b.T0));
			var parent = Enumerable.Range(0, runs.Count).ToArray();
			int Find(int q)
			{
				while (parent[q] != q)
				{
					parent[q] = parent[parent[q]];
					q = parent[q];
				}

				return q;
			}

			int firstOfPrevious = 0;
			for (int a = 0; a < runs.Count; a++)
			{
				while (firstOfPrevious < a && runs[firstOfPrevious].K < runs[a].K - 1)
				{
					firstOfPrevious++;
				}

				for (int b = firstOfPrevious; b < a; b++)
				{
					if (runs[b].K != runs[a].K - 1)
					{
						continue;
					}

					int overlap = Math.Min(runs[a].T1, runs[b].T1) - Math.Max(runs[a].T0, runs[b].T0) + 1;
					int shorter = Math.Min(runs[a].T1 - runs[a].T0, runs[b].T1 - runs[b].T0) + 1;
					if (overlap >= shorter * 0.7)
					{
						parent[Find(a)] = Find(b);
					}
				}
			}

			double diagonal = dx != 0 && dy != 0 ? Math.Sqrt(0.5) : 1.0;
			foreach (var band in Enumerable.Range(0, runs.Count).GroupBy(Find))
			{
				List<(int K, int T0, int T1)> members = band.Select(q => runs[q]).ToList();

				// Rachas principales: las largas (las de los bordes con antialiasing o el rayado vecino son más cortas).
				int longest = members.Max(m => m.T1 - m.T0);
				var main = members.Where(m => m.T1 - m.T0 >= longest * 0.6).ToList();
				int thicknessPx = main.Select(m => m.K).Distinct().Count();
				if (thicknessPx * diagonal > maxThickness)
				{
					continue; // mancha gruesa: no es una línea
				}

				int t0 = main.Min(m => m.T0), t1 = main.Max(m => m.T1);
				double kc = main.Sum(m => m.K * (double)(m.T1 - m.T0 + 1)) / main.Sum(m => (double)(m.T1 - m.T0 + 1));

				(double X, double Y) P(double t)
				{
					if (dy == 0) return (t, kc);
					if (dx == 0) return (kc, t);
					if (dy == 1) return (t, t - kc);
					return (t, kc - t);
				}

				var (x1, y1) = P(t0);
				var (x2, y2) = P(t1);
				// Solo se quitan los píxeles de la línea (rachas principales y su borde): una racha vecina que solo se unió
				// por tocarla (el alma de una viga bajo su ala) sigue siendo tinta para otras líneas.
				int mainLow = main.Min(m => m.K) - 1, mainHigh = main.Max(m => m.K) + 1;
				long sr = 0, sg = 0, sb = 0;
				int samples = 0;
				foreach (var m in members.Where(m => m.K >= mainLow && m.K <= mainHigh && m.T0 >= t0 - 2 && m.T1 <= t1 + 2))
				{
					for (int t = m.T0; t <= m.T1; t++)
					{
						int x, y;
						if (dy == 0) { x = t; y = m.K; }
						else if (dx == 0) { x = m.K; y = t; }
						else if (dy == 1) { x = t; y = t - m.K; }
						else { x = t; y = m.K - t; }

						if (x < 0 || y < 0 || x >= w || y >= h)
						{
							continue;
						}

						int i = y * w + x;
						if (cls[i] == Ink)
						{
							remove.Add(i);
							uint c = DarkestAround(argb, cls, i, w, h);
							sr += (c >> 16) & 255;
							sg += (c >> 8) & 255;
							sb += c & 255;
							samples++;
						}
					}
				}

				if (samples == 0)
				{
					continue;
				}

				drawing.Lines.Add(new RasterLine
				{
					X1 = x1,
					Y1 = y1,
					X2 = x2,
					Y2 = y2,
					R = (byte)(sr / samples),
					G = (byte)(sg / samples),
					B = (byte)(sb / samples),
					Thickness = Math.Max(1.0, thicknessPx * diagonal)
				});
			}

			foreach (int i in remove)
			{
				cls[i] = Background;
				taken[i] = true;
			}
		}

		// Restos sueltos de las bandas (bordes con antialiasing) no deben volverse trazos.
		RemoveSpecks(cls, w, h, (int)Math.Round(6 * S * S));
	}

	/// <summary>Color del píxel de tinta más oscuro en el entorno 3×3.</summary>
	private static uint DarkestAround(uint[] argb, byte[] cls, int i, int w, int h)
	{
		int x = i % w, y = i / w;
		uint best = argb[i];
		int bestLuma = Luma(best);
		for (int dy = -1; dy <= 1; dy++)
		{
			for (int dx = -1; dx <= 1; dx++)
			{
				int nx = x + dx, ny = y + dy;
				if (nx < 0 || ny < 0 || nx >= w || ny >= h || cls[ny * w + nx] != Ink)
				{
					continue;
				}

				uint c = argb[ny * w + nx];
				int l = Luma(c);
				if (l < bestLuma)
				{
					best = c;
					bestLuma = l;
				}
			}
		}

		return best;
	}

	private static int Luma(uint c) => (int)((((c >> 16) & 255) * 299 + ((c >> 8) & 255) * 587 + (c & 255) * 114) / 1000);

	/// <summary>
	/// Une los tramos que están sobre una misma recta (mismo ángulo ±3°, a menos de 1.2 px de la recta, con huecos de
	/// hasta 2.5 px y color y grosor parecidos) en un solo segmento ajustado por mínimos cuadrados: los rayados y las
	/// líneas que el esqueleto corta en cada cruce vuelven a ser rectas continuas.
	/// </summary>
	private static List<RasterLine> MergeCollinear(List<RasterLine> lines)
	{
		int n = lines.Count;
		var angle = new double[n];
		var rho = new double[n];
		var length = new double[n];
		var buckets = new Dictionary<(int, int), List<int>>();
		const double angleBin = 2.0, rhoBin = 2.0;
		for (int k = 0; k < n; k++)
		{
			RasterLine l = lines[k];
			double dx = l.X2 - l.X1, dy = l.Y2 - l.Y1;
			length[k] = Math.Sqrt(dx * dx + dy * dy);
			double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
			a = ((a % 180.0) + 180.0) % 180.0;
			angle[k] = a;
			double rad = a * Math.PI / 180.0;
			rho[k] = -(l.X1 + l.X2) / 2.0 * Math.Sin(rad) + (l.Y1 + l.Y2) / 2.0 * Math.Cos(rad);
			var key = ((int)Math.Floor(a / angleBin), (int)Math.Floor(rho[k] / rhoBin));
			if (!buckets.TryGetValue(key, out List<int> list))
			{
				list = new List<int>();
				buckets[key] = list;
			}

			list.Add(k);
		}

		var parent = Enumerable.Range(0, n).ToArray();
		int Find(int k)
		{
			while (parent[k] != k)
			{
				parent[k] = parent[parent[k]];
				k = parent[k];
			}

			return k;
		}

		int angleBins = (int)(180.0 / angleBin);
		for (int k = 0; k < n; k++)
		{
			if (length[k] < 2.0)
			{
				continue;
			}

			int ab = (int)Math.Floor(angle[k] / angleBin), rb = (int)Math.Floor(rho[k] / rhoBin);
			for (int da = -1; da <= 1; da++)
			{
				int a2 = ab + da;
				bool wrapped = a2 < 0 || a2 >= angleBins;
				a2 = (a2 + angleBins) % angleBins;
				for (int dr = -1; dr <= 1; dr++)
				{
					// Al dar la vuelta (0° ↔ 180°) la distancia ρ cambia de signo.
					int r2 = wrapped ? -rb - 1 + dr : rb + dr;
					if (!buckets.TryGetValue((a2, r2), out List<int> candidates))
					{
						continue;
					}

					foreach (int j in candidates)
					{
						if (j <= k || length[j] < 2.0 || Find(j) == Find(k) || !Collinear(lines[k], lines[j]))
						{
							continue;
						}

						parent[Find(j)] = Find(k);
					}
				}
			}
		}

		var result = new List<RasterLine>();
		foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
		{
			List<RasterLine> members = group.Select(k => lines[k]).ToList();
			RasterLine merged = members.Count > 1 ? FitLine(members) : null;
			if (merged != null)
			{
				result.Add(merged);
			}
			else
			{
				result.AddRange(members);
			}
		}

		return result;
	}

	private static bool Collinear(RasterLine a, RasterLine b)
	{
		double ax = a.X2 - a.X1, ay = a.Y2 - a.Y1;
		double la = Math.Sqrt(ax * ax + ay * ay);
		double bx = b.X2 - b.X1, by = b.Y2 - b.Y1;
		double lb = Math.Sqrt(bx * bx + by * by);
		if (la < 1E-9 || lb < 1E-9)
		{
			return false;
		}

		double ux = ax / la, uy = ay / la;
		double cos = Math.Abs((ux * bx + uy * by) / lb);
		if (cos < Math.Cos(3.0 * Math.PI / 180.0))
		{
			return false;
		}

		// Distancia de los extremos de b a la recta de a.
		double d1 = Math.Abs(-(b.X1 - a.X1) * uy + (b.Y1 - a.Y1) * ux);
		double d2 = Math.Abs(-(b.X2 - a.X2) * uy + (b.Y2 - a.Y2) * ux);
		if (d1 > 1.2 * S || d2 > 1.2 * S)
		{
			return false;
		}

		// Hueco entre los dos tramos medido sobre la recta.
		double t1 = (b.X1 - a.X1) * ux + (b.Y1 - a.Y1) * uy, t2 = (b.X2 - a.X1) * ux + (b.Y2 - a.Y1) * uy;
		double bMin = Math.Min(t1, t2), bMax = Math.Max(t1, t2);
		double gap = Math.Max(bMin - la, -bMax);
		if (gap > 2.5 * S)
		{
			return false;
		}

		int colorDistance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
		double thick = Math.Max(a.Thickness, b.Thickness) / Math.Max(1.0, Math.Min(a.Thickness, b.Thickness));
		return colorDistance < 120 && thick < 2.2;
	}

	/// <summary>Recta de mínimos cuadrados por los extremos de los tramos (ponderados por su largo); null si no encajan.</summary>
	private static RasterLine FitLine(List<RasterLine> members)
	{
		double sw = 0, sx = 0, sy = 0;
		foreach (RasterLine l in members)
		{
			double len = Math.Max(0.5, Math.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1)));
			sw += 2 * len;
			sx += (l.X1 + l.X2) * len;
			sy += (l.Y1 + l.Y2) * len;
		}

		double mx = sx / sw, my = sy / sw;
		double sxx = 0, syy = 0, sxy = 0;
		foreach (RasterLine l in members)
		{
			double len = Math.Max(0.5, Math.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1)));
			foreach (var (px, py) in new[] { (l.X1, l.Y1), (l.X2, l.Y2) })
			{
				sxx += (px - mx) * (px - mx) * len;
				syy += (py - my) * (py - my) * len;
				sxy += (px - mx) * (py - my) * len;
			}
		}

		double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
		double ux = Math.Cos(theta), uy = Math.Sin(theta);
		double tMin = double.MaxValue, tMax = double.MinValue, worst = 0, totalLength = 0, sr = 0, sg = 0, sbl = 0, st = 0;
		foreach (RasterLine l in members)
		{
			double len = Math.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1));
			foreach (var (px, py) in new[] { (l.X1, l.Y1), (l.X2, l.Y2) })
			{
				double t = (px - mx) * ux + (py - my) * uy;
				tMin = Math.Min(tMin, t);
				tMax = Math.Max(tMax, t);
				worst = Math.Max(worst, Math.Abs(-(px - mx) * uy + (py - my) * ux));
			}

			totalLength += len;
			sr += l.R * len;
			sg += l.G * len;
			sbl += l.B * len;
			st += l.Thickness * len;
		}

		if (worst > 1.5 * S || totalLength <= 0)
		{
			return null;
		}

		double x1 = mx + ux * tMin, y1 = my + uy * tMin, x2 = mx + ux * tMax, y2 = my + uy * tMax;
		Straighten(ref x1, ref y1, ref x2, ref y2);
		return new RasterLine
		{
			X1 = x1,
			Y1 = y1,
			X2 = x2,
			Y2 = y2,
			R = (byte)(sr / totalLength),
			G = (byte)(sg / totalLength),
			B = (byte)(sbl / totalLength),
			Thickness = st / totalLength
		};
	}

	/// <summary>Quita los tramos cortos (≤ 5 px) que quedan pegados a una línea larga (restos de los cruces).</summary>
	private static List<RasterLine> RemoveStubs(List<RasterLine> lines)
	{
		double stub = 5.0 * S, near = 1.8 * S;
		const int cell = 16;
		var grid = new Dictionary<(int, int), List<RasterLine>>();
		double Len(RasterLine l) => Math.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1));
		foreach (RasterLine l in lines.Where(l => Len(l) >= 12.0 * S))
		{
			int cx1 = (int)Math.Floor(Math.Min(l.X1, l.X2) / cell), cx2 = (int)Math.Floor(Math.Max(l.X1, l.X2) / cell);
			int cy1 = (int)Math.Floor(Math.Min(l.Y1, l.Y2) / cell), cy2 = (int)Math.Floor(Math.Max(l.Y1, l.Y2) / cell);
			for (int cx = cx1; cx <= cx2; cx++)
			{
				for (int cy = cy1; cy <= cy2; cy++)
				{
					if (!grid.TryGetValue((cx, cy), out List<RasterLine> list))
					{
						list = new List<RasterLine>();
						grid[(cx, cy)] = list;
					}

					list.Add(l);
				}
			}
		}

		bool NearLong(double x, double y)
		{
			if (!grid.TryGetValue(((int)Math.Floor(x / cell), (int)Math.Floor(y / cell)), out List<RasterLine> list))
			{
				return false;
			}

			return list.Any(l => PointLineDistance((x, y), (l.X1, l.Y1), (l.X2, l.Y2)) <= near
				&& (x - l.X1) * (l.X2 - l.X1) + (y - l.Y1) * (l.Y2 - l.Y1) >= -near * Len(l)
				&& (x - l.X2) * (l.X1 - l.X2) + (y - l.Y2) * (l.Y1 - l.Y2) >= -near * Len(l));
		}

		return lines.Where(l => Len(l) > stub || !(NearLong(l.X1, l.Y1) && NearLong(l.X2, l.Y2))).ToList();
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
