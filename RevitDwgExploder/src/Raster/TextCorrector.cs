using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitDwgExploder.Raster;

/// <summary>
/// Corrige lo que el OCR lee mal en imágenes de baja calidad usando un vocabulario de construcción (español e inglés):
/// <list type="bullet">
/// <item>tildes y Ñ que faltan (LAMINAS → LÁMINAS, BANOS → BAÑOS);</item>
/// <item>letras confundidas (WACK → WICK, MEADER → HEADER, STUOS → STUDS), solo si hay una única palabra del
/// vocabulario a esa distancia;</item>
/// <item>viñetas leídas como «, “, * (→ ·) y "NIVEL 20.00" → "NIVEL ±0.00".</item>
/// </list>
/// Las palabras que no se parecen a ninguna del vocabulario no se tocan.
/// </summary>
internal static class TextCorrector
{
	private static readonly string[] Spanish =
	{
		"ACABADO", "ACABADOS", "ACERO", "AGUA", "ALBAÑILERÍA", "ALTURA", "ANCHO", "ÁNGULO", "ÁNGULOS", "ARMADURA", "BAÑO", "BAÑOS",
		"BARANDA", "BLOQUE", "BLOQUES", "BRUÑADO", "CALCÁREO", "CAPA", "CEMENTO", "CERÁMICO", "CERÁMICA", "CIELO", "CIMENTACIÓN",
		"COBERTURA", "COLUMNA", "COLUMNAS", "CONCRETO", "CONSIDERARÁ", "CONSTRUCTIVOS", "CONSTRUIDOS", "CONSULTADOS",
		"CONTRATISTA", "CORRESPONDE", "CORTE", "CORTES", "CUBIERTA", "DATOS", "DEBERÁ", "DEBERÁN", "DEBIENDO", "DESCRIPCIÓN",
		"DETALLE", "DETALLES", "DIMENSIONES", "DINTEL", "DRYWALL", "ELEVACIÓN", "ELEVACIONES", "ENCHAPE", "ESCALA", "ESCALERA",
		"ESCALERAS", "ESPECIALISTA", "ESPECIFICACIONES", "ESTRUCTURA", "ESTRUCTURAS", "EXTERIOR", "EXTERIORES", "FALSA",
		"FIBROCEMENTO", "FRIBROCEMENTO", "FONDO", "FROTACHADO", "GRADERÍA", "GRADERÍAS", "HORAS", "IGNÍFUGOS", "IMPERMEABILIZANTE",
		"INDICADOS", "INSTALACIONES", "INTERIOR", "LADRILLO", "LADRILLOS", "LÁMINA", "LÁMINAS", "LEYENDA", "LOSA", "MADERA",
		"MAMPOSTERÍA", "MEDIDAS", "METÁLICA", "METÁLICO", "MORTERO", "MURO", "MUROS", "NIVEL", "NIVELES", "NOTA", "NOTAS",
		"OBRA", "PANEL", "PARAPETO", "PASES", "PERMITIRÁ", "PISO", "PISOS", "PLACA", "PLANO", "PLANOS", "PLANTA", "PLANTAS",
		"POYO", "PUERTA", "PUERTAS", "RAMPA", "RAMPAS", "RASO", "RELLENO", "RESISTENCIA", "RESPONSABLE", "REVESTIMIENTO",
		"SANITARIAS", "SECCIÓN", "SELLADORES", "SELLARSE", "SEMIPULIDO", "SERÁ", "SERÁN", "SÍLICO", "SOBRE", "SOLO", "TABIQUE",
		"TABIQUES", "TARRAJEADO", "TARRAJEO", "TÉCNICA", "TÉCNICAS", "TECHO", "TEXTURADO", "TIPO", "TOPOGRÁFICO", "TOTALMENTE",
		"VENTANA", "VENTANAS", "VERIFICACIÓN", "VERIFICADOS", "VIBROCOMPACTADO", "VIGA", "VIGAS", "ZAPATA", "ZÓCALO"
	};

	private static readonly string[] English =
	{
		"AIR", "ANCHOR", "ASPHALT", "BASE", "BEAM", "BITUMINOUS", "BLOCK", "BOARD", "BOLT", "BRICK", "BUILDING", "CEMENT",
		"CENTERS", "CLADDING", "COLLAR", "COLUMN", "CONCRETE", "CORRUGATED", "DETAIL", "DRAINAGE", "EVERY", "EXTERIOR", "FELT",
		"FINISH", "FLASHING", "FLOOR", "FOOTING", "FOUNDATION", "FRAME", "FULL", "GALVANIZED", "GAUGE", "GUAGE", "GYPSUM",
		"HEADER", "HEADJOINT", "HORIZONTALLY", "INSULATION", "INTERIOR", "JOINT", "JOIST", "MEMBRANE", "METAL", "MORTAR",
		"NAIL", "OPEN", "PARGING", "PLATE", "PROJECT", "PROOFING", "RESISTANT", "ROOF", "SCALE", "SECTION", "SHEATHING",
		"SILL", "SLAB", "SOLE", "SPACE", "STEEL", "STUD", "STUDS", "SUB", "SUBFLOOR", "TIE", "VAPOR", "VENEER", "VERTICALLY",
		"WALL", "WATER", "WEATHER", "WEEPHOLES", "WICK", "WINDOW", "WOOD"
	};

	/// <summary>Pares de caracteres que el OCR confunde a menudo (sustituirlos cuesta la mitad).</summary>
	private static readonly string[] Confusions =
	{
		"O0", "OD", "OQ", "OC", "OU", "DO", "I1", "IL", "IT", "I|", "L1", "LI", "S5", "SZ", "Z2", "B8", "BE", "BR", "G6", "GC",
		"EF", "FE", "FP", "HM", "MH", "HN", "NH", "MN", "RA", "AR", "IA", "AI", "UO", "UV", "VY", "CE", "EC", "TI", "TL", "KX", "RP",
		"PF", "SE", "ES"
	};

	/// <summary>Palabra sin tildes → (palabra con tildes, idioma: 'e' español, 'i' inglés).</summary>
	private static readonly Dictionary<string, (string Word, char Language)> ByPlain = BuildPlain();

	private static readonly HashSet<(char, char)> ConfusionSet = new HashSet<(char, char)>(
		Confusions.SelectMany(p => new[] { (p[0], p[1]), (p[1], p[0]) }));

	private static Dictionary<string, (string Word, char Language)> BuildPlain()
	{
		var map = new Dictionary<string, (string, char)>(StringComparer.Ordinal);
		foreach (string word in Spanish)
		{
			map[Strip(word)] = (word, 'e');
		}

		foreach (string word in English)
		{
			map[Strip(word)] = (word, 'i');
		}

		return map;
	}

	/// <param name="confidence">Confianza del OCR (0-100): con lecturas seguras solo se ponen tildes, no se cambian letras
	/// (así "DETALL" no pasa a "DETAIL").</param>
	public static string Correct(string text, float confidence = 0f)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return text;
		}

		string result = text.Trim();

		// Viñeta inicial leída como «, “, ‘, ", *, • o un guion pegado a la primera palabra.
		result = Regex.Replace(result, "^(?:[«“‘\"'•*]\\s?|[-–](?=\\S))(?=[A-ZÁÉÍÓÚÑ])", "·");

		// Cota de nivel cero: "NIVEL 20.00", "NIVEL £0.00", "NIVEL +0.00" → "NIVEL ±0.00".
		result = Regex.Replace(result, "(NIVEL\\s+)[£2+]0[.,]00\\b", "$1±0.00");

		// Palabra a palabra (solo mayúsculas: el texto de los planos va en mayúsculas).
		// Contexto: idiomas de las palabras de la línea que ya son del vocabulario. Dos pasadas: una palabra corregida
		// en la primera ("WOOO" → "WOOD") da contexto a la siguiente ("STUOS" → "STUDS").
		const string wordPattern = "[A-Za-zÁÉÍÓÚÑÜáéíóúñü0-9]{3,}";
		bool lowConfidence = confidence < 90f;
		for (int pass = 0; pass < 2; pass++)
		{
			var context = new HashSet<char>(Regex.Matches(result, wordPattern).Cast<Match>()
				.Select(m => ByPlain.TryGetValue(Strip(m.Value), out var entry) ? entry.Language : ' ')
				.Where(c => c != ' '));
			result = Regex.Replace(result, wordPattern, m => CorrectWord(m.Value, lowConfidence, context));
		}

		return result;
	}

	private static string CorrectWord(string word, bool lowConfidence, HashSet<char> context)
	{
		if (!word.Any(char.IsLetter) || word.Any(char.IsLower))
		{
			return word;
		}

		string plain = Strip(word);
		if (ByPlain.TryGetValue(plain, out var exact))
		{
			// Ya es una palabra conocida: solo se ponen las tildes que falten.
			return exact.Word;
		}

		// Palabras con cifras (A-35, D19, 1.5cm…) no se corrigen.
		if (word.Any(char.IsDigit) && word.Count(char.IsDigit) > 1)
		{
			return word;
		}

		// Se cambian letras solo si hay motivo: lectura poco segura, una triple letra imposible ("WOOO", "RESSSTANT")
		// o, si no, solo hacia palabras del idioma del resto de la línea (así "DETALL D18" se queda como está).
		bool tripled = Regex.IsMatch(plain, "(.)\\1\\1");
		bool anyLanguage = lowConfidence || tripled;
		if (!anyLanguage && context.Count == 0)
		{
			return word;
		}

		double limit = plain.Length >= 11 ? 2.0 : plain.Length >= 9 ? 1.5 : plain.Length >= 5 ? 1.0 : plain.Length == 4 ? 0.5 : 0.0;
		if (limit <= 0)
		{
			return word;
		}

		string best = null;
		double bestCost = double.MaxValue;
		bool tie = false;
		foreach (var entry in ByPlain)
		{
			if (Math.Abs(entry.Key.Length - plain.Length) > 1 || (!anyLanguage && !context.Contains(entry.Value.Language)))
			{
				continue;
			}

			double cost = Distance(plain, entry.Key, limit);
			if (cost < bestCost - 1E-9)
			{
				bestCost = cost;
				best = entry.Value.Word;
				tie = false;
			}
			else if (Math.Abs(cost - bestCost) < 1E-9 && entry.Value.Word != best)
			{
				tie = true;
			}
		}

		return best != null && !tie && bestCost <= limit ? best : word;
	}

	/// <summary>Distancia de edición con sustituciones baratas (0.5) para las confusiones típicas del OCR.</summary>
	private static double Distance(string a, string b, double limit)
	{
		var previous = new double[b.Length + 1];
		var current = new double[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
		{
			previous[j] = j;
		}

		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			double rowMin = current[0];
			for (int j = 1; j <= b.Length; j++)
			{
				double substitution = a[i - 1] == b[j - 1] ? 0 : ConfusionSet.Contains((a[i - 1], b[j - 1])) ? 0.5 : 1.0;
				current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + substitution);
				rowMin = Math.Min(rowMin, current[j]);
			}

			if (rowMin > limit)
			{
				return double.MaxValue;
			}

			(previous, current) = (current, previous);
		}

		return previous[b.Length];
	}

	private static string Strip(string text) => new string(text.ToUpperInvariant().Normalize(NormalizationForm.FormD)
		.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
		.ToArray());
}
