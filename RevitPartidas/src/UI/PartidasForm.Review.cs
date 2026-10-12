using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using RevitPartidas.Commands;
using RevitPartidas.Core;

namespace RevitPartidas.UI;

/// <summary>Paso 4: revisión. Árbol del presupuesto a la izquierda; resultados agrupados por categoría y nota a la derecha.</summary>
internal sealed partial class PartidasForm
{
	private const int FilterReview = 0;
	private const int FilterPending = 1;
	private const int FilterWithout = 2;
	private const int FilterOk = 3;
	private const int FilterAll = 4;
	private const int FilterNode = 5;
	private const int FilterRevit = 6;

	/// <summary>Elementos que se cargan como máximo al expandir un grupo (el resto se trabaja desde el grupo).</summary>
	private const int MaxElementNodes = 500;

	private static readonly string[] Filters =
	{
		"Por revisar (conflictos, no coincide, no está, título, difiere)", "Pendientes de escribir", "Sin partida", "OK", "Todos",
		"De la partida marcada en el presupuesto", "Selección actual de Revit"
	};

	private static readonly Dictionary<RowStatus, string> StatusText = new Dictionary<RowStatus, string>
	{
		[RowStatus.Conflicto] = "Conflicto",
		[RowStatus.NoCoincide] = "No coincide con el enlace",
		[RowStatus.NoEnPresupuesto] = "No está en el presupuesto",
		[RowStatus.Titulo] = "Es título",
		[RowStatus.Difiere] = "Difiere de lo escrito",
		[RowStatus.Pendiente] = "Pendiente",
		[RowStatus.Parcial] = "Solo niveles superiores",
		[RowStatus.SinPartida] = "Sin partida",
		[RowStatus.Ok] = "OK"
	};

	private static readonly Dictionary<RowStatus, Color> StatusColor = new Dictionary<RowStatus, Color>
	{
		[RowStatus.Conflicto] = Color.FromArgb(255, 205, 205),
		[RowStatus.NoCoincide] = Color.FromArgb(255, 205, 205),
		[RowStatus.NoEnPresupuesto] = Color.FromArgb(255, 205, 205),
		[RowStatus.Titulo] = Color.FromArgb(255, 225, 190),
		[RowStatus.Difiere] = Color.FromArgb(255, 225, 190),
		[RowStatus.Pendiente] = Color.FromArgb(255, 248, 200),
		[RowStatus.Parcial] = Color.FromArgb(255, 248, 200),
		[RowStatus.SinPartida] = Color.FromArgb(235, 235, 235),
		[RowStatus.Ok] = Color.FromArgb(215, 240, 215)
	};

	/// <summary>Lo que representa un nodo del árbol de resultados.</summary>
	private sealed class NodeInfo
	{
		public List<ElementRow> Rows;

		public ElementRow Row;

		public RowStatus? Status;

		public bool Loaded;
	}

	// Presupuesto (izquierda).
	private readonly TextBox _treeSearch = new TextBox();
	private readonly CheckBox _onlyEmpty = new CheckBox();
	private readonly TreeView _budgetTree = new TreeView();
	private readonly Button _assign = new Button();
	private readonly Button _selectNode = new Button();
	private readonly Dictionary<string, TreeNode> _budgetNodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);

	// Resultados (derecha).
	private readonly ComboBox _filter = new ComboBox();
	private readonly TextBox _search = new TextBox();
	private readonly Button _rescan = new Button();
	private readonly TreeView _results = new TreeView();
	private readonly TextBox _details = new TextBox();
	private readonly Label _summary = new Label();

	// Acciones.
	private readonly Button _writePending = new Button();
	private readonly Button _resolve3D = new Button();
	private readonly Button _clear = new Button();
	private readonly Button _selectRows = new Button();
	private readonly Button _useRevitSelection = new Button();
	private readonly Button _export = new Button();

	private List<ElementRow> _rows = new List<ElementRow>();
	private Dictionary<string, int> _countByCode = new Dictionary<string, int>(StringComparer.Ordinal);
	private HashSet<long> _revitSelection = new HashSet<long>();
	private Dictionary<long, string> _searchKeys = new Dictionary<long, string>();
	private bool _syncingChecks;

	#region Layout

	private TabPage BuildReviewPage()
	{
		var page = new TabPage("4 · Revisión");
		var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
		split.Panel1.Controls.Add(BuildBudgetPanel());
		split.Panel2.Controls.Add(BuildResultsPanel());
		bool splitSet = false;
		split.SizeChanged += (s, e) =>
		{
			// El ancho final solo se conoce al mostrarse: se fija una vez.
			if (!splitSet && split.Width > 900)
			{
				splitSet = true;
				Safe(() => split.SplitterDistance = 440);
			}
		};

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
		Setup(_writePending, "Escribir pendientes", WritePending, bold: true);
		Setup(_resolve3D, "Resolver en 3D…", Resolve3D, bold: true);
		_resolve3D.ForeColor = Color.Firebrick;
		Setup(_clear, "Vaciar parámetros", ClearTargets);
		Setup(_selectRows, "Seleccionar en Revit", () => SelectInRevit(Targets().Select(r => r.Id)));
		Setup(_useRevitSelection, "Usar selección de Revit", () => Raise(PartidasAction.ReadSelection, null));
		Setup(_export, "Exportar CSV…", ExportCsv);
		bottom.Controls.AddRange(new Control[] { _writePending, _resolve3D, _clear, _selectRows, _useRevitSelection, _export });

		page.Controls.Add(split);
		page.Controls.Add(bottom);
		return page;
	}

	private void Setup(Button button, string text, Action action, bool bold = false)
	{
		button.Text = text;
		button.AutoSize = true;
		button.Click += Do(action);
		if (bold)
		{
			button.Font = new Font(Font, FontStyle.Bold);
		}
	}

	private Control BuildBudgetPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill };
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 4, 0), WrapContents = false };
		top.Controls.Add(NewLabel("Presupuesto:"));
		_treeSearch.Width = 150;
		_treeSearch.TextChanged += (s, e) => Safe(BuildBudgetTree);
		top.Controls.Add(_treeSearch);
		_onlyEmpty.Text = "Solo sin elementos";
		_onlyEmpty.AutoSize = true;
		_onlyEmpty.Margin = new Padding(8, 5, 0, 0);
		_onlyEmpty.CheckedChanged += (s, e) => Safe(BuildBudgetTree);
		top.Controls.Add(_onlyEmpty);

		_budgetTree.Dock = DockStyle.Fill;
		_budgetTree.HideSelection = false;
		_budgetTree.ShowNodeToolTips = true;
		_budgetTree.AfterSelect += (s, e) => Safe(() =>
		{
			UpdateButtons();
			if (_filter.SelectedIndex == FilterNode)
			{
				Populate();
			}
		});
		_budgetTree.NodeMouseDoubleClick += (s, e) => Safe(() =>
		{
			_budgetTree.SelectedNode = e.Node;
			if (_filter.SelectedIndex != FilterNode)
			{
				_filter.SelectedIndex = FilterNode;
			}
			else
			{
				Populate();
			}
		});

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(6, 4, 4, 0) };
		Setup(_assign, "Asignar esta partida a los marcados  →", AssignNode, bold: true);
		Setup(_selectNode, "Ver sus elementos en Revit", () => SelectInRevit(RowsOfBudgetNode().Select(r => r.Id)));
		bottom.Controls.AddRange(new Control[] { _assign, _selectNode });
		bottom.SetFlowBreak(_selectNode, true);
		bottom.Controls.Add(new Label
		{
			AutoSize = true,
			ForeColor = Color.DimGray,
			Text = "Verde: con elementos · Rojo: sin elementos · Gris: \"Sin tabla en el modelo BIM\". Doble clic: ver sus elementos."
		});

		panel.Controls.Add(_budgetTree);
		panel.Controls.Add(bottom);
		panel.Controls.Add(top);
		return panel;
	}

	private Control BuildResultsPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill };
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4, 6, 8, 0), WrapContents = false };
		top.Controls.Add(NewLabel("Mostrar:"));
		_filter.DropDownStyle = ComboBoxStyle.DropDownList;
		_filter.Width = 330;
		_filter.Items.AddRange(Filters);
		_filter.SelectedIndex = FilterReview;
		_filter.SelectedIndexChanged += (s, e) => Safe(Populate);
		top.Controls.Add(_filter);
		top.Controls.Add(NewLabel("Buscar:"));
		_search.Width = 170;
		_search.TextChanged += (s, e) => Safe(Populate);
		top.Controls.Add(_search);
		Setup(_rescan, "Analizar de nuevo", RequestScan);
		top.Controls.Add(_rescan);

		_results.Dock = DockStyle.Fill;
		_results.CheckBoxes = true;
		_results.HideSelection = false;
		_results.ShowNodeToolTips = true;
		_results.BeforeExpand += (s, e) => Safe(() => LoadElements(e.Node));
		_results.AfterCheck += (s, e) => Safe(() => SyncChecks(e.Node));
		_results.AfterSelect += (s, e) => Safe(() =>
		{
			ShowDetails(e.Node);
			UpdateButtons();
		});
		_results.NodeMouseDoubleClick += (s, e) => Safe(() =>
		{
			if (e.Node.Tag is NodeInfo { Row: not null } info)
			{
				SelectInRevit(new[] { info.Row.Id });
			}
		});

		_details.Dock = DockStyle.Bottom;
		_details.Height = 150;
		_details.Multiline = true;
		_details.ReadOnly = true;
		_details.ScrollBars = ScrollBars.Vertical;
		_details.BackColor = SystemColors.Window;
		_details.Font = new Font("Consolas", 9f);

		_summary.Dock = DockStyle.Bottom;
		_summary.Height = 22;
		_summary.Padding = new Padding(4, 4, 0, 0);
		_summary.ForeColor = Color.DimGray;

		panel.Controls.Add(_results);
		panel.Controls.Add(_details);
		panel.Controls.Add(_summary);
		panel.Controls.Add(top);
		return panel;
	}

	#endregion

	#region Análisis y resultados

	private void RequestScan()
	{
		_scanSignature = Signature();
		Raise(PartidasAction.Scan, "Analizando el modelo…");
	}

	private void SetScan(ScanResult result, string title, string message)
	{
		_rows = result.Rows;
		_documentTitle = title;
		Text = "EMASY · Partidas · " + title;
		_searchKeys = new Dictionary<long, string>();
		int missing = _config.Levels - result.ParametersBound;
		Done(message ?? $"Análisis terminado en {result.Elapsed.TotalSeconds:0.0} s: {_rows.Count} elementos." +
			(missing > 0 ? $"  Faltan {missing} parámetro(s) en el modelo: créalos en el paso 2." : string.Empty));
		RefreshAll();
	}

	private void SetWritten(Dictionary<long, string[]> written, string message)
	{
		Done(message);
		foreach (ElementRow row in _rows)
		{
			if (written.TryGetValue(row.Id, out string[] values))
			{
				row.Current = values;
				row.HasDycusaParameters = true;
				row.DycusaCode = PartidaCode.Deepest(values, out bool consistent);
				row.DycusaInconsistent = !consistent;
				ElementScanner.Evaluate(row, _budget, _config);
			}
		}

		RefreshAll();
		if (_resolveForm != null && !_resolveForm.IsDisposed && written.Keys.Any(id => _resolveRows.Any(r => r.Id == id)))
		{
			_resolveForm.CloseAfterDecision();
		}
	}

	private void ReevaluateAll()
	{
		foreach (ElementRow row in _rows)
		{
			ElementScanner.Evaluate(row, _budget, _config);
		}

		RefreshAll();
	}

	private void RefreshAll()
	{
		_countByCode = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (ElementRow row in _rows.Where(r => r.ProposedCode != null))
		{
			foreach (string level in PartidaCode.Levels(row.ProposedCode, PartidaCode.MaxLevels).Where(l => l.Length > 0))
			{
				_countByCode[level] = Count(level) + 1;
			}
		}

		if (_onlyEmpty.Checked)
		{
			BuildBudgetTree();
		}
		else
		{
			UpdateBudgetCounts();
		}

		Populate();
	}

	private void SetRevitSelection(List<long> ids)
	{
		Done($"{ids.Count} elemento(s) seleccionados en Revit.");
		_revitSelection = new HashSet<long>(ids);
		if (_filter.SelectedIndex != FilterRevit)
		{
			_filter.SelectedIndex = FilterRevit;
		}
		else
		{
			Populate();
		}

		// Todo lo mostrado queda marcado: las acciones se aplican a esa selección.
		_syncingChecks = true;
		foreach (TreeNode node in _results.Nodes)
		{
			node.Checked = true;
			foreach (TreeNode child in node.Nodes)
			{
				child.Checked = true;
			}
		}

		_syncingChecks = false;
		UpdateButtons();
	}

	private IEnumerable<ElementRow> FilteredRows()
	{
		string search = BudgetReader.Simplify(_search.Text);
		string nodeCode = (_budgetTree.SelectedNode?.Tag as BudgetItem)?.Code;
		int filter = _filter.SelectedIndex;
		foreach (ElementRow row in _rows)
		{
			bool visible;
			switch (filter)
			{
				case FilterReview:
					visible = row.Status <= RowStatus.Difiere;
					break;
				case FilterPending:
					visible = row.Status == RowStatus.Pendiente || row.Status == RowStatus.Parcial;
					break;
				case FilterWithout:
					visible = (row.Status == RowStatus.SinPartida || row.Status == RowStatus.Parcial) && row.InCodedCategory;
					break;
				case FilterOk:
					visible = row.Status == RowStatus.Ok;
					break;
				case FilterNode:
					visible = nodeCode != null && row.ProposedCode != null && PartidaCode.IsAncestorOrSelf(nodeCode, row.ProposedCode);
					break;
				case FilterRevit:
					visible = _revitSelection.Contains(row.Id);
					break;
				default:
					visible = row.InCodedCategory || (row.Status != RowStatus.SinPartida && row.Status != RowStatus.Parcial);
					break;
			}

			if (visible && (search.Length == 0 || SearchKey(row).Contains(search)))
			{
				yield return row;
			}
		}
	}

	private string SearchKey(ElementRow row)
	{
		if (!_searchKeys.TryGetValue(row.Id, out string key))
		{
			key = BudgetReader.Simplify(string.Join(" ", row.Id, row.Category, row.FamilyType, row.Level, row.SourceParam, row.SourceCode,
				row.ProposedCode, string.Join(" ", row.Candidates), row.Note));
			_searchKeys[row.Id] = key;
		}

		return key;
	}

	/// <summary>Etiqueta del grupo (la "nota" común) dentro de una categoría.</summary>
	private string GroupLabel(ElementRow row)
	{
		string Describe(string code)
		{
			string description = _budget?.Find(code)?.Description;
			return string.IsNullOrEmpty(description) ? code : $"{code} {Shorten(description, 45)}";
		}

		switch (row.Status)
		{
			case RowStatus.Conflicto:
				return "Conflicto · " + string.Join("  /  ", row.Candidates.Select(Describe));
			case RowStatus.NoCoincide:
				return $"No coincide con {LinkText(row)} · " + string.Join(" / ", row.Candidates.Count > 0 ? row.Candidates : new List<string> { row.ProposedCode });
			case RowStatus.NoEnPresupuesto:
				return "No está en el presupuesto · " + row.ProposedCode;
			case RowStatus.Titulo:
				return "Es título · " + Describe(row.ProposedCode);
			case RowStatus.Difiere:
				return $"Difiere · origen {row.SourceCode} ≠ escrito {row.DycusaCode}";
			case RowStatus.Pendiente:
				return "Pendiente · " + Describe(row.ProposedCode);
			case RowStatus.Parcial:
				return "Solo niveles superiores · " + Describe(row.LinkedChain);
			case RowStatus.SinPartida:
				return "Sin partida · " + row.FamilyType;
			default:
				return "OK · " + Describe(row.ProposedCode);
		}
	}

	private string LinkText(ElementRow row)
	{
		var parts = new List<string>();
		for (int i = 0; i < (row.Linked?.Length ?? 0); i++)
		{
			if (!string.IsNullOrEmpty(row.Linked[i]))
			{
				parts.Add(_budget?.Find(row.Linked[i])?.Description ?? row.Linked[i]);
			}
		}

		return parts.Count == 0 ? "el enlace" : string.Join(" / ", parts);
	}

	private static string Shorten(string text, int length) =>
		string.IsNullOrEmpty(text) || text.Length <= length ? text : text.Substring(0, length - 1).TrimEnd() + "…";

	private void Populate()
	{
		// Se conserva lo expandido y lo seleccionado al reconstruir.
		var expanded = new HashSet<string>(AllNodes(_results.Nodes).Where(n => n.IsExpanded).Select(n => n.Name));
		string selected = _results.SelectedNode?.Name;

		List<ElementRow> rows = FilteredRows().ToList();
		_results.BeginUpdate();
		_results.Nodes.Clear();
		foreach (var category in rows.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
		{
			var categoryRows = category.ToList();
			var categoryNode = new TreeNode($"{category.Key}   [{categoryRows.Count}]")
			{
				Name = "c:" + category.Key,
				Tag = new NodeInfo { Rows = categoryRows, Loaded = true },
				NodeFont = new Font(_results.Font, FontStyle.Bold)
			};
			foreach (var group in categoryRows.GroupBy(r => (r.Status, Label: GroupLabel(r)))
						 .OrderBy(g => g.Key.Status).ThenBy(g => g.Key.Label, StringComparer.Ordinal))
			{
				var groupRows = group.ToList();
				var groupNode = new TreeNode($"{group.Key.Label}   [{groupRows.Count}]")
				{
					Name = $"g:{category.Key}|{group.Key.Label}",
					Tag = new NodeInfo { Rows = groupRows, Status = group.Key.Status },
					BackColor = StatusColor[group.Key.Status],
					ToolTipText = StatusText[group.Key.Status]
				};
				groupNode.Nodes.Add(new TreeNode("…"));
				categoryNode.Nodes.Add(groupNode);
			}

			_results.Nodes.Add(categoryNode);
		}

		foreach (TreeNode node in AllNodes(_results.Nodes).ToList())
		{
			if (expanded.Contains(node.Name))
			{
				node.Expand();
			}
		}

		if (selected != null)
		{
			_results.SelectedNode = AllNodes(_results.Nodes).FirstOrDefault(n => n.Name == selected);
		}

		_results.EndUpdate();
		if (_results.SelectedNode == null)
		{
			ShowDetails(null);
		}

		IEnumerable<ElementRow> visible = _rows.Where(r => r.InCodedCategory || (r.Status != RowStatus.SinPartida && r.Status != RowStatus.Parcial));
		string counts = string.Join(" · ", visible.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{StatusText[g.Key]}: {g.Count()}"));
		_summary.Text = $"Mostrando {rows.Count}  ·  {counts}";
		UpdateButtons();
	}

	private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
	{
		foreach (TreeNode node in nodes)
		{
			yield return node;
			foreach (TreeNode child in AllNodes(node.Nodes))
			{
				yield return child;
			}
		}
	}

	/// <summary>Los elementos de un grupo se crean al expandirlo (así la lista se arma rápido aunque haya miles).</summary>
	private void LoadElements(TreeNode node)
	{
		if (!(node.Tag is NodeInfo info) || info.Loaded)
		{
			return;
		}

		info.Loaded = true;
		_results.BeginUpdate();
		node.Nodes.Clear();
		foreach (ElementRow row in info.Rows.Take(MaxElementNodes))
		{
			node.Nodes.Add(new TreeNode($"{row.Id} · {row.FamilyType} · {row.Level}")
			{
				Name = "e:" + row.Id,
				Tag = new NodeInfo { Rows = new List<ElementRow> { row }, Row = row, Loaded = true },
				Checked = node.Checked
			});
		}

		if (info.Rows.Count > MaxElementNodes)
		{
			node.Nodes.Add(new TreeNode($"… y {info.Rows.Count - MaxElementNodes} más (marca el grupo para trabajar con todos)") { ForeColor = Color.DimGray });
		}

		_results.EndUpdate();
	}

	private void SyncChecks(TreeNode node)
	{
		if (_syncingChecks)
		{
			return;
		}

		_syncingChecks = true;
		foreach (TreeNode child in AllNodes(node.Nodes))
		{
			child.Checked = node.Checked;
		}

		// Al desmarcar un hijo, el padre deja de representar a todos.
		if (!node.Checked)
		{
			for (TreeNode parent = node.Parent; parent != null; parent = parent.Parent)
			{
				parent.Checked = false;
			}
		}

		_syncingChecks = false;
		UpdateButtons();
	}

	/// <summary>Los elementos marcados con casilla; si no hay ninguno, los del nodo seleccionado.</summary>
	private List<ElementRow> Targets()
	{
		var rows = new Dictionary<long, ElementRow>();
		void Collect(TreeNodeCollection nodes)
		{
			foreach (TreeNode node in nodes)
			{
				if (node.Checked && node.Tag is NodeInfo info)
				{
					foreach (ElementRow row in info.Rows)
					{
						rows[row.Id] = row;
					}
				}
				else
				{
					Collect(node.Nodes);
				}
			}
		}

		Collect(_results.Nodes);
		if (rows.Count == 0 && _results.SelectedNode?.Tag is NodeInfo selected)
		{
			foreach (ElementRow row in selected.Rows)
			{
				rows[row.Id] = row;
			}
		}

		return rows.Values.ToList();
	}

	private void ShowDetails(TreeNode node)
	{
		if (!(node?.Tag is NodeInfo info))
		{
			_details.Text = "Selecciona un grupo o un elemento para ver el detalle. Marca las casillas para aplicar una acción a varios a la vez.";
			return;
		}

		var text = new StringBuilder();
		if (info.Row != null)
		{
			ElementRow row = info.Row;
			text.AppendLine($"Elemento {row.Id} · {row.Category} · {row.FamilyType} · {row.Level}");
			text.AppendLine($"Estado: {StatusText[row.Status]}");
			if (row.SourceCode != null)
			{
				text.AppendLine($"Origen: {row.SourceCode}  ({row.SourceParam})");
			}

			if (row.Candidates.Count > 0)
			{
				text.AppendLine("Opciones: " + string.Join("  |  ", row.Candidates.Select(Describe)) + $"  ({row.SourceParam})");
			}

			string[] names = _config.Names;
			text.AppendLine("Nivel  Parámetro                 Escrito              Propuesto            Enlace");
			for (int i = 0; i < names.Length; i++)
			{
				string current = i < row.Current.Length ? row.Current[i] ?? "(sin parámetro)" : string.Empty;
				string proposed = i < row.Proposed.Length ? row.Proposed[i] : string.Empty;
				string linked = row.Linked != null && i < row.Linked.Length ? row.Linked[i] : string.Empty;
				text.AppendLine($"{i + 1,-6} {Shorten(names[i], 25),-25} {current,-20} {proposed,-20} {linked}");
			}

			if (!string.IsNullOrEmpty(row.Note))
			{
				text.AppendLine("Nota: " + row.Note);
			}
		}
		else if (info.Status.HasValue)
		{
			text.AppendLine($"{node.Text}");
			text.AppendLine(Advice(info.Status.Value));
			var candidates = info.Rows.SelectMany(r => r.Candidates).Distinct().ToList();
			foreach (string candidate in candidates)
			{
				text.AppendLine("  · " + Describe(candidate));
			}

			string sources = string.Join(", ", info.Rows.Select(r => r.SourceParam).Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(6));
			if (sources.Length > 0)
			{
				text.AppendLine("Parámetros de origen: " + sources);
			}

			string note = info.Rows.Select(r => r.Note).FirstOrDefault(n => !string.IsNullOrEmpty(n));
			if (note != null)
			{
				text.AppendLine("Nota (ejemplo): " + note);
			}
		}
		else
		{
			text.AppendLine(node.Text);
			foreach (var group in info.Rows.GroupBy(r => r.Status).OrderBy(g => g.Key))
			{
				text.AppendLine($"  {StatusText[group.Key]}: {group.Count()}");
			}
		}

		_details.Text = text.ToString();
	}

	private string Describe(string code)
	{
		BudgetItem item = _budget?.Find(code);
		return item == null ? $"{code} (no está en el presupuesto)" : $"{code} {item.Description}" + (item.IsLeaf ? string.Empty : " (título)");
	}

	private static string Advice(RowStatus status)
	{
		switch (status)
		{
			case RowStatus.Conflicto:
				return "El elemento tiene varias partidas posibles. Pulsa \"Resolver en 3D…\" para verlas pintadas y elegir con un clic.";
			case RowStatus.NoCoincide:
				return "Su código no corresponde al nivel enlazado (p. ej. código de ETAPA 2 en un elemento de ETAPA 1). Marca la partida correcta en el presupuesto y pulsa \"Asignar\", o resuélvelo en 3D.";
			case RowStatus.NoEnPresupuesto:
				return "El código no existe en el presupuesto: marca la partida correcta a la izquierda y pulsa \"Asignar\".";
			case RowStatus.Titulo:
				return "El código es un título: asigna la partida final correcta desde el presupuesto.";
			case RowStatus.Difiere:
				return "Lo escrito y el parámetro de origen no coinciden. Activa en el paso 3 \"Los parámetros de origen mandan\" o asigna la correcta.";
			case RowStatus.Pendiente:
				return "Listo para escribir: pulsa \"Escribir pendientes\".";
			case RowStatus.Parcial:
				return "Sin partida, pero los enlaces dan los niveles superiores: \"Escribir pendientes\" los escribe; la partida final se asigna desde el presupuesto.";
			case RowStatus.SinPartida:
				return "Ningún parámetro tiene código: marca la partida a la izquierda y pulsa \"Asignar\".";
			default:
				return "Completo y coherente con el presupuesto.";
		}
	}

	private void UpdateButtons()
	{
		bool idle = !_busy;
		foreach (Control control in new Control[] { _loadBudget, _createParams, _suggest, _analyze, _rescan, _writePending, _clear, _selectRows, _useRevitSelection, _export })
		{
			control.Enabled = idle;
		}

		bool hasTarget = _results.SelectedNode != null || AllNodes(_results.Nodes).Any(n => n.Checked);
		_assign.Enabled = idle && hasTarget && _budgetTree.SelectedNode != null;
		_selectNode.Enabled = idle && _budgetTree.SelectedNode != null;
		_resolve3D.Enabled = idle && hasTarget;
		int pending = _rows.Count(r => r.Status == RowStatus.Pendiente || r.Status == RowStatus.Parcial);
		_writePending.Text = $"Escribir pendientes ({pending})";
	}

	#endregion

	#region Árbol del presupuesto

	private void BuildBudgetTree()
	{
		_budgetTree.BeginUpdate();
		_budgetTree.Nodes.Clear();
		_budgetNodes.Clear();
		if (_budget != null)
		{
			string search = BudgetReader.Simplify(_treeSearch.Text);
			foreach (BudgetItem root in _budget.Roots)
			{
				TreeNode node = BuildBudgetNode(root, search);
				if (node != null)
				{
					_budgetTree.Nodes.Add(node);
				}
			}

			if (search.Length > 0 || _onlyEmpty.Checked)
			{
				_budgetTree.ExpandAll();
			}
			else
			{
				foreach (TreeNode node in _budgetTree.Nodes)
				{
					node.Expand();
					foreach (TreeNode child in node.Nodes)
					{
						child.Expand();
					}
				}
			}
		}

		_budgetTree.EndUpdate();
		UpdateBudgetCounts();
	}

	private TreeNode BuildBudgetNode(BudgetItem item, string search)
	{
		var children = item.Children.Select(c => BuildBudgetNode(c, search)).Where(n => n != null).ToList();
		bool matches = (search.Length == 0 || item.Code.StartsWith(search, StringComparison.Ordinal) ||
				BudgetReader.Simplify(item.Description).Contains(search)) &&
			(!_onlyEmpty.Checked || (item.IsLeaf && Count(item.Code) == 0));
		if (!matches && children.Count == 0)
		{
			return null;
		}

		var node = new TreeNode { Tag = item, ToolTipText = Tooltip(item) };
		node.Nodes.AddRange(children.ToArray());
		_budgetNodes[item.Code] = node;
		return node;
	}

	private static string Tooltip(BudgetItem item)
	{
		var text = new StringBuilder(item.Code + "  " + item.Description);
		if (item.IsLeaf)
		{
			text.Append($"\nUnidad: {item.Unit}");
			if (item.Metrado.HasValue)
			{
				text.Append($" · Metrado ppto.: {item.Metrado.Value.ToString("0.##", CultureInfo.CurrentCulture)}");
			}
		}

		if (!string.IsNullOrEmpty(item.Observation))
		{
			text.Append("\n" + item.Observation);
		}

		return text.ToString();
	}

	private int Count(string code) => code != null && _countByCode.TryGetValue(code, out int count) ? count : 0;

	private void UpdateBudgetCounts()
	{
		_budgetTree.BeginUpdate();
		foreach (TreeNode node in _budgetNodes.Values)
		{
			var item = (BudgetItem)node.Tag;
			int count = Count(item.Code);
			node.Text = $"{item.Code}  {item.Description}" + (count > 0 ? $"   [{count}]" : item.IsLeaf ? "   [0]" : string.Empty);
			node.ForeColor = !item.IsLeaf ? (count > 0 ? Color.Black : Color.Gray)
				: count > 0 ? Color.ForestGreen
				: item.WithoutBimTable ? Color.Gray
				: Color.Firebrick;
			node.NodeFont = item.IsLeaf ? null : new Font(_budgetTree.Font, FontStyle.Bold);
		}

		_budgetTree.EndUpdate();
	}

	private IEnumerable<ElementRow> RowsOfBudgetNode()
	{
		string code = (_budgetTree.SelectedNode?.Tag as BudgetItem)?.Code;
		return code == null
			? Enumerable.Empty<ElementRow>()
			: _rows.Where(r => r.ProposedCode != null && PartidaCode.IsAncestorOrSelf(code, r.ProposedCode));
	}

	#endregion
}
