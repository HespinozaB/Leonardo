using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RevitPartidas.Core;

namespace RevitPartidas.UI;

/// <summary>
/// Enlazar elementos con el presupuesto (doble clic en un grupo o elemento). Muestra su Código y Descripción de montaje, el
/// filtro (p. ej. la etapa) y las sugerencias; permite buscar la partida por código o por nombre (con parecido) y elegirla.
/// </summary>
internal sealed class LinkForm : Form
{
	private readonly List<ElementRow> _rows;
	private readonly Budget _budget;
	private readonly BudgetMatcher _matcher;
	private readonly string _branch;
	private readonly TextBox _search = new TextBox();
	private readonly CheckBox _onlyBranch = new CheckBox();
	private readonly CheckBox _onlyLeaves = new CheckBox();
	private readonly ListView _results = new ListView();
	private readonly Button _ok = new Button();

	public LinkForm(List<ElementRow> rows, Budget budget, BudgetMatcher matcher, PartidasConfig config, Action<IEnumerable<long>> showInRevit)
	{
		_rows = rows;
		_budget = budget;
		_matcher = matcher;

		// La rama común del filtro (p. ej. 03.01 si todos son de ETAPA 1).
		List<string> branches = rows.Select(r => r.LinkedChain).Distinct().ToList();
		_branch = branches.Count == 1 ? branches[0] : null;

		Text = $"Enlazar con el presupuesto · {rows.Count} elemento(s)";
		Font = new Font("Segoe UI", 9f);
		StartPosition = FormStartPosition.CenterParent;
		Size = new Size(1000, 680);
		MinimumSize = new Size(700, 450);
		ShowInTaskbar = false;
		MinimizeBox = false;

		var info = new TextBox
		{
			Dock = DockStyle.Top,
			Height = 130,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Vertical,
			Font = new Font("Consolas", 9f),
			Text = Summary(rows, budget, config)
		};

		var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
		bar.Controls.Add(new Label { Text = "Buscar partida (código o nombre):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_search.Width = 330;
		_search.TextChanged += (s, e) => Fill();
		bar.Controls.Add(_search);
		_onlyBranch.Text = _branch != null ? $"Solo en {_branch} {budget.Find(_branch)?.Description}" : "Solo en la rama del filtro";
		_onlyBranch.AutoSize = true;
		_onlyBranch.Checked = _branch != null;
		_onlyBranch.Enabled = _branch != null;
		_onlyBranch.Margin = new Padding(10, 5, 0, 0);
		_onlyBranch.CheckedChanged += (s, e) => Fill();
		bar.Controls.Add(_onlyBranch);
		_onlyLeaves.Text = "Solo partidas finales";
		_onlyLeaves.AutoSize = true;
		_onlyLeaves.Checked = true;
		_onlyLeaves.Margin = new Padding(10, 5, 0, 0);
		_onlyLeaves.CheckedChanged += (s, e) => Fill();
		bar.Controls.Add(_onlyLeaves);

		_results.Dock = DockStyle.Fill;
		_results.View = View.Details;
		_results.FullRowSelect = true;
		_results.GridLines = true;
		_results.MultiSelect = false;
		_results.HideSelection = false;
		_results.Columns.Add("Código", 140);
		_results.Columns.Add("Descripción", 560);
		_results.Columns.Add("Und.", 50);
		_results.Columns.Add("Por qué", 200);
		_results.SelectedIndexChanged += (s, e) => _ok.Enabled = _results.SelectedItems.Count > 0;
		_results.DoubleClick += (s, e) => Accept();

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
		var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
		_ok.Text = "Asignar esta partida";
		_ok.AutoSize = true;
		_ok.Font = new Font(Font, FontStyle.Bold);
		_ok.Enabled = false;
		_ok.Click += (s, e) => Accept();
		var show = new Button { Text = "Ver en Revit", AutoSize = true };
		show.Click += (s, e) => showInRevit(rows.Select(r => r.Id));
		bottom.Controls.AddRange(new Control[] { cancel, _ok, show });
		AcceptButton = _ok;
		CancelButton = cancel;

		Controls.Add(_results);
		Controls.Add(bar);
		Controls.Add(info);
		Controls.Add(bottom);
		Fill();
	}

	/// <summary>Código elegido (tal cual en la lista).</summary>
	public string ChosenCode { get; private set; }

	/// <summary>El código para un elemento: el elegido, o su equivalente si el elemento es de otra rama del filtro.</summary>
	public string ChosenFor(ElementRow row) => InBranch(row, ChosenCode, _matcher);

	public static string InBranch(ElementRow row, string code, BudgetMatcher matcher)
	{
		if (code == null || Classifier.MatchesLinks(code, row.Linked))
		{
			return code;
		}

		return matcher?.Equivalent(code, row.LinkedChain)?.Code ?? code;
	}

	private void Accept()
	{
		if (_results.SelectedItems.Count == 0 || !(_results.SelectedItems[0].Tag is BudgetItem item))
		{
			return;
		}

		if (!item.IsLeaf && MessageBox.Show(this, $"{item.Code} es un título, no una partida final. ¿Usarlo igualmente?", Text,
				MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
		{
			return;
		}

		ChosenCode = item.Code;
		DialogResult = DialogResult.OK;
		Close();
	}

	/// <summary>
	/// Sin búsqueda: las sugerencias del grupo, las partidas del título de su código de montaje y las más parecidas a su nombre.
	/// Con búsqueda: por código (empieza por) o por nombre (todas las palabras, más las parecidas).
	/// </summary>
	private void Fill()
	{
		string branch = _onlyBranch.Checked ? _branch : null;
		var items = new List<(BudgetItem Item, string Why)>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		void Add(BudgetItem item, string why)
		{
			if (item != null && (branch == null || PartidaCode.IsAncestorOrSelf(branch, item.Code)) && (!_onlyLeaves.Checked || item.IsLeaf) && seen.Add(item.Code))
			{
				items.Add((item, why));
			}
		}

		string search = _search.Text.Trim();
		if (search.Length == 0)
		{
			foreach (Suggestion suggestion in _rows.SelectMany(r => r.Suggestions).GroupBy(x => x.Code).Select(g => g.OrderByDescending(x => x.Score).First())
						 .OrderByDescending(x => x.ByCode).ThenByDescending(x => x.Score))
			{
				Add(_budget.Find(suggestion.Code), suggestion.ByCode ? $"código en {suggestion.Source}" : $"{suggestion.Score:P0} por {suggestion.Source}");
			}

			foreach (string montaje in _rows.Select(r => PartidaCode.Normalize(r.AssemblyCode)).Where(c => c != null).Distinct())
			{
				BudgetItem item = _budget.Find(montaje);
				if (item != null && item.IsLeaf)
				{
					Add(_matcher.Equivalent(montaje, branch) ?? item, "código de montaje");
				}
				else if (item != null)
				{
					foreach (BudgetItem child in Descendants(item))
					{
						Add(child, "dentro del título de montaje");
					}
				}
			}

			foreach (string name in _rows.Select(r => r.FamilyType).Distinct().Take(5))
			{
				foreach ((BudgetItem item, double score) in _matcher.Match(name, branch))
				{
					Add(item, $"{score:P0} por el nombre");
				}
			}
		}
		else
		{
			string code = PartidaCode.Normalize(search);
			string simple = BudgetReader.Simplify(search);
			string[] words = simple.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (BudgetItem item in _budget.All.OrderBy(i => i.Code, StringComparer.Ordinal))
			{
				if ((code != null && item.Code.StartsWith(search.Trim(), StringComparison.Ordinal)) ||
					(words.Length > 0 && words.All(w => BudgetReader.Simplify(item.Description).Contains(w))))
				{
					Add(item, code != null ? "código" : "nombre");
				}
			}

			foreach ((BudgetItem item, double score) in _matcher.Match(search, branch))
			{
				Add(item, $"{score:P0} parecido");
			}
		}

		_results.BeginUpdate();
		_results.Items.Clear();
		foreach (var (item, why) in items.Take(400))
		{
			_results.Items.Add(new ListViewItem(new[] { item.Code, item.Description, item.Unit, why })
			{
				Tag = item,
				ForeColor = item.IsLeaf ? SystemColors.WindowText : Color.Gray
			});
		}

		_results.EndUpdate();
		if (_results.Items.Count > 0)
		{
			_results.Items[0].Selected = true;
		}

		_ok.Enabled = _results.SelectedItems.Count > 0;
	}

	private static IEnumerable<BudgetItem> Descendants(BudgetItem item)
	{
		foreach (BudgetItem child in item.Children)
		{
			yield return child;
			foreach (BudgetItem grandchild in Descendants(child))
			{
				yield return grandchild;
			}
		}
	}

	private static string Summary(List<ElementRow> rows, Budget budget, PartidasConfig config)
	{
		var lines = new List<string>
		{
			$"Elementos: {rows.Count}   Categorías: {string.Join(", ", rows.Select(r => r.Category).Distinct().Take(5))}",
			"Familia : Tipo: " + string.Join(" | ", rows.Select(r => r.FamilyType).Distinct().Take(4)) +
				(rows.Select(r => r.FamilyType).Distinct().Count() > 4 ? " …" : string.Empty)
		};
		foreach (var montaje in rows.GroupBy(r => (r.AssemblyCode, r.AssemblyDescription)).Take(4))
		{
			string code = montaje.Key.AssemblyCode.Length > 0 ? montaje.Key.AssemblyCode : "(vacío)";
			string description = montaje.Key.AssemblyDescription.Length > 0 ? montaje.Key.AssemblyDescription
				: budget.Find(PartidaCode.Normalize(montaje.Key.AssemblyCode))?.Description ?? string.Empty;
			lines.Add($"Código de montaje: {code}   Descripción de montaje: {description}   ({montaje.Count()} elem.)");
		}

		for (int i = 0; i < config.Levels; i++)
		{
			LevelLink link = config.Link(i);
			if (link.Mode == LinkMode.Parametro)
			{
				lines.Add($"Filtro nivel {i + 1} ({link.ParameterName}): " + string.Join(", ", rows
					.GroupBy(r => r.LinkValues != null && i < r.LinkValues.Length && r.LinkValues[i].Length > 0 ? r.LinkValues[i] : "(vacío)")
					.Select(g => $"{g.Key} ×{g.Count()}")));
			}
		}

		string note = rows.Select(r => r.Note).FirstOrDefault(n => !string.IsNullOrEmpty(n));
		if (note != null)
		{
			lines.Add("Nota: " + note);
		}

		return string.Join(Environment.NewLine, lines);
	}
}
