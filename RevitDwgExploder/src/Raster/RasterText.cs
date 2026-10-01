using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitDwgExploder.Raster;

/// <summary>Texto reconocido en la imagen, en píxeles (origen arriba a la izquierda).</summary>
internal sealed class RasterTextLine
{
	public string Text;
	public int X1;
	public int Y1;
	public int X2;
	public int Y2;

	/// <summary>Línea base (y en píxeles); si el OCR no la da, el borde inferior de la caja.</summary>
	public double BaselineY;

	public float Confidence;

	/// <summary>Tamaño de letra ("em") en píxeles, ajustado al ancho real del texto.</summary>
	public double EmPx;

	public bool Bold;
}

/// <summary>
/// Preparación de la imagen para el OCR y limpieza de su resultado (sin dependencias de Revit ni de Windows).
/// </summary>
internal static class RasterText
{
	/// <summary>
	/// Imagen en grises para el OCR: sin los rellenos ni las líneas largas (las líneas de referencia pegadas a los
	/// textos impiden que Tesseract los lea) y ampliada <paramref name="scale"/> veces (lee mal letras de menos de
	/// ~20 px de alto).
	/// </summary>
	/// <param name="clean">
	/// true: se borran los rellenos, las líneas largas y los grises claros (lee las etiquetas subrayadas por líneas
	/// de referencia); false: la imagen tal cual (lee mejor los títulos grandes y el texto gris fino).
	/// </param>
	public static byte[] OcrGray(int w, int h, uint[] argb, RasterDrawing firstPass, bool clean)
	{
		var gray = new byte[w * h];
		for (int i = 0; i < gray.Length; i++)
		{
			uint c = argb[i];
			int a = (int)(c >> 24);
			int r = (int)((c >> 16) & 255), g = (int)((c >> 8) & 255), b = (int)(c & 255);
			int v = a < 128 ? 255 : (r * 299 + g * 587 + b * 114) / 1000;

			// Los grises claros (rayados, vegetación, sombreados) se blanquean: el texto de un plano es oscuro.
			gray[i] = clean && (v >= LightLimit || firstPass.Classes != null && firstPass.Classes[i] == 2) ? (byte)255 : (byte)v;
		}

		if (!clean)
		{
			return gray;
		}

		double minLength = LongLineLength(w, h);
		double minAxisLength = Math.Max(26.0, (w + h) / 80.0);
		foreach (RasterLine line in firstPass.Lines)
		{
			double length = Math.Sqrt((line.X2 - line.X1) * (line.X2 - line.X1) + (line.Y2 - line.Y1) * (line.Y2 - line.Y1));

			// Horizontales más cortas también: son los subrayados de las etiquetas (las letras tienen trazos verticales
			// largos, pero sus trazos horizontales son cortos).
			bool horizontal = Math.Abs(line.Y2 - line.Y1) < 0.5;
			if (length >= minLength || (horizontal && length >= minAxisLength))
			{
				EraseSegment(gray, w, h, line.X1, line.Y1, line.X2, line.Y2, line.Thickness / 2.0 + 1.5);
			}
		}

		return gray;
	}

	/// <summary>Ampliación bilineal ×<paramref name="scale"/>.</summary>
	public static byte[] Upscale(byte[] gray, int w, int h, int scale, out int outWidth, out int outHeight)
	{
		if (scale <= 1)
		{
			outWidth = w;
			outHeight = h;
			return gray;
		}

		outWidth = w * scale;
		outHeight = h * scale;
		var result = new byte[outWidth * outHeight];
		for (int y = 0; y < outHeight; y++)
		{
			double sy = Math.Max(0, Math.Min(h - 1.0, (y + 0.5) / scale - 0.5));
			int y0 = (int)sy, y1 = Math.Min(h - 1, y0 + 1);
			double fy = sy - y0;
			for (int x = 0; x < outWidth; x++)
			{
				double sx = Math.Max(0, Math.Min(w - 1.0, (x + 0.5) / scale - 0.5));
				int x0 = (int)sx, x1 = Math.Min(w - 1, x0 + 1);
				double fx = sx - x0;
				double top = gray[y0 * w + x0] * (1 - fx) + gray[y0 * w + x1] * fx;
				double bottom = gray[y1 * w + x0] * (1 - fx) + gray[y1 * w + x1] * fx;
				result[y * outWidth + x] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
			}
		}

		return result;
	}

	/// <summary>
	/// Zonas que parecen texto (grupos en fila de manchas oscuras pequeñas, del tamaño de una letra) y que el OCR de
	/// página no leyó: se leen una a una como una sola línea.
	/// </summary>
	public static List<PixelRect> FindTextCandidates(byte[] cleanGray, int w, int h, IList<RasterTextLine> known, int maxCandidates = 400)
	{
		int maxHeight = Math.Max(40, h / 20);
		var seen = new bool[w * h];
		var stack = new Stack<int>();
		var boxes = new List<(int X1, int Y1, int X2, int Y2)>();
		for (int start = 0; start < seen.Length; start++)
		{
			if (seen[start] || cleanGray[start] >= 128)
			{
				continue;
			}

			int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1, count = 0;
			stack.Push(start);
			seen[start] = true;
			while (stack.Count > 0)
			{
				int i = stack.Pop();
				int x = i % w, y = i / w;
				count++;
				x1 = Math.Min(x1, x);
				y1 = Math.Min(y1, y);
				x2 = Math.Max(x2, x);
				y2 = Math.Max(y2, y);
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
						if (!seen[j] && cleanGray[j] < 128)
						{
							seen[j] = true;
							stack.Push(j);
						}
					}
				}
			}

			int bh = y2 - y1 + 1, bw = x2 - x1 + 1;
			if (bh >= 6 && bh <= maxHeight && bw <= bh * 12 && count >= 8)
			{
				boxes.Add((x1, y1, x2, y2));
			}
		}

		// Agrupar letras vecinas de la misma fila.
		boxes.Sort((a, b) => a.X1.CompareTo(b.X1));
		var parent = Enumerable.Range(0, boxes.Count).ToArray();
		int Find(int k) => parent[k] == k ? k : parent[k] = Find(parent[k]);
		for (int a = 0; a < boxes.Count; a++)
		{
			var A = boxes[a];
			int ha = A.Y2 - A.Y1 + 1;
			for (int b = a + 1; b < boxes.Count; b++)
			{
				var B = boxes[b];
				int hb = B.Y2 - B.Y1 + 1;
				int maxH = Math.Max(ha, hb);
				if (B.X1 - A.X2 > maxH * 0.8)
				{
					break;
				}

				int overlap = Math.Min(A.Y2, B.Y2) - Math.Max(A.Y1, B.Y1) + 1;
				if (overlap >= Math.Min(ha, hb) * 0.6 && maxH <= Math.Min(ha, hb) * 1.7)
				{
					parent[Find(b)] = Find(a);
				}
			}
		}

		var result = new List<PixelRect>();
		foreach (var group in Enumerable.Range(0, boxes.Count).GroupBy(Find))
		{
			int gx1 = group.Min(k => boxes[k].X1), gy1 = group.Min(k => boxes[k].Y1);
			int gx2 = group.Max(k => boxes[k].X2), gy2 = group.Max(k => boxes[k].Y2);
			// Una sola mancha vale si es ancha (letras pegadas); varias deben formar una fila.
			if (gx2 - gx1 + 1 < (gy2 - gy1 + 1) * (group.Count() < 2 ? 1.5 : 1.2))
			{
				continue;
			}

			bool covered = known.Any(t => Math.Min(t.X2, gx2) > Math.Max(t.X1, gx1) && Math.Min(t.Y2, gy2) > Math.Max(t.Y1, gy1));
			if (!covered)
			{
				result.Add(new PixelRect(gx1, gy1, gx2, gy2));
			}
		}

		return result.Take(maxCandidates).ToList();
	}

	/// <summary>Negrita: trazo grueso respecto a la altura; si casi todo el texto es grueso, se relaja el umbral.</summary>
	public static void DetectBold(List<RasterTextLine> lines, byte[] gray, int w, int h)
	{
		var ratios = lines.Select(l => StrokeRatio(gray, w, h, l)).ToList();
		if (ratios.Count == 0)
		{
			return;
		}

		double median = ratios.OrderBy(r => r).ElementAt(ratios.Count / 2);
		double limit = median >= 0.19 ? 0.15 : 0.19;
		for (int k = 0; k < lines.Count; k++)
		{
			lines[k].Bold = ratios[k] >= limit;
		}
	}

	/// <summary>Luminosidad a partir de la cual un píxel se considera "claro" para el OCR.</summary>
	private const int LightLimit = 170;

	/// <summary>Ampliación para el OCR: 3× si la imagen es pequeña, 2× normalmente, 1× si ya es muy grande.</summary>
	public static int OcrScale(int w, int h)
	{
		int side = Math.Max(w, h);
		return side <= 1400 ? 3 : side <= 3000 ? 2 : 1;
	}

	/// <summary>Largo a partir del cual una línea no es parte de una letra.</summary>
	public static double LongLineLength(int w, int h) => Math.Max(30.0, (w + h) / 60.0);

	private static void EraseSegment(byte[] gray, int w, int h, double x1, double y1, double x2, double y2, double radius)
	{
		double length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
		int steps = Math.Max(1, (int)Math.Ceiling(length * 2));
		int r = (int)Math.Ceiling(radius);
		for (int s = 0; s <= steps; s++)
		{
			double t = (double)s / steps;
			int cx = (int)Math.Round(x1 + (x2 - x1) * t), cy = (int)Math.Round(y1 + (y2 - y1) * t);
			for (int dy = -r; dy <= r; dy++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					int x = cx + dx, y = cy + dy;
					if (x >= 0 && y >= 0 && x < w && y < h && dx * dx + dy * dy <= radius * radius)
					{
						gray[y * w + x] = 255;
					}
				}
			}
		}
	}

	/// <summary>Guarda una imagen en grises como PGM (Tesseract/Leptonica lo leen directamente).</summary>
	public static void WritePgm(string path, byte[] gray, int w, int h)
	{
		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
		byte[] header = Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
		stream.Write(header, 0, header.Length);
		stream.Write(gray, 0, gray.Length);
	}

	/// <summary>
	/// Descarta lo que el OCR "lee" en rayados, vegetación o muestras de leyenda: textos sin suficientes letras o
	/// números, repeticiones ("zzzz"), o textos cortos con baja confianza que no parecen códigos (C01, A-35…).
	/// </summary>
	public static bool IsPlausible(string text, float confidence)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		string trimmed = text.Trim();

		// Código de plano exacto (C02, S05, A101): aunque la confianza sea baja, la lectura es casi segura.
		if (System.Text.RegularExpressions.Regex.IsMatch(FixCodes(trimmed), @"^[A-Z]\d{2,3}$"))
		{
			return confidence >= 25f;
		}

		var alnum = trimmed.Where(char.IsLetterOrDigit).ToList();
		if (alnum.Count < 2 || alnum.Count < trimmed.Count(c => !char.IsWhiteSpace(c)) * 0.6)
		{
			return false;
		}

		if (alnum.Select(char.ToUpperInvariant).Distinct().Count() == 1)
		{
			return false;
		}

		bool hasDigit = alnum.Any(char.IsDigit);
		bool hasVowel = alnum.Any(c => "AEIOUaeiouÁÉÍÓÚáéíóú".IndexOf(c) >= 0);
		if (alnum.Count <= 4)
		{
			// Corto: debe parecer un código (letra(s) + número) o tener confianza alta.
			bool code = hasDigit && alnum.Any(char.IsLetter) || alnum.All(char.IsDigit);
			return code ? confidence >= 60f : confidence >= 85f && (hasVowel || alnum.All(char.IsUpper));
		}

		return (hasVowel || hasDigit) && confidence >= 60f;
	}

	/// <summary>
	/// Corrige confusiones típicas en códigos de plano: "co2" → "C02", "$05" → "S05", "AQ1" → "A01".
	/// </summary>
	public static string FixCodes(string text)
	{
		var words = text.Split(' ');
		for (int k = 0; k < words.Length; k++)
		{
			string word = words[k].Trim('_', '|', '.', ',', ':', ';', '-');
			if (word.Length < 2 || word.Length > 5)
			{
				continue;
			}

			// Letra + dígitos (con letras parecidas a números en la parte numérica).
			char first = char.ToUpperInvariant(word[0] == '$' ? 'S' : word[0]);
			string rest = word.Substring(1).ToUpperInvariant().Replace('O', '0').Replace('Q', '0').Replace('D', '0').Replace('I', '1').Replace('L', '1');
			if (char.IsLetter(first) && rest.Length >= 1 && rest.All(char.IsDigit) && word.Skip(1).Count(char.IsDigit) >= 1)
			{
				words[k] = first + rest;
			}
		}

		return string.Join(" ", words);
	}

	/// <summary>
	/// Grosor relativo del trazo de las letras (ancho medio de las rachas horizontales de tinta / alto de la caja):
	/// ~0.12 en Arial normal y ~0.2 en negrita.
	/// </summary>
	public static double StrokeRatio(byte[] gray, int w, int h, RasterTextLine line)
	{
		int x1 = Math.Max(0, line.X1), x2 = Math.Min(w - 1, line.X2);
		int y1 = Math.Max(0, line.Y1), y2 = Math.Min(h - 1, line.Y2);
		long inkPixels = 0, runs = 0;
		for (int y = y1; y <= y2; y++)
		{
			bool inRun = false;
			for (int x = x1; x <= x2; x++)
			{
				bool ink = gray[y * w + x] < 128;
				if (ink)
				{
					inkPixels++;
					if (!inRun)
					{
						runs++;
					}
				}

				inRun = ink;
			}
		}

		double boxHeight = Math.Max(1, y2 - y1 + 1);
		return runs == 0 ? 0 : inkPixels / (double)runs / boxHeight;
	}

	/// <summary>
	/// Une las lecturas de varias pasadas de OCR: para cada zona se queda la de mayor confianza (las cajas que se
	/// solapan en más de la mitad son la misma zona).
	/// </summary>
	public static List<RasterTextLine> Merge(IEnumerable<RasterTextLine> candidates)
	{
		var accepted = new List<RasterTextLine>();
		foreach (RasterTextLine line in candidates.OrderByDescending(l => l.Confidence))
		{
			bool overlaps = accepted.Any(a =>
			{
				int ix = Math.Min(a.X2, line.X2) - Math.Max(a.X1, line.X1);
				int iy = Math.Min(a.Y2, line.Y2) - Math.Max(a.Y1, line.Y1);
				if (ix <= 0 || iy <= 0)
				{
					return false;
				}

				double smaller = Math.Min((a.X2 - a.X1) * (double)(a.Y2 - a.Y1), (line.X2 - line.X1) * (double)(line.Y2 - line.Y1));
				return ix * (double)iy > smaller * 0.5;
			});
			if (!overlaps)
			{
				accepted.Add(line);
			}
		}

		return accepted;
	}

	/// <summary>
	/// Unifica los tamaños: los textos de tamaño parecido (±12 %) usan el mismo, así no aparece un tipo de texto por
	/// línea. Se agrupa de menor a mayor y cada grupo toma su mediana.
	/// </summary>
	public static void UnifySizes(List<RasterTextLine> lines)
	{
		List<RasterTextLine> sorted = lines.Where(l => l.EmPx > 0).OrderBy(l => l.EmPx).ToList();
		int start = 0;
		while (start < sorted.Count)
		{
			int end = start;
			while (end + 1 < sorted.Count && sorted[end + 1].EmPx <= sorted[start].EmPx * 1.12)
			{
				end++;
			}

			double median = sorted[(start + end) / 2].EmPx;
			for (int k = start; k <= end; k++)
			{
				sorted[k].EmPx = median;
			}

			start = end + 1;
		}
	}
}
