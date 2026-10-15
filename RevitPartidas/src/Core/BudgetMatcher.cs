using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitPartidas.Core;

/// <summary>Una partida candidata para un elemento, con su parecido (0–1) y de dónde salió.</summary>
public sealed class Suggestion
{
	public string Code;

	public double Score;

	/// <summary>Qué la sugiere (p. ej. "Familia : Tipo", "DOC_NOM_PARTIDA (tipo)").</summary>
	public string Source;

	/// <summary>True si un parámetro tiene escrito el código exacto; false si es por parecido de nombre.</summary>
	public bool ByCode;

	public override string ToString() => ByCode ? $"{Code} (código en {Source})" : $"{Code} ({Score:P0} por {Source})";
}

/// <summary>
/// Busca en el presupuesto la partida cuya descripción se parece a un texto del modelo (nombre de familia o tipo, o el valor de
/// un parámetro). Compara palabras sin tildes, sin plurales simples y sin palabras vacías.
/// </summary>
public sealed class BudgetMatcher
{
	/// <summary>Parecido mínimo para sugerir una partida por nombre.</summary>
	public const double Threshold = 0.6;

	private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
	{
		"de", "del", "la", "las", "el", "los", "lo", "con", "sin", "y", "e", "o", "u", "en", "para", "por", "a", "al", "un", "una",
		"tipo", "mm", "cm", "m", "m2", "m3", "ml", "und", "incl", "incluye", "segun", "similar", "otros", "varios", "generico", "basico"
	};

	private readonly Budget _budget;

	private readonly List<(BudgetItem Item, HashSet<string> Tokens, string Simple, string Head)> _items = new List<(BudgetItem, HashSet<string>, string, string)>();

	private readonly Dictionary<string, List<int>> _index = new Dictionary<string, List<int>>(StringComparer.Ordinal);

	private readonly Dictionary<(string, string), List<(BudgetItem, double)>> _cache = new Dictionary<(string, string), List<(BudgetItem, double)>>();

	public BudgetMatcher(Budget budget)
	{
		_budget = budget;
		foreach (BudgetItem item in budget.All.Where(i => i.Depth >= 3))
		{
			HashSet<string> tokens = Tokenize(item.Description);
			if (tokens.Count == 0)
			{
				continue;
			}

			int index = _items.Count;
			_items.Add((item, tokens, Simple(item.Description), Head(item.Description)));
			foreach (string token in tokens)
			{
				if (!_index.TryGetValue(token, out List<int> list))
				{
					_index[token] = list = new List<int>();
				}

				list.Add(index);
			}
		}
	}

	/// <summary>
	/// Las partidas más parecidas al texto (las de parecido ≥ <see cref="Threshold"/>), solo dentro de la rama indicada
	/// (p. ej. "03.01" para ETAPA 1; null = todo el presupuesto). El resultado se guarda para textos repetidos.
	/// </summary>
	public List<(BudgetItem Item, double Score)> Match(string text, string branch)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return new List<(BudgetItem, double)>();
		}

		var key = (text, branch ?? string.Empty);
		if (_cache.TryGetValue(key, out List<(BudgetItem, double)> cached))
		{
			return cached;
		}

		var result = new List<(BudgetItem, double)>();
		HashSet<string> tokens = Tokenize(text);
		string simple = Simple(text);
		string head = Head(text);
		if (tokens.Count > 0)
		{
			var shared = new Dictionary<int, int>();
			foreach (string token in tokens)
			{
				if (_index.TryGetValue(token, out List<int> list))
				{
					foreach (int i in list)
					{
						shared[i] = shared.TryGetValue(i, out int n) ? n + 1 : 1;
					}
				}
			}

			foreach (KeyValuePair<int, int> pair in shared)
			{
				var (item, itemTokens, itemSimple, itemHead) = _items[pair.Key];
				if (branch != null && !PartidaCode.IsAncestorOrSelf(branch, item.Code))
				{
					continue;
				}

				double score;
				if (itemSimple == simple)
				{
					score = 1;
				}
				else
				{
					// Hacen falta al menos 2 palabras en común, salvo que el texto tenga una sola palabra significativa.
					if (pair.Value < Math.Min(2, tokens.Count))
					{
						continue;
					}

					double dice = 2.0 * pair.Value / (tokens.Count + itemTokens.Count);
					double coverage = (double)pair.Value / tokens.Count;
					if (dice < 0.3 || coverage < 0.6)
					{
						continue;
					}

					// Desempate: la partida que empieza por la misma palabra principal ("CANALETA…" antes que "REJILLA… PARA CANALETA").
					score = Math.Min(0.99, (dice + coverage) / 2 + (head != null && head == itemHead ? 0.08 : 0));
				}

				if (score >= Threshold)
				{
					result.Add((item, score));
				}
			}

			result = result.OrderByDescending(r => r.Item2).ThenByDescending(r => r.Item1.IsLeaf).ThenBy(r => r.Item1.Code, StringComparer.Ordinal).Take(5).ToList();
		}

		_cache[key] = result;
		return result;
	}

	/// <summary>
	/// La misma partida en otra rama (p. ej. el tarrajeo de ETAPA 1 en ETAPA 2): una partida final con la misma descripción y
	/// unidad. Null si no hay exactamente una.
	/// </summary>
	public BudgetItem Equivalent(string code, string branch)
	{
		BudgetItem item = _budget.Find(code);
		if (item == null || string.IsNullOrEmpty(branch))
		{
			return null;
		}

		string simple = Simple(item.Description);
		var matches = _items.Where(i => i.Item.IsLeaf && i.Simple == simple && PartidaCode.IsAncestorOrSelf(branch, i.Item.Code) &&
				string.Equals(i.Item.Unit, item.Unit, StringComparison.OrdinalIgnoreCase))
			.Select(i => i.Item).ToList();
		return matches.Count == 1 ? matches[0] : null;
	}

	/// <summary>La primera palabra significativa (normalmente lo que es: muro, puerta, canaleta…).</summary>
	private static string Head(string text)
	{
		foreach (string word in Simple(text).Split(' '))
		{
			if (word.Length >= 3 && !StopWords.Contains(word) && !char.IsDigit(word[0]))
			{
				return Stem(word);
			}
		}

		return null;
	}

	/// <summary>Texto comparable: sin tildes, minúsculas, solo letras y números separados por un espacio.</summary>
	public static string Simple(string text)
	{
		string simple = BudgetReader.Simplify(text);
		var builder = new StringBuilder(simple.Length);
		bool space = false;
		foreach (char c in simple)
		{
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

	public static HashSet<string> Tokenize(string text)
	{
		var tokens = new HashSet<string>(StringComparer.Ordinal);
		foreach (string word in Simple(text).Split(' '))
		{
			if (word.Length < 3 || StopWords.Contains(word) || char.IsDigit(word[0]))
			{
				continue;
			}

			tokens.Add(Stem(word));
		}

		return tokens;
	}

	/// <summary>Plurales simples: "muros" → "muro", "canaletas" → "canaleta", "pavimentos" → "pavimento", "metalicas" → "metalica".</summary>
	private static string Stem(string word)
	{
		if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) && !word.EndsWith("ies", StringComparison.Ordinal) &&
			"rlnd".IndexOf(word[word.Length - 3]) >= 0)
		{
			return word.Substring(0, word.Length - 2);
		}

		return word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal) ? word.Substring(0, word.Length - 1) : word;
	}
}
