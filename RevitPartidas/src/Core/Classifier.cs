using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitPartidas.Core;

/// <summary>Estado de un elemento frente al presupuesto (de lo que más atención pide a lo que menos).</summary>
public enum RowStatus
{
	/// <summary>Varias partidas posibles: la decide el usuario (lista o vista 3D).</summary>
	Conflicto,

	/// <summary>Sin código de montaje, pero un parámetro o su nombre apunta a una partida: hay que confirmarla.</summary>
	Sugerencia,

	/// <summary>Su código es de otra rama que la del filtro (p. ej. ETAPA 2 en un elemento de ETAPA 1) y no hay equivalente.</summary>
	NoCoincide,

	/// <summary>El código de montaje no existe en el presupuesto.</summary>
	NoEnPresupuesto,

	/// <summary>El código de montaje es un título, no una partida final.</summary>
	Titulo,

	/// <summary>Lo escrito en los parámetros de nivel y el código de montaje dicen partidas distintas.</summary>
	Difiere,

	/// <summary>Partida clara: falta escribir (o completar) los parámetros de nivel.</summary>
	Pendiente,

	/// <summary>Sin partida, pero el filtro da los niveles superiores (p. ej. 03 / 03.01): se pueden escribir ya.</summary>
	Parcial,

	/// <summary>Nada en el modelo apunta a una partida.</summary>
	SinPartida,

	/// <summary>Los niveles están escritos y la partida existe en el presupuesto.</summary>
	Ok
}

/// <summary>Un elemento del modelo: lo leído de Revit y la clasificación frente al presupuesto.</summary>
public sealed class ElementRow
{
	// Leído del modelo.
	public long Id;

	public long TypeId = -1;

	public string Category;

	public string FamilyType;

	public string Level;

	/// <summary>Código y Descripción de montaje del tipo, tal como están en Revit.</summary>
	public string AssemblyCode = string.Empty;

	public string AssemblyDescription = string.Empty;

	/// <summary>Código de cada nivel según el filtro (fijo o parámetro de Revit); "" si el nivel sale del código.</summary>
	public string[] Linked;

	/// <summary>Valor leído de los parámetros del filtro (para mostrarlo; "" si no tiene).</summary>
	public string[] LinkValues;

	/// <summary>Algún nivel salió de un parámetro del elemento (p. ej. su ETAPA), no solo de un valor fijo.</summary>
	public bool HasParameterLink;

	/// <summary>Códigos del presupuesto escritos en cualquier parámetro de ejemplar o de tipo.</summary>
	public List<(string Code, string Source)> CodeHits = new List<(string, string)>();

	/// <summary>Textos para comparar por nombre: familia, tipo y valores de parámetros.</summary>
	public List<(string Text, string Source)> Texts = new List<(string, string)>();

	/// <summary>Valores actuales de los parámetros de nivel ("" si vacío; null si el elemento no tiene el parámetro).</summary>
	public string[] Current;

	public bool HasDycusaParameters;

	public double Area;

	public double Length;

	public double Volume;

	// Clasificación.
	public string DycusaCode;

	public bool DycusaInconsistent;

	/// <summary>Partida que sale del código de montaje (o de su equivalente en la etapa del elemento).</summary>
	public string SourceCode;

	public string SourceParam;

	public List<Suggestion> Suggestions = new List<Suggestion>();

	/// <summary>Las opciones a elegir (conflicto, sugerencia, o el código que no coincide).</summary>
	public List<string> Candidates = new List<string>();

	public string ProposedCode;

	public string[] Proposed;

	public RowStatus Status;

	public string Note;

	/// <summary>Su categoría tiene elementos con partida (los "sin partida" de otras categorías no se muestran salvo en la selección).</summary>
	public bool InCodedCategory = true;

	public bool IsUpToDate => HasDycusaParameters && Current.Select(v => v ?? string.Empty).SequenceEqual(Proposed);

	/// <summary>Cadena de niveles del filtro contiguos desde el nivel 1 (p. ej. "03.01"), o null.</summary>
	public string LinkedChain
	{
		get
		{
			string chain = null;
			foreach (string code in Linked ?? Array.Empty<string>())
			{
				if (string.IsNullOrEmpty(code) || (chain != null && !PartidaCode.IsAncestorOrSelf(chain, code)))
				{
					break;
				}

				chain = code;
			}

			return chain;
		}
	}

	public void AppendNote(string note) => Note = string.IsNullOrEmpty(Note) ? note : Note + " " + note;
}

public sealed class ScanResult
{
	public readonly List<ElementRow> Rows = new List<ElementRow>();

	public int ParametersBound;

	public TimeSpan Elapsed;
}

/// <summary>Sugerencia de filtro: un valor fijo o un parámetro cuyo valor coincide con las descripciones de un nivel.</summary>
public sealed class LinkSuggestion
{
	public int Level;

	public LinkMode Mode;

	public string ParameterName;

	public string FixedCode;

	public int Matches;

	public string Example;
}

/// <summary>
/// Decide la partida de cada elemento (sin tocar Revit, así se puede recalcular al instante):
/// 1) el Código de montaje de su tipo, ajustado a la etapa del filtro (si es de la otra etapa, su equivalente con la misma
///    descripción); 2) si no hay, los códigos del presupuesto escritos en cualquier parámetro; 3) si tampoco, el parecido del
///    nombre de familia/tipo o de los valores de parámetros con las descripciones del presupuesto. 2 y 3 son sugerencias que
///    el usuario confirma.
/// </summary>
public static class Classifier
{
	public static void Classify(ElementRow row, Budget budget, BudgetMatcher matcher, PartidasConfig config)
	{
		row.SourceCode = null;
		row.SourceParam = null;
		row.Suggestions = new List<Suggestion>();
		row.Candidates = new List<string>();
		row.Note = null;
		row.DycusaCode = PartidaCode.Deepest((row.Current ?? Array.Empty<string>()).Select(v => v ?? string.Empty).ToList(), out bool consistent);
		row.DycusaInconsistent = !consistent;

		string branch = row.LinkedChain ?? row.Linked?.LastOrDefault(l => !string.IsNullOrEmpty(l));
		RowStatus? montajeProblem = null;
		string montaje = PartidaCode.Normalize(row.AssemblyCode);
		if (!string.IsNullOrEmpty(row.AssemblyCode) && montaje == null)
		{
			row.AppendNote($"El código de montaje \"{row.AssemblyCode}\" no tiene forma de partida.");
		}

		if (montaje != null)
		{
			BudgetItem item = budget?.Find(montaje);
			if (budget != null && item == null)
			{
				montajeProblem = RowStatus.NoEnPresupuesto;
				row.AppendNote($"El código de montaje {montaje} no está en el presupuesto.");
			}
			else if (item != null && !item.IsLeaf)
			{
				montajeProblem = RowStatus.Titulo;
				row.AppendNote($"El código de montaje {montaje} es un título.");
			}
			else if (MatchesLinks(montaje, row.Linked))
			{
				row.SourceCode = montaje;
				row.SourceParam = "Código de montaje";
			}
			else
			{
				BudgetItem equivalent = matcher?.Equivalent(montaje, branch);
				if (equivalent != null)
				{
					row.SourceCode = equivalent.Code;
					row.SourceParam = "Código de montaje (equivalente)";
					row.AppendNote($"El código de montaje {montaje} es de otra rama; en {Describe(budget, branch)} se usa {equivalent.Code} (misma descripción).");
				}
				else
				{
					montajeProblem = RowStatus.NoCoincide;
					row.Candidates.Add(montaje);
					row.AppendNote($"El código de montaje {montaje} no corresponde a {Describe(budget, branch)} y no tiene equivalente por nombre.");
				}
			}
		}

		RowStatus? suggestionStatus = null;
		if (row.SourceCode == null)
		{
			suggestionStatus = Suggest(row, budget, matcher, branch, montajeProblem == RowStatus.Titulo ? montaje : null);
		}

		// Lo escrito a mano en los niveles es una decisión del usuario: vale si no hay código de montaje.
		string primary = row.SourceCode;
		row.ProposedCode = primary != null ? (config.PreferSource ? primary : row.DycusaCode ?? primary) : row.DycusaCode;
		int levels = config.Levels;
		if (row.ProposedCode == null)
		{
			row.Proposed = PartidaCode.Levels(row.HasParameterLink ? row.LinkedChain : null, levels);
			row.Status = suggestionStatus ?? montajeProblem ??
				(row.HasParameterLink && row.LinkedChain != null && !row.IsUpToDate ? RowStatus.Parcial : RowStatus.SinPartida);
			return;
		}

		row.Proposed = PartidaCode.Levels(row.ProposedCode, levels);
		BudgetItem proposed = budget?.Find(row.ProposedCode);
		if (!MatchesLinks(row.ProposedCode, row.Linked))
		{
			row.Status = RowStatus.NoCoincide;
			if (!row.Candidates.Contains(row.ProposedCode))
			{
				row.Candidates.Add(row.ProposedCode);
			}
		}
		else if (budget != null && proposed == null)
		{
			row.Status = RowStatus.NoEnPresupuesto;
		}
		else if (proposed != null && !proposed.IsLeaf)
		{
			row.Status = RowStatus.Titulo;
		}
		else if (row.DycusaCode != null && primary != null && row.DycusaCode != primary)
		{
			row.Status = RowStatus.Difiere;
			row.AppendNote($"Escrito: {row.DycusaCode}; código de montaje: {primary}.");
		}
		else
		{
			row.Status = row.IsUpToDate && !row.DycusaInconsistent ? RowStatus.Ok : RowStatus.Pendiente;
		}
	}

	/// <summary>Busca partidas en los parámetros (código exacto) y en los nombres (parecido). Devuelve el estado si encontró algo.</summary>
	private static RowStatus? Suggest(ElementRow row, Budget budget, BudgetMatcher matcher, string branch, string titleBranch)
	{
		var byCode = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
		var mismatched = new List<string>();
		foreach ((string code, string source) in row.CodeHits)
		{
			if (budget != null && budget.Find(code) == null)
			{
				continue;
			}

			string target = code;
			string from = source;
			if (!MatchesLinks(code, row.Linked))
			{
				BudgetItem equivalent = matcher?.Equivalent(code, branch);
				if (equivalent == null)
				{
					mismatched.Add(code);
					continue;
				}

				target = equivalent.Code;
				from = $"{source}: {code} → equivalente";
			}

			if (!byCode.ContainsKey(target))
			{
				byCode[target] = new Suggestion { Code = target, Score = 1, Source = from, ByCode = true };
			}
		}

		// Si hay varias, se prefieren las partidas finales y, entre ellas, las más profundas (03.01.04 frente a 03.01.04.01.01).
		List<Suggestion> codes = byCode.Values.Where(s => !byCode.Keys.Any(k => k != s.Code && PartidaCode.IsAncestorOrSelf(s.Code, k))).ToList();
		if (codes.Count > 1 && budget != null && codes.Any(s => budget.Find(s.Code)?.IsLeaf == true))
		{
			codes = codes.Where(s => budget.Find(s.Code)?.IsLeaf == true).ToList();
		}

		if (codes.Count > 0)
		{
			row.Suggestions = codes;
			row.Candidates = codes.Select(s => s.Code).Take(4).ToList();
			return codes.Count == 1 ? RowStatus.Sugerencia : RowStatus.Conflicto;
		}

		var byName = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
		if (matcher != null)
		{
			foreach ((string text, string source) in row.Texts)
			{
				foreach ((BudgetItem item, double score) in matcher.Match(text, titleBranch ?? branch))
				{
					if (!MatchesLinks(item.Code, row.Linked))
					{
						continue;
					}

					if (!byName.TryGetValue(item.Code, out Suggestion existing) || existing.Score < score)
					{
						byName[item.Code] = new Suggestion { Code = item.Code, Score = score, Source = source };
					}
				}
			}
		}

		if (byName.Count > 0)
		{
			// Las partidas finales antes que los títulos con el mismo parecido; empates (±5 %) quedan como opciones.
			List<Suggestion> ranked = byName.Values
				.OrderByDescending(s => s.Score)
				.ThenByDescending(s => budget?.Find(s.Code)?.IsLeaf == true)
				.ThenBy(s => s.Code, StringComparer.Ordinal)
				.ToList();
			double best = ranked[0].Score;
			List<Suggestion> close = ranked.Where(s => s.Score >= best - 0.05).Take(4).ToList();
			row.Suggestions = ranked.Take(6).ToList();
			row.Candidates = close.Select(s => s.Code).ToList();
			return close.Count == 1 ? RowStatus.Sugerencia : RowStatus.Conflicto;
		}

		if (mismatched.Count > 0)
		{
			row.Candidates = mismatched.Distinct().Take(4).ToList();
			row.AppendNote($"Hay códigos en sus parámetros ({string.Join(", ", row.Candidates)}) que no corresponden a {Describe(budget, branch)}.");
			return RowStatus.NoCoincide;
		}

		return null;
	}

	public static bool MatchesLinks(string code, string[] linked)
	{
		foreach (string link in linked ?? Array.Empty<string>())
		{
			if (!string.IsNullOrEmpty(link) && !PartidaCode.IsAncestorOrSelf(link, code))
			{
				return false;
			}
		}

		return true;
	}

	private static string Describe(Budget budget, string code)
	{
		if (string.IsNullOrEmpty(code))
		{
			return "el filtro";
		}

		string description = budget?.Find(code)?.Description?.Trim();
		return string.IsNullOrEmpty(description) ? code : $"{code} {description}";
	}
}
