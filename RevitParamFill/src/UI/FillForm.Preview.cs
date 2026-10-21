using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using RevitParamFill.Commands;
using RevitParamFill.Core;

namespace RevitParamFill.UI;

/// <summary>Pestañas 3 (vista previa: parámetro → estado y valor → elementos) y 4 (libre: masivo y personalizado).</summary>
internal sealed partial class FillForm
{
	private const int MaxElementNodes = 500;

	private static readonly Dictionary<FillStatus, string> StatusText = new Dictionary<FillStatus, string>
	{
		[FillStatus.Directo] = "Directo",
		[FillStatus.Inferido] = "Inferido (confirmar)",
		[FillStatus.SinDato] = "Sin dato",
		[FillStatus.Manual] = "Manual",
		[FillStatus.Igual] = "Ya tiene el valor",
		[FillStatus.Conservado] = "Se conserva",
		[FillStatus.Bloqueado] = "Bloqueado"
	};

	private static readonly Dictionary<FillStatus, Color> StatusColor = new Dictionary<FillStatus, Color>
	{
		[FillStatus.Directo] = Color.FromArgb(215, 240, 215),
		[FillStatus.Inferido] = Color.FromArgb(205, 225, 255),
		[FillStatus.SinDato] = Color.FromArgb(255, 205, 205),
		[FillStatus.Manual] = Color.FromArgb(255, 248, 200),
		[FillStatus.Igual] = Color.FromArgb(235, 235, 235),
		[FillStatus.Conservado] = Color.FromArgb(235, 235, 235),
		[FillStatus.Bloqueado] = Color.FromArgb(255, 225, 190)
	};

	private sealed class NodeInfo
	{
		public List<FillEntry> Entries;

		public bool Loaded = true;
	}

	private readonly TreeView _preview = new TreeView();
	private readonly TextBox _details = new TextBox();
	private readonly ComboBox _previewFilter = new ComboBox();
	private readonly Label _summary = new Label();

	// Libre.
	private readonly ComboBox _freeTarget = new ComboBox();
	private readonly TextBox _freeValue = new TextBox();
	private readonly RadioButton _scopeSelection = new RadioButton();
	private readonly RadioButton _scopeChecked = new RadioButton();
	private readonly RadioButton _scopeCategory = new RadioButton();
	private readonly ComboBox _freeCategory = new ComboBox();
	private readonly Label _selectionLabel = new Label();
	private readonly ComboBox _customTarget = new ComboBox();
	private readonly ComboBox _customGroup = new ComboBox();
	private readonly DataGridView _custom = new DataGridView();

	private FillPlan _plan = new FillPlan();
	private HashSet<long> _revitSelection = new HashSet<long>();
	private bool _syncing;

	#region Vista previa

	private TabPage BuildPreviewPage()
	{
		var page = new TabPage("3 · Vista previa");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
		top.Controls.Add(NewButton("Calcular", Do(() => Raise(FillAction.Compute, "Calculando los valores de todos los elementos…")), bold: true));
		top.Controls.Add(NewLabel("   Mostrar:"));
		_previewFilter.DropDownStyle = ComboBoxStyle.DropDownList;
		_previewFilter.Width = 260;
		_previewFilter.Items.AddRange(new object[] { "Lo que se va a escribir (directos e inferidos)", "Solo inferidos", "Sin dato", "Todo" });
		_previewFilter.SelectedIndex = 0;
		_previewFilter.SelectedIndexChanged += (s, e) => Safe(Populate);
		top.Controls.Add(_previewFilter);
		top.Controls.Add(NewLabel("   Las casillas marcadas se escriben: los directos van marcados; los inferidos, desmarcados hasta que los aceptes."));

		_preview.Dock = DockStyle.Fill;
		_preview.CheckBoxes = true;
		_preview.HideSelection = false;
		_preview.BeforeExpand += (s, e) => Safe(() => LoadElements(e.Node));
		_preview.BeforeCheck += (s, e) =>
		{
			// Solo se pueden marcar los valores que se pueden escribir.
			if (!_syncing && e.Node.Tag is NodeInfo info && !info.Entries.Any(x => x.Writable))
			{
				e.Cancel = true;
			}
		};
		_preview.AfterCheck += (s, e) => Safe(() => SyncChecks(e.Node));
		_preview.AfterSelect += (s, e) => Safe(() => ShowDetails(e.Node));
		_preview.NodeMouseDoubleClick += (s, e) => Safe(() =>
		{
			if (e.Node.Tag is NodeInfo info)
			{
				SelectInRevit(info.Entries.Select(x => x.Element.Id));
			}
		});

		_details.Dock = DockStyle.Bottom;
		_details.Height = 120;
		_details.Multiline = true;
		_details.ReadOnly = true;
		_details.ScrollBars = ScrollBars.Vertical;
		_details.Font = new Font("Consolas", 9f);
		_details.BackColor = SystemColors.Window;

		_summary.Dock = DockStyle.Bottom;
		_summary.Height = 22;
		_summary.Padding = new Padding(4, 4, 0, 0);
		_summary.ForeColor = Color.DimGray;

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
		var accept = NewButton("Aceptar inferidos seleccionados", Do(AcceptSelected), bold: true);
		accept.ForeColor = Color.RoyalBlue;
		bottom.Controls.Add(accept);
		bottom.Controls.Add(NewButton("Escribir marcados", Do(WriteChecked), bold: true));
		bottom.Controls.Add(NewButton("Seleccionar en Revit", Do(() => SelectInRevit(Selected().Select(x => x.Element.Id)))));
		bottom.Controls.Add(NewButton("Exportar CSV…", Do(ExportCsv)));

		page.Controls.Add(_preview);
		page.Controls.Add(_details);
		page.Controls.Add(_summary);
		page.Controls.Add(bottom);
		page.Controls.Add(top);
		return page;
	}

	private void SetPlan(FillPlan plan, string title)
	{
		_plan = plan;
		_documentTitle = title;
		int inferred = plan.Entries.Count(e => e.Status == FillStatus.Inferido);
		string notes = plan.Notes.Count > 0 ? "  ·  " + string.Join("  ·  ", plan.Notes.Take(3)) : string.Empty;
		SetBusy(false, $"Calculado en {plan.Elapsed.TotalSeconds:0.0} s: {plan.Elements.Count} elementos, {plan.Entries.Count(e => e.Status == FillStatus.Directo)} valores directos, " +
			$"{inferred} inferidos por confirmar, {plan.Entries.Count(e => e.Status == FillStatus.SinDato)} sin dato.{notes}");
		Populate();
		FillFreeChoices();
	}

	private IEnumerable<FillEntry> VisibleEntries()
	{
		switch (_previewFilter.SelectedIndex)
		{
			case 0:
				return _plan.Entries.Where(e => e.Writable);
			case 1:
				return _plan.Entries.Where(e => e.Status == FillStatus.Inferido);
			case 2:
				return _plan.Entries.Where(e => e.Status == FillStatus.SinDato);
			default:
				return _plan.Entries;
		}
	}

	/// <summary>Motivo sin los números (distancias, cantidades), para agrupar deducciones del mismo tipo.</summary>
	private static string ReasonKind(FillEntry entry)
	{
		if (entry.Status != FillStatus.Inferido || string.IsNullOrEmpty(entry.Reason))
		{
			return string.Empty;
		}

		int cut = entry.Reason.IndexOfAny(new[] { ':', '(', ';' });
		return " · " + (cut > 0 ? entry.Reason.Substring(0, cut) : entry.Reason).Trim();
	}

	private void Populate()
	{
		var expanded = new HashSet<string>(All(_preview.Nodes).Where(n => n.IsExpanded).Select(n => n.Name));
		_preview.BeginUpdate();
		_syncing = true;
		_preview.Nodes.Clear();
		foreach (var parameter in VisibleEntries().GroupBy(e => e.Target).OrderBy(g => g.Min(e => e.RuleIndex)))
		{
			var entries = parameter.ToList();
			var parameterNode = new TreeNode($"{parameter.Key}   [{entries.Count}]")
			{
				Name = "p:" + parameter.Key,
				Tag = new NodeInfo { Entries = entries },
				NodeFont = new Font(_preview.Font, FontStyle.Bold)
			};
			foreach (var group in entries.GroupBy(e => (e.Status, Value: e.NewValue ?? string.Empty, Kind: ReasonKind(e)))
						 .OrderBy(g => g.Key.Status == FillStatus.Directo ? 0 : g.Key.Status == FillStatus.Inferido ? 1 : 2)
						 .ThenByDescending(g => g.Count()))
			{
				var groupEntries = group.ToList();
				string value = group.Key.Value.Length > 0 ? $"\"{group.Key.Value}\"" : "(vacío)";
				var groupNode = new TreeNode($"{StatusText[group.Key.Status]} · {value}{group.Key.Kind}   [{groupEntries.Count}]")
				{
					Name = $"g:{parameter.Key}|{group.Key.Status}|{group.Key.Value}|{group.Key.Kind}",
					Tag = new NodeInfo { Entries = groupEntries, Loaded = false },
					BackColor = StatusColor[group.Key.Status],
					Checked = groupEntries.All(e => e.Accepted && e.Writable)
				};
				groupNode.Nodes.Add(new TreeNode("…"));
				parameterNode.Nodes.Add(groupNode);
			}

			parameterNode.Checked = parameterNode.Nodes.Count > 0 && parameterNode.Nodes.Cast<TreeNode>().All(n => n.Checked);
			_preview.Nodes.Add(parameterNode);
		}

		foreach (TreeNode node in All(_preview.Nodes).ToList().Where(n => expanded.Contains(n.Name)))
		{
			node.Expand();
		}

		_syncing = false;
		_preview.EndUpdate();
		UpdateSummary();
	}

	private static IEnumerable<TreeNode> All(TreeNodeCollection nodes)
	{
		foreach (TreeNode node in nodes)
		{
			yield return node;
			foreach (TreeNode child in All(node.Nodes))
			{
				yield return child;
			}
		}
	}

	private void LoadElements(TreeNode node)
	{
		if (!(node.Tag is NodeInfo info) || info.Loaded)
		{
			return;
		}

		info.Loaded = true;
		_preview.BeginUpdate();
		_syncing = true;
		node.Nodes.Clear();
		foreach (FillEntry entry in info.Entries.Take(MaxElementNodes))
		{
			string current = entry.Current.Length > 0 ? $"\"{entry.Current}\"" : "(vacío)";
			node.Nodes.Add(new TreeNode($"{entry.Element.Id} · {entry.Element.Category} · {entry.Element.FamilyType} · {entry.Element.Level} · {current} → \"{entry.NewValue}\"" +
				(entry.OnType ? "  (de tipo)" : string.Empty))
			{
				Tag = new NodeInfo { Entries = new List<FillEntry> { entry } },
				Checked = entry.Accepted && entry.Writable
			});
		}

		if (info.Entries.Count > MaxElementNodes)
		{
			node.Nodes.Add(new TreeNode($"… y {info.Entries.Count - MaxElementNodes} más (marca el grupo para todos)") { ForeColor = Color.DimGray });
		}

		_syncing = false;
		_preview.EndUpdate();
	}

	/// <summary>Marcar un nodo acepta (o quita) todos sus valores; los hijos y padres se actualizan.</summary>
	private void SyncChecks(TreeNode node)
	{
		if (_syncing)
		{
			return;
		}

		_syncing = true;
		if (node.Tag is NodeInfo info)
		{
			foreach (FillEntry entry in info.Entries.Where(e => e.Writable))
			{
				entry.Accepted = node.Checked;
			}
		}

		foreach (TreeNode child in All(node.Nodes))
		{
			child.Checked = node.Checked && child.Tag is NodeInfo childInfo && childInfo.Entries.Any(e => e.Writable);
		}

		for (TreeNode parent = node.Parent; parent != null; parent = parent.Parent)
		{
			parent.Checked = parent.Tag is NodeInfo parentInfo && parentInfo.Entries.Where(e => e.Writable).All(e => e.Accepted);
		}

		_syncing = false;
		UpdateSummary();
	}

	private List<FillEntry> Selected() => _preview.SelectedNode?.Tag is NodeInfo info ? info.Entries : new List<FillEntry>();

	private void AcceptSelected()
	{
		TreeNode node = _preview.SelectedNode;
		if (node == null || !(node.Tag is NodeInfo info) || !info.Entries.Any(e => e.Status == FillStatus.Inferido))
		{
			MessageBox.Show(this, "Selecciona un grupo (o elemento) \"Inferido\" para aceptarlo.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		node.Checked = true;
		_status.Text = $"{info.Entries.Count(e => e.Status == FillStatus.Inferido)} valor(es) inferidos aceptados. Pulsa \"Escribir marcados\".";
	}

	private void UpdateSummary()
	{
		int toWrite = _plan.Entries.Count(e => e.Accepted && e.Writable);
		int inferred = _plan.Entries.Count(e => e.Status == FillStatus.Inferido);
		int accepted = _plan.Entries.Count(e => e.Status == FillStatus.Inferido && e.Accepted);
		_summary.Text = $"Se escribirán {toWrite} valor(es)  ·  inferidos aceptados: {accepted} de {inferred}  ·  sin dato: {_plan.Entries.Count(e => e.Status == FillStatus.SinDato)}" +
			$"  ·  bloqueados: {_plan.Entries.Count(e => e.Status == FillStatus.Bloqueado)}";
	}

	private void ShowDetails(TreeNode node)
	{
		if (!(node?.Tag is NodeInfo info) || info.Entries.Count == 0)
		{
			_details.Text = string.Empty;
			return;
		}

		var text = new StringBuilder();
		if (info.Entries.Count == 1)
		{
			FillEntry entry = info.Entries[0];
			text.AppendLine($"Elemento {entry.Element.Id} · {entry.Element.Category} · {entry.Element.FamilyType} · {entry.Element.Level}");
			text.AppendLine($"{entry.Target}{(entry.OnType ? " (parámetro de tipo)" : string.Empty)}: \"{entry.Current}\" → \"{entry.NewValue}\"   [{StatusText[entry.Status]}]");
			text.AppendLine("Motivo: " + entry.Reason);
		}
		else
		{
			text.AppendLine(node.Text);
			foreach (var reason in info.Entries.GroupBy(e => e.Reason).OrderByDescending(g => g.Count()).Take(5))
			{
				text.AppendLine($"  {reason.Count(),5} × {reason.Key}");
			}

			text.AppendLine("Doble clic: seleccionar en Revit.");
		}

		_details.Text = text.ToString();
	}

	private void WriteChecked()
	{
		List<FillEntry> entries = _plan.Entries.Where(e => e.Accepted && e.Writable && e.NewValue != null).ToList();
		if (entries.Count == 0)
		{
			MessageBox.Show(this, "No hay valores marcados para escribir (pulsa Calcular y marca los grupos).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		int inferred = entries.Count(e => e.Status == FillStatus.Inferido);
		string summary = string.Join("\n", entries.GroupBy(e => e.Target).Select(g => $"  {g.Key}: {g.Count()}"));
		if (MessageBox.Show(this, $"Escribir {entries.Count} valor(es){(inferred > 0 ? $", de ellos {inferred} inferidos aceptados" : string.Empty)}:\n\n{summary}\n\nSe deshace con Ctrl+Z en Revit.",
				Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			_handler.Values = entries.Select(e => (e.Element.Id, e.Target, e.NewValue)).ToList();
			Raise(FillAction.Write, "Escribiendo…");
		}
	}

	private void SelectInRevit(IEnumerable<long> ids)
	{
		_handler.Ids = ids.Distinct().ToList();
		if (_handler.Ids.Count > 0)
		{
			Raise(FillAction.Select, null);
		}
	}

	private void ExportCsv()
	{
		using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"Rellenar - {_documentTitle}.csv" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
		writer.WriteLine("Id;Categoría;Familia : Tipo;Nivel;Parámetro;De tipo;Actual;Nuevo;Estado;Se escribe;Motivo");
		foreach (FillEntry entry in _plan.Entries)
		{
			writer.WriteLine(string.Join(";", new[]
			{
				entry.Element.Id.ToString(CultureInfo.InvariantCulture), entry.Element.Category, entry.Element.FamilyType, entry.Element.Level, entry.Target,
				entry.OnType ? "Sí" : "No", entry.Current, entry.NewValue ?? string.Empty, StatusText[entry.Status], entry.Accepted && entry.Writable ? "Sí" : "No", entry.Reason
			}.Select(Escape)));
		}

		_status.Text = "Exportado: " + dialog.FileName;
	}

	private static string Escape(string value)
	{
		value ??= string.Empty;
		return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
	}

	#endregion

	#region Libre

	private TabPage BuildFreePage()
	{
		var page = new TabPage("4 · Libre");
		var massive = new GroupBox { Text = "Masivo: un mismo valor para muchos elementos", Dock = DockStyle.Top, Height = 150, Padding = new Padding(10) };
		var massiveFlow = new FlowLayoutPanel { Dock = DockStyle.Fill };
		massiveFlow.Controls.Add(NewLabel("Parámetro:"));
		_freeTarget.Width = 260;
		_freeTarget.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
		_freeTarget.AutoCompleteSource = AutoCompleteSource.ListItems;
		massiveFlow.Controls.Add(_freeTarget);
		massiveFlow.Controls.Add(NewLabel("   Valor:"));
		_freeValue.Width = 260;
		massiveFlow.Controls.Add(_freeValue);
		massiveFlow.SetFlowBreak(_freeValue, true);
		massiveFlow.Controls.Add(NewLabel("A:"));
		_scopeSelection.Text = "la selección actual de Revit";
		_scopeSelection.AutoSize = true;
		_scopeSelection.Checked = true;
		_scopeChecked.Text = "lo marcado en la vista previa";
		_scopeChecked.AutoSize = true;
		_scopeCategory.Text = "toda la categoría:";
		_scopeCategory.AutoSize = true;
		_freeCategory.DropDownStyle = ComboBoxStyle.DropDownList;
		_freeCategory.Width = 200;
		massiveFlow.Controls.AddRange(new Control[] { _scopeSelection, NewButton("Leer selección", Do(() => Raise(FillAction.ReadSelection, null))), _selectionLabel, _scopeChecked, _scopeCategory, _freeCategory });
		_selectionLabel.AutoSize = true;
		_selectionLabel.ForeColor = Color.DimGray;
		_selectionLabel.Margin = new Padding(0, 6, 12, 0);
		massiveFlow.SetFlowBreak(_freeCategory, true);
		massiveFlow.Controls.Add(NewButton("Aplicar masivo", Do(ApplyMassive), bold: true));
		massive.Controls.Add(massiveFlow);

		var custom = new GroupBox { Text = "Personalizado: un valor por grupo o por elemento (edita la columna \"Valor nuevo\")", Dock = DockStyle.Fill, Padding = new Padding(10) };
		var customTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
		customTop.Controls.Add(NewLabel("Parámetro:"));
		_customTarget.Width = 260;
		_customTarget.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
		_customTarget.AutoCompleteSource = AutoCompleteSource.ListItems;
		customTop.Controls.Add(_customTarget);
		customTop.Controls.Add(NewLabel("   Agrupar por:"));
		_customGroup.DropDownStyle = ComboBoxStyle.DropDownList;
		_customGroup.Width = 160;
		_customGroup.Items.AddRange(new object[] { "Categoría", "Familia : Tipo", "Nivel", "Elemento" });
		_customGroup.SelectedIndex = 1;
		customTop.Controls.Add(_customGroup);
		customTop.Controls.Add(NewButton("Cargar", Do(LoadCustom)));
		customTop.Controls.Add(NewButton("Aplicar personalizado", Do(ApplyCustom), bold: true));

		_custom.Dock = DockStyle.Fill;
		_custom.AllowUserToAddRows = false;
		_custom.AllowUserToDeleteRows = false;
		_custom.RowHeadersVisible = false;
		_custom.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Grupo", Width = 420, ReadOnly = true });
		_custom.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Elementos", Width = 80, ReadOnly = true });
		_custom.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Valores actuales", Width = 300, ReadOnly = true });
		_custom.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Valor nuevo", Width = 260 });
		custom.Controls.Add(_custom);
		custom.Controls.Add(customTop);

		page.Controls.Add(custom);
		page.Controls.Add(massive);
		return page;
	}

	private void FillFreeChoices()
	{
		foreach (ComboBox box in new[] { _freeTarget, _customTarget })
		{
			string current = box.Text;
			box.Items.Clear();
			// Primero los parámetros con regla "Libre", luego los demás de las reglas.
			box.Items.AddRange(_set.Rules.Where(r => r.Target.Length > 0).OrderBy(r => r.Source == SourceKind.Libre ? 0 : 1)
				.Select(r => r.Target).Distinct().Cast<object>().ToArray());
			box.Text = current.Length > 0 ? current : _set.Rules.FirstOrDefault(r => r.Source == SourceKind.Libre)?.Target ?? string.Empty;
		}

		string category = _freeCategory.SelectedItem as string;
		_freeCategory.Items.Clear();
		_freeCategory.Items.AddRange(_plan.Elements.Values.Select(e => e.Category).Distinct().OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray());
		if (category != null && _freeCategory.Items.Contains(category))
		{
			_freeCategory.SelectedItem = category;
		}
	}

	private void SetRevitSelection(List<long> ids)
	{
		_revitSelection = new HashSet<long>(ids);
		_selectionLabel.Text = $"({ids.Count} elemento(s))";
		SetBusy(false, $"{ids.Count} elemento(s) seleccionados en Revit.");
	}

	private void ApplyMassive()
	{
		string target = _freeTarget.Text.Trim();
		string value = _freeValue.Text;
		if (target.Length == 0)
		{
			MessageBox.Show(this, "Elige el parámetro.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		List<long> ids;
		if (_scopeSelection.Checked)
		{
			ids = _revitSelection.ToList();
		}
		else if (_scopeChecked.Checked)
		{
			ids = _plan.Entries.Where(e => e.Accepted && e.Writable).Select(e => e.Element.Id).Distinct().ToList();
		}
		else
		{
			string category = _freeCategory.SelectedItem as string;
			ids = _plan.Elements.Values.Where(e => e.Category == category).Select(e => e.Id).ToList();
		}

		if (ids.Count == 0)
		{
			MessageBox.Show(this, _scopeSelection.Checked ? "Selecciona elementos en Revit y pulsa \"Leer selección\"." : "No hay elementos en el alcance (pulsa Calcular en la vista previa).",
				Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (MessageBox.Show(this, $"Escribir \"{value}\" en {target} de {ids.Count} elemento(s)?\n(Los parámetros de tipo se escriben en su tipo.)", Text,
				MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			_handler.Values = ids.Select(id => (id, target, value)).ToList();
			Raise(FillAction.Write, "Escribiendo…");
		}
	}

	/// <summary>Arma la tabla del personalizado con los elementos que tienen el parámetro (de la última vista previa).</summary>
	private void LoadCustom()
	{
		string target = _customTarget.Text.Trim();
		List<FillEntry> entries = _plan.Entries.Where(e => e.Target == target).ToList();
		if (entries.Count == 0)
		{
			MessageBox.Show(this, "Pulsa Calcular en la vista previa: el parámetro debe tener una regla (por ejemplo \"Libre\").", Text,
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		Func<FillEntry, string> key = _customGroup.SelectedIndex switch
		{
			0 => e => e.Element.Category,
			2 => e => e.Element.Level,
			3 => e => $"{e.Element.Id} · {e.Element.Category} · {e.Element.FamilyType}",
			_ => e => $"{e.Element.Category} · {e.Element.FamilyType}"
		};
		_custom.Rows.Clear();
		foreach (var group in entries.GroupBy(key).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
		{
			string currents = string.Join(" | ", group.Select(e => e.Current.Length > 0 ? e.Current : "(vacío)").GroupBy(v => v)
				.OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} ×{g.Count()}"));
			int row = _custom.Rows.Add(group.Key, group.Count().ToString(CultureInfo.InvariantCulture), currents, string.Empty);
			_custom.Rows[row].Tag = group.Select(e => e.Element.Id).ToList();
		}

		_status.Text = $"{_custom.Rows.Count} grupo(s). Escribe el valor nuevo en los que quieras y pulsa \"Aplicar personalizado\".";
	}

	private void ApplyCustom()
	{
		_custom.EndEdit();
		string target = _customTarget.Text.Trim();
		var values = new List<(long, string, string)>();
		foreach (DataGridViewRow row in _custom.Rows)
		{
			string value = (row.Cells[3].Value as string ?? string.Empty).Trim();
			if (value.Length > 0 && row.Tag is List<long> ids)
			{
				values.AddRange(ids.Select(id => (id, target, value)));
			}
		}

		if (values.Count == 0)
		{
			MessageBox.Show(this, "Escribe al menos un valor nuevo en la tabla.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (MessageBox.Show(this, $"Escribir {target} en {values.Count} elemento(s)?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			_handler.Values = values;
			Raise(FillAction.Write, "Escribiendo…");
		}
	}

	#endregion
}
