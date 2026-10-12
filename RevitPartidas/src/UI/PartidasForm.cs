using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using RevitPartidas.Commands;
using RevitPartidas.Core;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;

namespace RevitPartidas.UI;

/// <summary>
/// Ventana no modal. A la izquierda, el presupuesto como árbol (con cuántos elementos tiene cada partida); a la derecha, los
/// elementos del modelo con su partida de origen, la propuesta DYCUSA_PARTIDA 1…6 y su estado.
/// </summary>
internal sealed class PartidasForm : Form
{
	private static readonly string[] ColumnNames =
	{
		"Id", "Estado", "Categoría", "Familia : Tipo", "Nivel", "Parámetro de origen", "Código de origen", "DYCUSA actual",
		"PARTIDA 1", "PARTIDA 2", "PARTIDA 3", "PARTIDA 4", "PARTIDA 5", "PARTIDA 6", "Nota"
	};

	private static readonly int[] ColumnWidths = { 70, 110, 120, 220, 110, 190, 120, 120, 45, 60, 75, 95, 110, 125, 300 };

	private const int FilterAll = 0;
	private const int FilterReview = 1;
	private const int FilterPending = 2;
	private const int FilterWithout = 3;
	private const int FilterOk = 4;
	private const int FilterNode = 5;
	private const int FilterRevit = 6;

	private static readonly string[] Filters =
	{
		"Todos", "Por revisar (conflicto, no está, título, difiere)", "Pendientes de escribir", "Sin partida", "OK",
		"De la partida marcada en el árbol", "Selección actual de Revit"
	};

	private static readonly Dictionary<RowStatus, string> StatusText = new Dictionary<RowStatus, string>
	{
		[RowStatus.Conflicto] = "Conflicto",
		[RowStatus.NoEnPresupuesto] = "No está en ppto.",
		[RowStatus.Titulo] = "Es título",
		[RowStatus.Difiere] = "Difiere",
		[RowStatus.Pendiente] = "Pendiente",
		[RowStatus.SinPartida] = "Sin partida",
		[RowStatus.Ok] = "OK"
	};

	private static readonly Dictionary<RowStatus, Color> StatusColor = new Dictionary<RowStatus, Color>
	{
		[RowStatus.Conflicto] = Color.FromArgb(255, 205, 205),
		[RowStatus.NoEnPresupuesto] = Color.FromArgb(255, 205, 205),
		[RowStatus.Titulo] = Color.FromArgb(255, 225, 190),
		[RowStatus.Difiere] = Color.FromArgb(255, 225, 190),
		[RowStatus.Pendiente] = Color.FromArgb(255, 248, 200),
		[RowStatus.SinPartida] = Color.FromArgb(235, 235, 235),
		[RowStatus.Ok] = Color.FromArgb(215, 240, 215)
	};

	private readonly PartidasHandler _handler;

	private readonly ExternalEvent _event;

	// Presupuesto (izquierda).
	private readonly Button _loadBudget = new Button();
	private readonly Label _budgetLabel = new Label();
	private readonly TextBox _treeSearch = new TextBox();
	private readonly CheckBox _onlyEmpty = new CheckBox();
	private readonly TreeView _tree = new TreeView();
	private readonly Button _assign = new Button();
	private readonly Button _selectNode = new Button();

	// Elementos (derecha).
	private readonly ComboBox _filter = new ComboBox();
	private readonly TextBox _search = new TextBox();
	private readonly Button _scan = new Button();
	private readonly TextBox _keywords = new TextBox();
	private readonly CheckBox _preferSource = new CheckBox();
	private readonly ListView _list = new ListView();
	private readonly Label _summary = new Label();

	// Acciones (abajo).
	private readonly Button _createParams = new Button();
	private readonly Button _apply = new Button();
	private readonly Button _clear = new Button();
	private readonly Button _selectRows = new Button();
	private readonly Button _useRevitSelection = new Button();
	private readonly Button _export = new Button();
	private readonly Label _status = new Label();

	private readonly Dictionary<string, TreeNode> _nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);

	private Budget _budget;
	private List<ElementRow> _rows = new List<ElementRow>();
	private List<ElementRow> _shown = new List<ElementRow>();
	private HashSet<long> _revitSelection = new HashSet<long>();
	private Dictionary<string, int> _countByCode = new Dictionary<string, int>(StringComparer.Ordinal);
	private string _documentTitle = string.Empty;
	private int _dycusaBound;
	private int _sortColumn = -1;
	private bool _sortAscending = true;
	private bool _busy;

	public PartidasForm(PartidasHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_event = externalEvent;
		_handler.OnResult = (result, title, message) => OnUi(() => SetResult(result, title, message));
		_handler.OnSelection = ids => OnUi(() => SetRevitSelection(ids));
		BuildLayout();
	}

	private void OnUi(Action action)
	{
		if (IsDisposed)
		{
			return;
		}

		if (InvokeRequired)
		{
			BeginInvoke(action);
		}
		else
		{
			action();
		}
	}

	/// <summary>Carga el último presupuesto usado y analiza el modelo.</summary>
	public void Start()
	{
		string path = Settings.Get("budget");
		if (!string.IsNullOrEmpty(path) && File.Exists(path))
		{
			LoadBudget(path, rescan: false);
		}

		RequestScan();
	}

	#region Layout

	private void BuildLayout()
	{
		Text = "EMASY · Partidas DYCUSA";
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1400, 760);
		MinimumSize = new Size(900, 450);
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
		split.Panel1.Controls.Add(BuildBudgetPanel());
		split.Panel2.Controls.Add(BuildElementsPanel());

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
		Setup(_createParams, "Crear parámetros DYCUSA", (s, e) => Raise(PartidasAction.CreateParameters, "Creando parámetros…"));
		Setup(_apply, "Escribir propuestos", (s, e) => ApplyProposed());
		_apply.Font = new Font(Font, FontStyle.Bold);
		Setup(_clear, "Limpiar DYCUSA", (s, e) => ClearSelected());
		_clear.ForeColor = Color.Firebrick;
		Setup(_selectRows, "Seleccionar en Revit", (s, e) => SelectInRevit(SelectedRows().Select(r => r.Id)));
		Setup(_useRevitSelection, "Usar selección de Revit", (s, e) => Raise(PartidasAction.ReadSelection, "Leyendo la selección…"));
		Setup(_export, "Exportar CSV…", (s, e) => ExportCsv());
		_status.AutoSize = true;
		_status.Margin = new Padding(16, 7, 0, 0);
		bottom.Controls.AddRange(new Control[] { _createParams, _apply, _clear, _selectRows, _useRevitSelection, _export, _status });

		Controls.Add(split);
		Controls.Add(bottom);
		Load += (s, e) => split.SplitterDistance = 430;
		UpdateButtons();
	}

	private Control BuildBudgetPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill };
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(8, 8, 4, 0) };
		Setup(_loadBudget, "Cargar presupuesto…", (s, e) => ChooseBudget());
		_budgetLabel.AutoSize = true;
		_budgetLabel.Margin = new Padding(6, 7, 0, 0);
		_budgetLabel.ForeColor = Color.DimGray;
		_budgetLabel.Text = "(sin presupuesto)";
		top.Controls.Add(_loadBudget);
		top.Controls.Add(_budgetLabel);
		top.SetFlowBreak(_budgetLabel, true);
		top.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_treeSearch.Width = 180;
		_treeSearch.TextChanged += (s, e) => BuildTree();
		top.Controls.Add(_treeSearch);
		_onlyEmpty.Text = "Solo partidas sin elementos";
		_onlyEmpty.AutoSize = true;
		_onlyEmpty.Margin = new Padding(10, 5, 0, 0);
		_onlyEmpty.CheckedChanged += (s, e) => BuildTree();
		top.Controls.Add(_onlyEmpty);

		_tree.Dock = DockStyle.Fill;
		_tree.HideSelection = false;
		_tree.ShowNodeToolTips = true;
		_tree.AfterSelect += (s, e) =>
		{
			UpdateButtons();
			if (_filter.SelectedIndex == FilterNode)
			{
				Populate();
			}
		};
		_tree.NodeMouseDoubleClick += (s, e) =>
		{
			_tree.SelectedNode = e.Node;
			if (_filter.SelectedIndex != FilterNode)
			{
				_filter.SelectedIndex = FilterNode;
			}
			else
			{
				Populate();
			}
		};

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(8, 4, 4, 0) };
		Setup(_assign, "Asignar partida a los elementos seleccionados  →", (s, e) => AssignNode());
		_assign.Font = new Font(Font, FontStyle.Bold);
		Setup(_selectNode, "Seleccionar sus elementos en Revit", (s, e) => SelectInRevit(RowsOfNode().Select(r => r.Id)));
		var legend = new Label
		{
			AutoSize = true,
			ForeColor = Color.DimGray,
			Margin = new Padding(0, 4, 0, 0),
			Text = "Verde: con elementos · Rojo: partida final sin elementos · Gris: \"Sin tabla en el modelo BIM\""
		};
		bottom.Controls.AddRange(new Control[] { _assign, _selectNode, legend });
		bottom.SetFlowBreak(_selectNode, true);

		panel.Controls.Add(_tree);
		panel.Controls.Add(bottom);
		panel.Controls.Add(top);
		return panel;
	}

	private Control BuildElementsPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill };
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(4, 8, 8, 0) };
		top.Controls.Add(new Label { Text = "Mostrar:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_filter.DropDownStyle = ComboBoxStyle.DropDownList;
		_filter.Width = 290;
		_filter.Items.AddRange(Filters);
		_filter.SelectedIndex = FilterReview;
		_filter.SelectedIndexChanged += (s, e) => Populate();
		top.Controls.Add(_filter);
		top.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
		_search.Width = 200;
		_search.TextChanged += (s, e) => Populate();
		top.Controls.Add(_search);
		Setup(_scan, "Analizar modelo", (s, e) => RequestScan());
		_scan.Margin = new Padding(12, 1, 0, 0);
		top.Controls.Add(_scan);
		top.SetFlowBreak(_scan, true);

		top.Controls.Add(new Label { Text = "Parámetros de origen (nombre contiene):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_keywords.Width = 160;
		_keywords.Text = Settings.Get("keywords") ?? ElementScanner.DefaultKeywords;
		top.Controls.Add(_keywords);
		_preferSource.Text = "Los parámetros de origen mandan sobre los DYCUSA ya escritos";
		_preferSource.AutoSize = true;
		_preferSource.Margin = new Padding(12, 5, 0, 0);
		_preferSource.CheckedChanged += (s, e) =>
		{
			foreach (ElementRow row in _rows)
			{
				ElementScanner.Evaluate(row, _budget, _preferSource.Checked);
			}

			RefreshAll();
		};
		top.Controls.Add(_preferSource);

		_list.Dock = DockStyle.Fill;
		_list.View = View.Details;
		_list.FullRowSelect = true;
		_list.MultiSelect = true;
		_list.HideSelection = false;
		_list.GridLines = true;
		_list.VirtualMode = true;
		for (int i = 0; i < ColumnNames.Length; i++)
		{
			_list.Columns.Add(ColumnNames[i], ColumnWidths[i]);
		}

		_list.RetrieveVirtualItem += (s, e) => e.Item = BuildItem(_shown[e.ItemIndex]);
		_list.ColumnClick += (s, e) =>
		{
			_sortAscending = _sortColumn != e.Column || !_sortAscending;
			_sortColumn = e.Column;
			Populate();
		};
		_list.SelectedIndexChanged += (s, e) => UpdateButtons();
		_list.VirtualItemsSelectionRangeChanged += (s, e) => UpdateButtons();
		_list.DoubleClick += (s, e) => SelectInRevit(SelectedRows().Select(r => r.Id));

		_summary.Dock = DockStyle.Bottom;
		_summary.Height = 22;
		_summary.Padding = new Padding(4, 4, 0, 0);
		_summary.ForeColor = Color.DimGray;

		panel.Controls.Add(_list);
		panel.Controls.Add(_summary);
		panel.Controls.Add(top);
		return panel;
	}

	private static void Setup(Button button, string text, EventHandler click)
	{
		button.Text = text;
		button.AutoSize = true;
		button.Click += click;
	}

	#endregion

	#region Revit

	private void RequestScan() => Raise(PartidasAction.Scan, "Analizando el modelo…");

	private void Raise(PartidasAction action, string busyText)
	{
		if (_busy)
		{
			return;
		}

		_handler.Action = action;
		_handler.Budget = _budget;
		_handler.Keywords = _keywords.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).ToList();
		_handler.PreferSource = _preferSource.Checked;
		_handler.DocumentTitle = _documentTitle;
		Settings.Set("keywords", _keywords.Text);
		if (action == PartidasAction.Scan || action == PartidasAction.CreateParameters || action == PartidasAction.Write ||
			action == PartidasAction.Clear)
		{
			SetBusy(true, busyText);
		}

		_event.Raise();
	}

	private void SetBusy(bool busy, string text)
	{
		_busy = busy;
		_status.Text = text ?? string.Empty;
		Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
		UpdateButtons();
		Update();
	}

	private void SetResult(ScanResult result, string title, string message)
	{
		SetBusy(false, message ?? "Listo.");
		if (result == null)
		{
			return;
		}

		_rows = result.Rows;
		_documentTitle = title;
		_dycusaBound = result.DycusaBound;
		Text = "EMASY · Partidas DYCUSA · " + title;
		RefreshAll();
	}

	private void SetRevitSelection(List<long> ids)
	{
		_revitSelection = new HashSet<long>(ids);
		_status.Text = $"{ids.Count} elemento(s) seleccionados en Revit.";
		if (_filter.SelectedIndex != FilterRevit)
		{
			_filter.SelectedIndex = FilterRevit;
		}
		else
		{
			Populate();
		}

		if (_shown.Count > 0)
		{
			_list.SelectedIndices.Clear();
			for (int i = 0; i < _shown.Count; i++)
			{
				_list.SelectedIndices.Add(i);
			}
		}
	}

	private void SelectInRevit(IEnumerable<long> ids)
	{
		_handler.Ids = ids.ToList();
		if (_handler.Ids.Count == 0)
		{
			_status.Text = "No hay elementos para seleccionar.";
			return;
		}

		_status.Text = $"{_handler.Ids.Count} elemento(s) seleccionados en Revit.";
		Raise(PartidasAction.Select, null);
	}

	private void ApplyProposed()
	{
		List<ElementRow> selected = SelectedRows();
		List<ElementRow> targets = selected.Count > 0
			? selected.Where(r => r.ProposedCode != null).ToList()
			: _rows.Where(r => r.Status == RowStatus.Pendiente).ToList();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, selected.Count > 0
					? "Los elementos seleccionados no tienen una partida propuesta (asígnales una desde el árbol)."
					: "No hay elementos pendientes de escribir.",
				Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (!ConfirmParameters())
		{
			return;
		}

		int risky = targets.Count(r => r.Status != RowStatus.Pendiente && r.Status != RowStatus.Ok);
		string question = selected.Count > 0
			? $"Escribir DYCUSA_PARTIDA 1–6 en los {targets.Count} elemento(s) seleccionados con su partida propuesta?"
			: $"Escribir DYCUSA_PARTIDA 1–6 en los {targets.Count} elemento(s) pendientes?\n\n" +
			  "(Los que están por revisar no se tocan: selecciónalos en la tabla para escribirlos uno a uno.)";
		if (risky > 0)
		{
			question += $"\n\nAtención: {risky} de ellos están por revisar (no está en el presupuesto, es título o difiere).";
		}

		if (MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}

		_handler.Assignments = targets.ToDictionary(r => r.Id, r => r.ProposedCode);
		Raise(PartidasAction.Write, "Escribiendo partidas…");
	}

	private void AssignNode()
	{
		if (!(_tree.SelectedNode?.Tag is BudgetItem item))
		{
			MessageBox.Show(this, "Marca una partida en el árbol del presupuesto.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		List<ElementRow> targets = SelectedRows();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "Selecciona los elementos en la tabla de la derecha (o en Revit y pulsa \"Usar selección de Revit\").",
				Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (!ConfirmParameters())
		{
			return;
		}

		string question = $"Asignar la partida\n\n{item.Code}  {item.Description}\n\na {targets.Count} elemento(s)?" +
			"\nSe escriben DYCUSA_PARTIDA 1–6 (los niveles superiores se completan solos).";
		if (!item.IsLeaf)
		{
			question += "\n\nAtención: es un título, no una partida final del presupuesto.";
		}

		int overwritten = targets.Count(r => r.DycusaCode != null && r.DycusaCode != item.Code);
		if (overwritten > 0)
		{
			question += $"\n\n{overwritten} elemento(s) ya tienen otra partida DYCUSA: se reemplaza.";
		}

		if (MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}

		_handler.Assignments = targets.ToDictionary(r => r.Id, r => item.Code);
		Raise(PartidasAction.Write, "Asignando partida…");
	}

	private void ClearSelected()
	{
		List<ElementRow> targets = SelectedRows();
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "Selecciona en la tabla los elementos a limpiar.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (MessageBox.Show(this, $"Vaciar DYCUSA_PARTIDA 1–6 en {targets.Count} elemento(s)?\n(Se deshace con Ctrl+Z en Revit.)",
				Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
		{
			return;
		}

		_handler.Ids = targets.Select(r => r.Id).ToList();
		Raise(PartidasAction.Clear, "Limpiando…");
	}

	/// <summary>Sin los parámetros en el modelo no se puede escribir: ofrece crearlos.</summary>
	private bool ConfirmParameters()
	{
		if (_dycusaBound >= SharedParams.Names.Length)
		{
			return true;
		}

		if (MessageBox.Show(this, "El modelo aún no tiene los parámetros DYCUSA_PARTIDA 1–6. ¿Crearlos ahora?\n\n" +
				"Después vuelve a pulsar el botón para escribir.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			Raise(PartidasAction.CreateParameters, "Creando parámetros…");
		}

		return false;
	}

	#endregion

	#region Presupuesto

	private void ChooseBudget()
	{
		using var dialog = new OpenFileDialog
		{
			Title = "Presupuesto (Excel con columnas Item, Descripción, Und.)",
			Filter = "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
			FileName = Settings.Get("budget") ?? string.Empty
		};
		if (dialog.ShowDialog(this) == DialogResult.OK)
		{
			LoadBudget(dialog.FileName, rescan: true);
		}
	}

	private void LoadBudget(string path, bool rescan)
	{
		try
		{
			_budget = BudgetReader.Read(path);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "No se pudo leer el presupuesto:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		Settings.Set("budget", path);
		int leaves = _budget.All.Count(i => i.IsLeaf);
		_budgetLabel.Text = $"{Path.GetFileName(path)} · {leaves} partidas";
		foreach (ElementRow row in _rows)
		{
			ElementScanner.Evaluate(row, _budget, _preferSource.Checked);
		}

		BuildTree();
		RefreshAll();
		if (rescan)
		{
			// Con el presupuesto se resuelven mejor los conflictos entre parámetros de origen.
			RequestScan();
		}
	}

	private void BuildTree()
	{
		_tree.BeginUpdate();
		_tree.Nodes.Clear();
		_nodes.Clear();
		if (_budget != null)
		{
			string search = BudgetReader.Simplify(_treeSearch.Text);
			foreach (BudgetItem root in _budget.Roots)
			{
				TreeNode node = BuildNode(root, search);
				if (node != null)
				{
					_tree.Nodes.Add(node);
				}
			}

			if (search.Length > 0 || _onlyEmpty.Checked)
			{
				_tree.ExpandAll();
			}
			else
			{
				foreach (TreeNode node in _tree.Nodes)
				{
					node.Expand();
					foreach (TreeNode child in node.Nodes)
					{
						child.Expand();
					}
				}
			}
		}

		_tree.EndUpdate();
		UpdateTreeCounts();
	}

	/// <summary>Nodo de la partida si ella o algún descendiente cumple el filtro (null si no).</summary>
	private TreeNode BuildNode(BudgetItem item, string search)
	{
		var children = item.Children.Select(c => BuildNode(c, search)).Where(n => n != null).ToList();
		bool matches = (search.Length == 0 || item.Code.StartsWith(search, StringComparison.Ordinal) ||
				BudgetReader.Simplify(item.Description).Contains(search)) &&
			(!_onlyEmpty.Checked || (item.IsLeaf && Count(item.Code) == 0));
		if (!matches && children.Count == 0)
		{
			return null;
		}

		var node = new TreeNode { Tag = item, ToolTipText = Tooltip(item) };
		node.Nodes.AddRange(children.ToArray());
		_nodes[item.Code] = node;
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

	private int Count(string code) => _countByCode.TryGetValue(code, out int count) ? count : 0;

	private void UpdateTreeCounts()
	{
		_tree.BeginUpdate();
		foreach (TreeNode node in _nodes.Values)
		{
			var item = (BudgetItem)node.Tag;
			int count = Count(item.Code);
			node.Text = $"{item.Code}  {item.Description}" + (count > 0 ? $"   [{count}]" : item.IsLeaf ? "   [0]" : string.Empty);
			node.ForeColor = !item.IsLeaf ? (count > 0 ? Color.Black : Color.Gray)
				: count > 0 ? Color.ForestGreen
				: item.WithoutBimTable ? Color.Gray
				: Color.Firebrick;
			node.NodeFont = item.IsLeaf ? null : new Font(_tree.Font, FontStyle.Bold);
		}

		_tree.EndUpdate();
	}

	#endregion

	#region Tabla

	private void RefreshAll()
	{
		_countByCode = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (ElementRow row in _rows)
		{
			if (row.ProposedCode == null)
			{
				continue;
			}

			foreach (string level in row.Proposed.Where(l => l.Length > 0))
			{
				_countByCode[level] = Count(level) + 1;
			}
		}

		if (_onlyEmpty.Checked)
		{
			BuildTree();
		}
		else
		{
			UpdateTreeCounts();
		}

		Populate();
	}

	private void Populate()
	{
		string search = BudgetReader.Simplify(_search.Text);
		string nodeCode = (_tree.SelectedNode?.Tag as BudgetItem)?.Code;
		int filter = _filter.SelectedIndex;
		IEnumerable<ElementRow> rows = _rows.Where(r =>
		{
			switch (filter)
			{
				case FilterReview:
					return r.Status <= RowStatus.Difiere;
				case FilterPending:
					return r.Status == RowStatus.Pendiente;
				case FilterWithout:
					return r.Status == RowStatus.SinPartida && r.InCodedCategory;
				case FilterOk:
					return r.Status == RowStatus.Ok;
				case FilterNode:
					return nodeCode != null && r.ProposedCode != null && PartidaCode.IsAncestorOrSelf(nodeCode, r.ProposedCode);
				case FilterRevit:
					return _revitSelection.Contains(r.Id);
				default:
					return r.InCodedCategory || r.Status != RowStatus.SinPartida;
			}
		});
		if (search.Length > 0)
		{
			rows = rows.Where(r => Cells(r).Any(c => BudgetReader.Simplify(c).Contains(search)));
		}

		if (_sortColumn >= 0)
		{
			Func<ElementRow, string> key = r => Cells(r)[_sortColumn];
			rows = _sortColumn == 0
				? (_sortAscending ? rows.OrderBy(r => r.Id) : rows.OrderByDescending(r => r.Id))
				: _sortAscending ? rows.OrderBy(key, StringComparer.CurrentCultureIgnoreCase) : rows.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase);
		}
		else
		{
			rows = rows.OrderBy(r => r.Status).ThenBy(r => r.ProposedCode ?? r.SourceCode ?? "~", StringComparer.Ordinal).ThenBy(r => r.Id);
		}

		_shown = rows.ToList();
		_list.SelectedIndices.Clear();
		_list.VirtualListSize = _shown.Count;
		_list.Invalidate();

		IEnumerable<ElementRow> visible = _rows.Where(r => r.InCodedCategory || r.Status != RowStatus.SinPartida);
		string counts = string.Join(" · ", visible.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{StatusText[g.Key]}: {g.Count()}"));
		_summary.Text = $"Mostrando {_shown.Count}  ·  {counts}" +
			(_dycusaBound < SharedParams.Names.Length ? "  ·  Faltan los parámetros DYCUSA en el modelo" : string.Empty);
		UpdateButtons();
	}

	private static string[] Cells(ElementRow row)
	{
		var cells = new List<string>
		{
			row.Id.ToString(CultureInfo.InvariantCulture),
			StatusText[row.Status],
			row.Category,
			row.FamilyType,
			row.Level,
			row.SourceParam ?? string.Empty,
			row.SourceCode ?? string.Empty,
			row.DycusaCode ?? (row.HasDycusaParameters ? string.Empty : "(sin parámetro)")
		};
		cells.AddRange(row.Proposed);
		string note = row.Note ?? string.Empty;
		if (row.Status == RowStatus.Difiere)
		{
			note = $"El origen dice {row.SourceCode}; DYCUSA dice {row.DycusaCode}. " + note;
		}
		else if (row.DycusaInconsistent)
		{
			note = "Los DYCUSA escritos no son de la misma rama: se reescriben desde el más profundo. " + note;
		}

		cells.Add(note.Trim());
		return cells.ToArray();
	}

	private static ListViewItem BuildItem(ElementRow row)
	{
		string[] cells = Cells(row);
		var item = new ListViewItem(cells) { BackColor = StatusColor[row.Status], UseItemStyleForSubItems = true };
		return item;
	}

	private List<ElementRow> SelectedRows()
	{
		var rows = new List<ElementRow>();
		foreach (int index in _list.SelectedIndices)
		{
			if (index >= 0 && index < _shown.Count)
			{
				rows.Add(_shown[index]);
			}
		}

		return rows;
	}

	private IEnumerable<ElementRow> RowsOfNode()
	{
		string code = (_tree.SelectedNode?.Tag as BudgetItem)?.Code;
		return code == null
			? Enumerable.Empty<ElementRow>()
			: _rows.Where(r => r.ProposedCode != null && PartidaCode.IsAncestorOrSelf(code, r.ProposedCode));
	}

	private void UpdateButtons()
	{
		int selected = _list.SelectedIndices.Count;
		bool idle = !_busy;
		_scan.Enabled = idle;
		_loadBudget.Enabled = idle;
		_createParams.Enabled = idle;
		_createParams.Text = _dycusaBound >= SharedParams.Names.Length ? "Actualizar parámetros DYCUSA" : "Crear parámetros DYCUSA";
		_apply.Enabled = idle;
		int pending = _rows.Count(r => r.Status == RowStatus.Pendiente);
		_apply.Text = selected > 0 ? $"Escribir propuestos ({selected} selec.)" : $"Escribir pendientes ({pending})";
		_clear.Enabled = idle && selected > 0;
		_selectRows.Enabled = idle && selected > 0;
		_useRevitSelection.Enabled = idle;
		_assign.Enabled = idle && selected > 0 && _tree.SelectedNode != null;
		_assign.Text = selected > 0 ? $"Asignar partida a {selected} elemento(s)  →" : "Asignar partida a los elementos seleccionados  →";
		_selectNode.Enabled = idle && _tree.SelectedNode != null;
	}

	#endregion

	#region CSV

	private void ExportCsv()
	{
		using var dialog = new SaveFileDialog
		{
			Title = "Exportar cobertura y elementos",
			Filter = "CSV (*.csv)|*.csv",
			FileName = $"Partidas DYCUSA - {_documentTitle}.csv"
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		string basePath = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? string.Empty, Path.GetFileNameWithoutExtension(dialog.FileName));
		string coveragePath = basePath + " - cobertura.csv";
		string elementsPath = basePath + " - elementos.csv";
		try
		{
			if (_budget != null)
			{
				WriteCsv(coveragePath, CoverageLines());
			}

			WriteCsv(elementsPath, ElementLines());
			_status.Text = _budget != null
				? $"Exportado: {Path.GetFileName(coveragePath)} y {Path.GetFileName(elementsPath)}"
				: $"Exportado: {Path.GetFileName(elementsPath)} (carga el presupuesto para exportar también la cobertura)";
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "No se pudo exportar:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	/// <summary>Por partida: metrado del presupuesto frente a la cantidad BIM de sus elementos (según su unidad).</summary>
	private IEnumerable<string[]> CoverageLines()
	{
		yield return new[] { "Código", "Descripción", "Nivel", "Und.", "Metrado ppto.", "Elementos BIM", "Cantidad BIM", "Diferencia", "Estado", "Observación ppto." };
		ILookup<string, ElementRow> byCode = _rows.Where(r => r.ProposedCode != null).ToLookup(r => r.ProposedCode, StringComparer.Ordinal);
		foreach (BudgetItem item in _budget.All.OrderBy(i => i.Code, StringComparer.Ordinal))
		{
			int count = Count(item.Code);
			string quantity = string.Empty;
			string difference = string.Empty;
			string state = string.Empty;
			if (item.IsLeaf)
			{
				double? bim = Quantity(item.Unit, byCode[item.Code].ToList());
				quantity = bim.HasValue ? Number(bim.Value) : string.Empty;
				if (bim.HasValue && item.Metrado.HasValue)
				{
					difference = Number(bim.Value - item.Metrado.Value);
				}

				state = count > 0 ? "Con elementos" : item.WithoutBimTable ? "Sin tabla en el modelo BIM" : "Sin elementos";
			}

			yield return new[]
			{
				item.Code, item.Description, item.Depth.ToString(CultureInfo.InvariantCulture), item.Unit,
				item.Metrado.HasValue ? Number(item.Metrado.Value) : string.Empty, count.ToString(CultureInfo.InvariantCulture),
				quantity, difference, state, item.Observation ?? string.Empty
			};
		}
	}

	private IEnumerable<string[]> ElementLines()
	{
		var header = new List<string>(ColumnNames) { "Área (m²)", "Longitud (m)", "Volumen (m³)" };
		yield return header.ToArray();
		foreach (ElementRow row in _rows.Where(r => r.InCodedCategory || r.Status != RowStatus.SinPartida)
					 .OrderBy(r => r.Status).ThenBy(r => r.ProposedCode ?? "~", StringComparer.Ordinal))
		{
			var cells = new List<string>(Cells(row)) { Number(row.Area), Number(row.Length), Number(row.Volume) };
			yield return cells.ToArray();
		}
	}

	/// <summary>Suma la magnitud que corresponde a la unidad (m² → área, m → longitud, m³ → volumen, und → cantidad).</summary>
	private static double? Quantity(string unit, List<ElementRow> rows)
	{
		string u = BudgetReader.Simplify(unit).Replace("²", "2").Replace("³", "3").Replace(".", string.Empty);
		switch (u)
		{
			case "m2":
				return rows.Sum(r => r.Area);
			case "m3":
				return rows.Sum(r => r.Volume);
			case "m":
			case "ml":
				return rows.Sum(r => r.Length);
			case "und":
			case "u":
			case "un":
			case "pza":
			case "jgo":
			case "glb":
				return rows.Count;
			default:
				return null;
		}
	}

	private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

	private static void WriteCsv(string path, IEnumerable<string[]> lines)
	{
		// Separador ";" y UTF-8 con BOM: abre bien en Excel en español.
		using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
		foreach (string[] line in lines)
		{
			writer.WriteLine(string.Join(";", line.Select(Escape)));
		}
	}

	private static string Escape(string value)
	{
		value ??= string.Empty;
		return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
	}

	#endregion
}

/// <summary>Preferencias del usuario (último presupuesto y palabras clave) en %AppData%\EMASY\Partidas.ini.</summary>
internal static class Settings
{
	private static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EMASY", "Partidas.ini");

	public static string Get(string key)
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return null;
			}

			string prefix = key + "=";
			return File.ReadAllLines(FilePath, Encoding.UTF8).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static void Set(string key, string value)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
			var lines = File.Exists(FilePath)
				? File.ReadAllLines(FilePath, Encoding.UTF8).Where(l => !l.StartsWith(key + "=", StringComparison.Ordinal)).ToList()
				: new List<string>();
			lines.Add(key + "=" + (value ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
			File.WriteAllLines(FilePath, lines, Encoding.UTF8);
		}
		catch (Exception)
		{
			// Las preferencias son una comodidad: si no se pueden guardar, se sigue sin ellas.
		}
	}
}
