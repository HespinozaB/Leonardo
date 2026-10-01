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

	/// <summary>Tamaño de letra ("em") en píxeles, según la altura real de las mayúsculas.</summary>
	public double EmPx;

	/// <summary>Factor de ancho del tipo de texto para que el texto ocupe el ancho que tiene en la imagen.</summary>
	public double WidthFactor = 1.0;

	/// <summary>Caja que dio el OCR (antes de ajustarla a la tinta).</summary>
	public PixelRect OcrBox;

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
			// Los rellenos de color o gris también (muestras de leyenda); los negros no: suelen ser letras gruesas.
			bool lightFill = firstPass.Classes != null && firstPass.Classes[i] == 2 && v >= 90;
			gray[i] = clean && (v >= LightLimit || lightFill) ? (byte)255 : (byte)v;
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
				EraseSegment(gray, w, h, line.X1, line.Y1, line.X2, line.Y2, Math.Min(2.0, line.Thickness / 2.0 + 1.0));
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
		double limit = median >= 0.235 ? 0.18 : 0.235;
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
		return side <= 3000 ? 2 : 1;
	}

	/// <summary>Largo a partir del cual una línea no es parte de una letra.</summary>
	public static double LongLineLength(int w, int h) => Math.Max(45.0, (w + h) / 40.0);

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
			return confidence >= 50f;
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
			// Corto: debe parecer un código (C07, A-35, P7), una medida (16", 1/2, 2.10) o una palabra leída con
			// mucha confianza; los restos de rayados ("1 > q", "ve") no pasan.
			string compact = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray());
			bool code = System.Text.RegularExpressions.Regex.IsMatch(compact, @"^[A-Z]{1,3}[-.]?\d{1,4}[A-Za-z]?[.,:]?$");
			bool measure = System.Text.RegularExpressions.Regex.IsMatch(compact, @"^[+±]?\d+([.,/]\d+)?(°|""|'|m|cm|mm)?[.,:]?$");
			bool word = alnum.All(char.IsLetter) && trimmed.All(c => char.IsLetter(c) || c == ' ' || c == '.' || c == ':');

			// Palabras cortas en minúsculas ("cos", "on") o mezcladas ("Ho"): en un plano casi siempre son lecturas de
			// rayados o letras sueltas; las de 2 letras necesitan una lectura casi segura.
			if (alnum.Any(char.IsLower) && alnum.Any(char.IsUpper))
			{
				return false;
			}

			float wordConfidence = alnum.All(char.IsLower) || alnum.Count <= 2 ? 95f : 85f;
			return code ? confidence >= 60f : measure ? confidence >= 80f : word && confidence >= wordConfidence && (hasVowel || alnum.All(char.IsUpper));
		}

		return (hasVowel || hasDigit) && confidence >= 60f;
	}

	/// <summary>¿La caja es lo bastante ancha para ese texto? (una caja de 3 px no puede tener "51").</summary>
	public static bool FitsBox(RasterTextLine line)
	{
		int characters = line.Text.Count(c => !char.IsWhiteSpace(c));
		return line.X2 - line.X1 >= 0.4 * (line.Y2 - line.Y1) * characters;
	}

	/// <summary>
	/// Corrige confusiones típicas en códigos de plano: "co2" → "C02", "$05" → "S05", "AQ1" → "A01".
	/// </summary>
	public static string FixCodes(string text)
	{
		// Etiqueta sola de 3 caracteres con forma de código (FOS → F05, COS → C05): letra + "O" + cifra o letra
		// parecida a una cifra. Solo si es todo el texto, para no tocar palabras como "LOS".
		string single = text.Trim().Trim('_', '|', '.', ',', ':', ';', '-');
		if (single.Length == 3 && char.IsUpper(single[0]) && "O0Q".IndexOf(char.ToUpperInvariant(single[1])) >= 0
			&& "0123456789OSZBIl".IndexOf(single[2]) >= 0)
		{
			char last = single[2] switch { 'O' => '0', 'S' => '5', 'Z' => '2', 'B' => '8', 'I' => '1', 'l' => '1', _ => single[2] };
			return single[0] + "0" + last;
		}

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
	/// con la caja ajustada a las mayúsculas, ~0.15-0.2 en texto normal y ~0.27-0.3 en negrita.
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
		foreach (RasterTextLine line in candidates.OrderByDescending(Score))
		{
			// Misma lectura con y sin tildes (el modelo de inglés no las conoce): se queda la que las tiene.
			RasterTextLine same = accepted.FirstOrDefault(a => Overlap(a, line) && StripAccents(a.Text) == StripAccents(line.Text));
			if (same != null)
			{
				if (AccentCount(line.Text) > AccentCount(same.Text))
				{
					same.Text = line.Text;
				}

				continue;
			}

			// Un fragmento ("ES") no debe tapar la línea completa que lo contiene ("WICK WEEPHOLES"): la lectura mucho
			// más grande lo sustituye si su puntuación no es muy inferior.
			List<RasterTextLine> overlapping = accepted.Where(a => Overlap(a, line)).ToList();
			if (overlapping.Count > 0 && overlapping.All(a => Area(line) > 2.5 * Area(a) && Score(line) >= Score(a) - 20)
				&& overlapping.Sum(a => a.Text.Length) <= line.Text.Length + 2)
			{
				int index = accepted.IndexOf(overlapping[0]);
				accepted.RemoveAll(overlapping.Contains);
				accepted.Insert(Math.Min(index, accepted.Count), line);
				continue;
			}

			if (!accepted.Any(a => Overlap(a, line)))
			{
				accepted.Add(line);
			}
		}

		return accepted;
	}

	/// <summary>Palabra leída por el OCR (píxeles de la imagen original).</summary>
	internal readonly struct OcrWord
	{
		public readonly string Text;
		public readonly int X1, Y1, X2, Y2;
		public readonly float Confidence;
		public readonly int LineId;
		public readonly double BaselineY;

		public OcrWord(string text, int x1, int y1, int x2, int y2, float confidence, int lineId, double baselineY)
		{
			Text = text;
			X1 = x1;
			Y1 = y1;
			X2 = x2;
			Y2 = y2;
			Confidence = confidence;
			LineId = lineId;
			BaselineY = baselineY;
		}
	}

	/// <summary>
	/// Agrupa las palabras en líneas como el OCR, pero corta una línea donde hay un hueco grande (más de 1.5 veces la
	/// altura del texto): así un título y una etiqueta vecina ("DETALL D18" … "C01") no se mezclan y la lectura dudosa de
	/// una no arrastra a la otra.
	/// </summary>
	public static List<RasterTextLine> GroupWords(IEnumerable<OcrWord> words)
	{
		var result = new List<RasterTextLine>();
		foreach (var line in words.Where(wd => !string.IsNullOrWhiteSpace(wd.Text)).GroupBy(wd => wd.LineId))
		{
			List<OcrWord> sorted = line.OrderBy(wd => wd.X1).ToList();
			double height = sorted.Select(wd => wd.Y2 - wd.Y1).OrderBy(v => v).ElementAt(sorted.Count / 2);
			var piece = new List<OcrWord>();
			void Flush()
			{
				if (piece.Count == 0)
				{
					return;
				}

				result.Add(new RasterTextLine
				{
					Text = string.Join(" ", piece.Select(wd => wd.Text.Trim())),
					Confidence = piece.Average(wd => wd.Confidence),
					X1 = piece.Min(wd => wd.X1),
					Y1 = piece.Min(wd => wd.Y1),
					X2 = piece.Max(wd => wd.X2),
					Y2 = piece.Max(wd => wd.Y2),
					BaselineY = piece.Average(wd => wd.BaselineY)
				});
				piece.Clear();
			}

			foreach (OcrWord word in sorted)
			{
				if (piece.Count > 0 && word.X1 - piece[piece.Count - 1].X2 > Math.Max(6.0, height * 1.5))
				{
					Flush();
				}

				piece.Add(word);
			}

			Flush();
		}

		return result;
	}

	/// <summary>
	/// Organiza las lecturas de página: cada zona de texto queda una vez (la de mejor puntuación). Si esa lectura es
	/// segura (creíble y puntuación ≥ 90) se acepta tal cual; si no, la zona se vuelve a leer sola, como una línea:
	/// en una línea con palabras dudosas el OCR baja la confianza de todas (un título junto a una etiqueta).
	/// </summary>
	/// <summary>Máximo de zonas que se releen (tiempo acotado en imágenes con mucho "ruido" que parece texto).</summary>
	private const int MaxRereads = 150;

	public static void PlanReading(IEnumerable<RasterTextLine> pageReads, int height, out List<RasterTextLine> confident, out List<PixelRect> reread,
		out List<RasterTextLine> fallback)
	{
		confident = new List<RasterTextLine>();
		reread = new List<PixelRect>();
		fallback = new List<RasterTextLine>();
		var taken = new List<RasterTextLine>();
		foreach (RasterTextLine line in pageReads.OrderByDescending(Score))
		{
			int boxHeight = line.Y2 - line.Y1;
			if (boxHeight < 5 || boxHeight > height * 0.08)
			{
				continue;
			}

			bool plausible = IsPlausible(line.Text, line.Confidence) && FitsBox(line);
			if (taken.Any(t => Overlap(t, line)))
			{
				// Zona ya tomada por otra lectura: si esta es creíble, compite igual en la unión final.
				if (plausible)
				{
					line.Text = FixCodes(line.Text).Trim();
					fallback.Add(line);
				}

				continue;
			}

			taken.Add(line);
			if (plausible && Score(line) >= 90)
			{
				line.Text = FixCodes(line.Text).Trim();
				confident.Add(line);
			}
			else
			{
				if (reread.Count < MaxRereads)
				{
					reread.Add(new PixelRect(line.X1, line.Y1, line.X2 - 1, line.Y2 - 1));
				}

				// La lectura de página sigue compitiendo con la relectura (si es creíble): si la relectura sale peor, se
				// queda esta.
				if (plausible)
				{
					line.Text = FixCodes(line.Text).Trim();
					fallback.Add(line);
				}
			}
		}
	}

	/// <summary>
	/// Puntuación para elegir entre lecturas de la misma zona: la confianza del OCR, con bonificación para los códigos
	/// de plano (C03, S05) y penalización para lo que no suele haber en un plano (todo en minúsculas, signos dentro de
	/// una palabra como "DEIAI!").
	/// </summary>
	private static double Score(RasterTextLine line)
	{
		string text = line.Text?.Trim() ?? string.Empty;
		double score = line.Confidence;
		if (System.Text.RegularExpressions.Regex.IsMatch(FixCodes(text), @"^[A-Z]\d{2,3}$"))
		{
			score += 15;
		}

		if (text.Any(char.IsLower) && !text.Any(char.IsUpper))
		{
			score -= 10;
		}

		if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[A-Za-zÁÉÍÓÚÑáéíóúñ][!¡?¿|]+|[!¡?¿|]+[A-Za-zÁÉÍÓÚÑáéíóúñ]"))
		{
			score -= 20;
		}

		return score;
	}

	private static double Area(RasterTextLine l) => Math.Max(1, l.X2 - l.X1) * (double)Math.Max(1, l.Y2 - l.Y1);

	private static bool Overlap(RasterTextLine a, RasterTextLine b)
	{
		int ix = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
		int iy = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
		if (ix <= 0 || iy <= 0)
		{
			return false;
		}

		double smaller = Math.Min((a.X2 - a.X1) * (double)(a.Y2 - a.Y1), (b.X2 - b.X1) * (double)(b.Y2 - b.Y1));
		return ix * (double)iy > smaller * 0.5;
	}

	private static string StripAccents(string text) => new string(text.Normalize(NormalizationForm.FormD)
		.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
		.ToArray()).ToUpperInvariant();

	private static int AccentCount(string text) => text.Normalize(NormalizationForm.FormD)
		.Count(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark);

	/// <summary>
	/// Ajusta cada texto a lo que ocupa en la imagen:
	/// <list type="number">
	/// <item>caja ajustada a la tinta: la franja de filas más larga con tinta abundante (así no cuentan el subrayado,
	/// los paréntesis ni los acentos) da la altura de las mayúsculas y la línea base;</item>
	/// <item>tamaño = altura de mayúsculas / 0.716 (Arial); los tamaños parecidos (±15 %) se unifican y comparten
	/// también la negrita (mayoría);</item>
	/// <item>factor de ancho para que el texto en Arial mida lo mismo que en la imagen (fuentes estrechas).</item>
	/// </list>
	/// </summary>
	public static void FitTexts(List<RasterTextLine> lines, byte[] gray, int w, int h)
	{
		foreach (RasterTextLine line in lines)
		{
			line.OcrBox = new PixelRect(line.X1, line.Y1, line.X2, line.Y2);
			TightenBox(gray, w, h, line);
		}

		DetectBold(lines, gray, w, h);
		foreach (RasterTextLine line in lines)
		{
			line.EmPx = Math.Max(1.0, line.BaselineY - line.Y1) / 0.716;
		}

		List<RasterTextLine> sorted = lines.OrderBy(l => l.EmPx).ToList();
		int start = 0;
		while (start < sorted.Count)
		{
			int end = start;
			while (end + 1 < sorted.Count && sorted[end + 1].EmPx <= sorted[start].EmPx * 1.15)
			{
				end++;
			}

			double median = sorted[(start + end) / 2].EmPx;
			int boldCount = 0;
			for (int k = start; k <= end; k++)
			{
				boldCount += sorted[k].Bold ? 1 : 0;
			}

			bool bold = boldCount * 2 > end - start + 1;
			for (int k = start; k <= end; k++)
			{
				sorted[k].EmPx = median;
				sorted[k].Bold = bold;
			}

			start = end + 1;
		}

		foreach (RasterTextLine line in lines)
		{
			double natural = ArialWidthEm(line.Text, line.Bold) * line.EmPx;
			double factor = natural > 1.0 ? (line.X2 - line.X1) / natural : 1.0;
			factor = Math.Max(0.6, Math.Min(1.2, factor));
			line.WidthFactor = Math.Floor(factor * 20.0) / 20.0; // múltiplos de 0.05, hacia abajo: nunca más ancho
		}
	}

	/// <summary>
	/// Zona cuyos píxeles no se vectorizan: la caja del OCR por los lados y arriba, y por abajo la línea base (el
	/// subrayado de una etiqueta es parte de su línea de referencia y se conserva), con margen si hay descendentes.
	/// </summary>
	public static PixelRect EraseArea(RasterTextLine t)
	{
		bool descenders = t.Text.IndexOfAny("gjpqy,;()".ToCharArray()) >= 0;
		int bottom = t.Y2 + (descenders ? Math.Max(2, (int)((t.Y2 - t.Y1) * 0.3)) : 1);
		return new PixelRect(Math.Min(t.X1, t.OcrBox.X1) - 2, Math.Min(t.Y1, t.OcrBox.Y1) - 3, Math.Max(t.X2, t.OcrBox.X2) + 2, bottom);
	}

	/// <summary>Ajusta la caja a la franja principal de tinta (mayúsculas) y fija la línea base en su borde inferior.</summary>
	private static void TightenBox(byte[] gray, int w, int h, RasterTextLine line)
	{
		int x1 = Math.Max(0, line.X1), x2 = Math.Min(w - 1, line.X2 - 1);
		int y1 = Math.Max(0, line.Y1 - 2), y2 = Math.Min(h - 1, line.Y2 + 1);
		if (x2 <= x1 || y2 <= y1)
		{
			return;
		}

		int ink = InkLimit(gray, w, x1, y1, x2, y2);
		var rows = new int[y2 - y1 + 1];
		for (int y = y1; y <= y2; y++)
		{
			for (int x = x1; x <= x2; x++)
			{
				if (gray[y * w + x] < ink)
				{
					rows[y - y1]++;
				}
			}
		}

		int max = rows.Max();
		if (max == 0)
		{
			return;
		}

		// Franja más larga de filas con al menos un 15 % de la tinta de la fila más cargada (un subrayado es una fila
		// muy cargada pero aislada; los paréntesis y descendentes ponen poca tinta por fila).
		int limit = Math.Max(1, (int)(max * 0.15));
		int bestStart = -1, bestLength = 0;
		for (int k = 0; k < rows.Length;)
		{
			if (rows[k] < limit)
			{
				k++;
				continue;
			}

			int runStart = k;
			while (k < rows.Length && rows[k] >= limit)
			{
				k++;
			}

			if (k - runStart > bestLength)
			{
				bestLength = k - runStart;
				bestStart = runStart;
			}
		}

		if (bestLength < 4)
		{
			return;
		}

		int top = y1 + bestStart, bottom = top + bestLength - 1;
		int left = -1, right = -1;
		for (int x = x1; x <= x2; x++)
		{
			for (int y = top; y <= bottom; y++)
			{
				if (gray[y * w + x] < ink)
				{
					if (left < 0)
					{
						left = x;
					}

					right = x;
					break;
				}
			}
		}

		if (left < 0)
		{
			return;
		}

		// Una "I" o "|" inicial que en realidad es un marco o una línea vertical (más clara que el texto, a la izquierda
		// de la primera letra, y que sigue por encima y por debajo del texto): se quita del texto.
		if (line.Text.Length > 2 && "I|l[".IndexOf(line.Text[0]) >= 0)
		{
			for (int x = Math.Max(0, line.X1 - 2); x < left; x++)
			{
				if (InkAt(gray, w, h, x, top - 3, 200) && InkAt(gray, w, h, x, (top + bottom) / 2, 200) && InkAt(gray, w, h, x, bottom + 3, 200))
				{
					line.Text = line.Text.Substring(1).TrimStart();
					break;
				}
			}
		}

		line.X1 = left;
		line.X2 = right + 1;
		line.Y1 = top;
		line.Y2 = bottom + 1;
		line.BaselineY = bottom + 1;
	}

	private static bool InkAt(byte[] gray, int w, int h, int x, int y, int ink) =>
		x >= 0 && y >= 0 && x < w && y < h && (gray[y * w + x] < ink || (x > 0 && gray[y * w + x - 1] < ink) || (x < w - 1 && gray[y * w + x + 1] < ink));

	/// <summary>Umbral de tinta dentro de una caja: a medio camino entre el píxel más oscuro y el papel.</summary>
	private static int InkLimit(byte[] gray, int w, int x1, int y1, int x2, int y2)
	{
		int darkest = 255;
		for (int y = y1; y <= y2; y++)
		{
			for (int x = x1; x <= x2; x++)
			{
				darkest = Math.Min(darkest, gray[y * w + x]);
			}
		}

		return Math.Min(200, (darkest + 255) / 2);
	}

	/// <summary>Ancho de un texto en Arial, en unidades de "em" (métricas de Helvetica, idénticas a las de Arial).</summary>
	public static double ArialWidthEm(string text, bool bold)
	{
		int[] widths = bold ? BoldWidths : RegularWidths;
		double total = 0;
		foreach (char raw in text.Normalize(NormalizationForm.FormD))
		{
			if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			total += raw >= 32 && raw <= 126 ? widths[raw - 32] : 556;
		}

		return total / 1000.0;
	}

	// Anchos (milésimas de em) de los caracteres 32..126.
	private static readonly int[] RegularWidths =
	{
		278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
		556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
		1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
		667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
		333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
		556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
	};

	private static readonly int[] BoldWidths =
	{
		278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
		556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
		975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
		667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
		333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
		611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
	};
}
