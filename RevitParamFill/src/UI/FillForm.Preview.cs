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

/// <summary>
/// Hoja 2 (validar contenido: una fila por elemento y una columna por parámetro con el valor que quedaría) y hoja 4 (libre:
/// masivo y personalizado).
/// </summary>
internal sealed partial class FillForm
{
	private const int FixedColumns = 5;

	private static readonly Dictionary<FillStatus, string> StatusText = new Dictionary<FillStatus, string>
	{
		[FillStatus.Directo] = "Directo",
		[FillStatus.Inferido] = "Inferido",
		[FillStatus.SinDato] = "Sin dato",
		[FillStatus.Manual] = "Editado a mano",
		[FillStatus.Igual] = "Ya tiene el valor",
		[FillStatus.Conservado] = "Se conserva",
		[FillStatus.Bloqueado] = "Bloqueado"
	};

	private static readonly Color Direct = Color.FromArgb(215, 240, 215);
	private static readonly Color Inferred = Color.FromArgb(220, 232, 255);
	private static readonly Color InferredAccepted = Color.FromArgb(150, 190, 245);
	private static readonly Color Missing = Color.FromArgb(255, 210, 210);
	private static readonly Color Same = Color.FromArgb(240, 240, 240);
	private static readonly Color Locked = Color.FromArgb(255, 225, 190);
	private static readonly Color Edited = Color.FromArgb(255, 245, 180);

	private readonly DataGridView _table = new DataGridView();
	private readonly ComboBox _categoryFilter = new ComboBox();
	private readonly ComboBox _stateFilter = new ComboBox();
	private readonly TextBox _search = new TextBox();
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

	/// <summary>Columnas de parámetros (en el orden de las reglas).</summary>
	private List<string> _targets = new List<string>();

	/// <summary>(elemento, parámetro) → valor propuesto.</summary>
	private Dictionary<(long, string), FillEntry> _cells = new Dictionary<(long, string), FillEntry>();

	/// <summary>Todas las filas (elementos con algún parámetro) y las mostradas con los filtros.</summary>
	private List<ElementInfo> _allRows = new List<ElementInfo>();
	private List<ElementInfo> _shown = new List<ElementInfo>();

	/// <summary>Filas desmarcadas (no se escriben).</summary>
	private readonly HashSet<long> _excluded = new HashSet<long>();

	private Font _strike;

	#region Validar contenido

	private TabPage BuildValidatePage()
	{
		var page = new TabPage("2 · Validar contenido") { BackColor = Color.White };
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(10, 8, 10, 0), BackColor = Color.White };
		top.Controls.Add(NewLabel("Así quedaría cada elemento. Verde: dato directo · Azul: deducido (acéptalo para escribirlo) · Rojo: sin dato · Gris: ya lo tiene · Amarillo: editado a mano. Puedes escribir en una celda para corregirla.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		top.Controls.Add(NewLabel("Categoría:"));
		_categoryFilter.DropDownStyle = ComboBoxStyle.DropDownList;
		_categoryFilter.Width = 200;
		_categoryFilter.SelectedIndexChanged += (s, e) => Safe(ApplyFilters);
		top.Controls.Add(_categoryFilter);
		top.Controls.Add(NewLabel("   Mostrar:"));
		_stateFilter.DropDownStyle = ComboBoxStyle.DropDownList;
		_stateFilter.Width = 210;
		_stateFilter.Items.AddRange(new object[] { "Todos", "Con valores deducidos", "Con valores sin dato", "Con cambios a escribir" });
		_stateFilter.SelectedIndex = 0;
		_stateFilter.SelectedIndexChanged += (s, e) => Safe(ApplyFilters);
		top.Controls.Add(_stateFilter);
		top.Controls.Add(NewLabel("   Buscar:"));
		_search.Width = 200;
		_search.TextChanged += (s, e) => Safe(ApplyFilters);
		top.Controls.Add(_search);
		top.Controls.Add(NewButton("Recalcular", Do(() => Raise(FillAction.Compute, "Calculando el contenido de todos los elementos…"))));

		_table.Dock = DockStyle.Fill;
		_table.VirtualMode = true;
		_table.AllowUserToAddRows = false;
		_table.AllowUserToDeleteRows = false;
		_table.AllowUserToResizeRows = false;
		_table.RowHeadersVisible = false;
		_table.BackgroundColor = Color.White;
		_table.BorderStyle = BorderStyle.None;
		_table.GridColor = Color.FromArgb(225, 225, 225);
		_table.SelectionMode = DataGridViewSelectionMode.CellSelect;
		_table.EnableHeadersVisualStyles = false;
		_table.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 244, 248);
		_table.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
		_table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
		_table.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
		_table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(120, 160, 210);
		_table.ShowCellToolTips = true;
		_table.CellValueNeeded += (s, e) => Safe(() => e.Value = CellValue(e.RowIndex, e.ColumnIndex));
		_table.CellValuePushed += (s, e) => Safe(() => PushValue(e.RowIndex, e.ColumnIndex, e.Value));
		_table.CellFormatting += (s, e) => Safe(() => FormatCell(e));
		_table.CellToolTipTextNeeded += (s, e) => Safe(() => e.ToolTipText = CellTip(e.RowIndex, e.ColumnIndex));
		_table.CellBeginEdit += (s, e) => e.Cancel = !Editable(e.RowIndex, e.ColumnIndex);
		_table.CurrentCellDirtyStateChanged += (s, e) =>
		{
			if (_table.IsCurrentCellDirty && _table.CurrentCell is DataGridViewCheckBoxCell)
			{
				_table.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_table.CellDoubleClick += (s, e) => Safe(() =>
		{
			if (e.RowIndex >= 0 && e.ColumnIndex > 0 && e.ColumnIndex < FixedColumns)
			{
				SelectInRevit(new[] { _shown[e.RowIndex].Id });
			}
		});
		_table.DataError += (s, e) => e.ThrowException = false;

		_summary.Dock = DockStyle.Bottom;
		_summary.Height = 24;
		_summary.Padding = new Padding(8, 5, 0, 0);
		_summary.ForeColor = Color.DimGray;

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 0), BackColor = Color.FromArgb(245, 247, 250), WrapContents = false };
		bottom.Controls.Add(NewButton("Aceptar deducidos de las filas seleccionadas", Do(() => AcceptInferred(SelectedRows()))));
		bottom.Controls.Add(NewButton("Aceptar todos los deducidos mostrados", Do(() => AcceptInferred(_shown))));
		Button write = NewButton("Escribir en el modelo", Do(WriteTable), bold: true);
		write.BackColor = Color.FromArgb(30, 110, 170);
		write.ForeColor = Color.White;
		write.FlatStyle = FlatStyle.Flat;
		write.Margin = new Padding(16, 3, 16, 3);
		bottom.Controls.Add(write);
		bottom.Controls.Add(NewButton("Seleccionar en Revit", Do(() => SelectInRevit(SelectedRows().Select(r => r.Id)))));
		bottom.Controls.Add(NewButton("Exportar tabla (CSV)…", Do(ExportCsv)));

		page.Controls.Add(_table);
		page.Controls.Add(_summary);
		page.Controls.Add(bottom);
		page.Controls.Add(top);
		return page;
	}

	private void SetPlan(FillPlan plan, string title)
	{
		_plan = plan;
		_documentTitle = title;
		_excluded.Clear();
		_targets = plan.Entries.OrderBy(e => e.RuleIndex).Select(e => e.Target).Distinct().ToList();
		_cells = new Dictionary<(long, string), FillEntry>();
		foreach (FillEntry entry in plan.Entries)
		{
			_cells[(entry.Element.Id, entry.Target)] = entry;
		}

		// Las de tipo se ven en todos los elementos del tipo (se escriben una vez en el tipo).
		var typeEntries = plan.Entries.Where(e => e.OnType).ToDictionary(e => (e.Element.TypeId, e.Target), e => e);
		foreach (ElementInfo element in plan.Elements.Values)
		{
			foreach (string target in _targets)
			{
				if (!_cells.ContainsKey((element.Id, target)) && typeEntries.TryGetValue((element.TypeId, target), out FillEntry shared))
				{
					_cells[(element.Id, target)] = shared;
				}
			}
		}

		_allRows = plan.Elements.Values.OrderBy(e => e.Category, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(e => e.FamilyType, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Id).ToList();

		_table.Columns.Clear();
		_table.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Escribir", Width = 60, Frozen = true });
		_table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Id", Width = 80, ReadOnly = true, Frozen = true });
		_table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Categoría", Width = 130, ReadOnly = true, Frozen = true });
		_table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Familia : Tipo", Width = 260, ReadOnly = true, Frozen = true });
		_table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nivel", Width = 110, ReadOnly = true });
		foreach (string target in _targets)
		{
			_table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = target, Width = 170, SortMode = DataGridViewColumnSortMode.NotSortable });
		}

		string category = _categoryFilter.SelectedItem as string;
		_categoryFilter.Items.Clear();
		_categoryFilter.Items.Add("(todas)");
		_categoryFilter.Items.AddRange(_allRows.Select(r => r.Category).Distinct().Cast<object>().ToArray());
		_categoryFilter.SelectedItem = category != null && _categoryFilter.Items.Contains(category) ? category : "(todas)";

		string notes = plan.Notes.Count > 0 ? "  ·  " + string.Join("  ·  ", plan.Notes.Take(3)) : string.Empty;
		SetBusy(false, $"Calculado en {plan.Elapsed.TotalSeconds:0.0} s: {plan.Elements.Count} elementos.{notes}");
		ApplyFilters();
		FillFreeChoices();
	}

	private FillEntry Entry(ElementInfo row, int column) =>
		column >= FixedColumns && column - FixedColumns < _targets.Count && _cells.TryGetValue((row.Id, _targets[column - FixedColumns]), out FillEntry entry)
			? entry
			: null;

	/// <summary>Se escribe: fila marcada, valor escribible y, si es deducido, aceptado.</summary>
	private bool WillWrite(FillEntry entry) =>
		entry != null && entry.NewValue != null && !_excluded.Contains(entry.Element.Id) &&
		(entry.Status == FillStatus.Directo || entry.Status == FillStatus.Manual || (entry.Status == FillStatus.Inferido && entry.Accepted));

	private void ApplyFilters()
	{
		string category = _categoryFilter.SelectedItem as string;
		string search = Core.Text.Simplify(_search.Text);
		int state = _stateFilter.SelectedIndex;
		_shown = _allRows.Where(row =>
		{
			if (category != null && category != "(todas)" && row.Category != category)
			{
				return false;
			}

			if (search.Length > 0 && !Core.Text.Simplify($"{row.Id} {row.Category} {row.FamilyType} {row.Level}").Contains(search))
			{
				return false;
			}

			IEnumerable<FillEntry> entries = _targets.Select(t => _cells.TryGetValue((row.Id, t), out FillEntry e) ? e : null).Where(e => e != null);
			switch (state)
			{
				case 1:
					return entries.Any(e => e.Status == FillStatus.Inferido);
				case 2:
					return entries.Any(e => e.Status == FillStatus.SinDato);
				case 3:
					return entries.Any(WillWrite);
				default:
					return true;
			}
		}).ToList();

		_table.RowCount = 0;
		_table.RowCount = _shown.Count;
		_table.Invalidate();
		UpdateSummary();
	}

	private object CellValue(int rowIndex, int column)
	{
		if (rowIndex < 0 || rowIndex >= _shown.Count)
		{
			return null;
		}

		ElementInfo row = _shown[rowIndex];
		switch (column)
		{
			case 0:
				return !_excluded.Contains(row.Id);
			case 1:
				return row.Id.ToString(CultureInfo.InvariantCulture);
			case 2:
				return row.Category;
			case 3:
				return row.FamilyType;
			case 4:
				return row.Level;
		}

		FillEntry entry = Entry(row, column);
		if (entry == null)
		{
			return "·";
		}

		return entry.Status == FillStatus.SinDato ? "(sin dato)" : entry.Status == FillStatus.Bloqueado ? entry.Current : entry.NewValue ?? entry.Current;
	}

	private bool Editable(int rowIndex, int column)
	{
		if (rowIndex < 0 || rowIndex >= _shown.Count)
		{
			return false;
		}

		if (column == 0)
		{
			return true;
		}

		FillEntry entry = Entry(_shown[rowIndex], column);
		return entry != null && entry.Status != FillStatus.Bloqueado;
	}

	/// <summary>Marcar/desmarcar la fila, o escribir un valor a mano en una celda (queda "editado a mano" y aceptado).</summary>
	private void PushValue(int rowIndex, int column, object value)
	{
		if (rowIndex < 0 || rowIndex >= _shown.Count)
		{
			return;
		}

		ElementInfo row = _shown[rowIndex];
		if (column == 0)
		{
			if (value is bool check && check)
			{
				_excluded.Remove(row.Id);
			}
			else
			{
				_excluded.Add(row.Id);
			}
		}
		else
		{
			FillEntry entry = Entry(row, column);
			string text = (value as string ?? string.Empty).Trim();
			if (entry == null || entry.Status == FillStatus.Bloqueado || text == (entry.NewValue ?? string.Empty) || text == "(sin dato)")
			{
				return;
			}

			entry.NewValue = text.Length > 0 ? text : null;
			entry.Status = text.Length == 0 ? FillStatus.SinDato : text == entry.Current ? FillStatus.Igual : FillStatus.Manual;
			entry.Accepted = entry.Status == FillStatus.Manual;
			entry.Reason = "Editado a mano en la tabla.";
		}

		_table.InvalidateRow(rowIndex);
		UpdateSummary();
	}

	private void FormatCell(DataGridViewCellFormattingEventArgs e)
	{
		if (e.RowIndex < 0 || e.RowIndex >= _shown.Count || e.ColumnIndex < FixedColumns)
		{
			return;
		}

		FillEntry entry = Entry(_shown[e.RowIndex], e.ColumnIndex);
		if (entry == null)
		{
			e.CellStyle.ForeColor = Color.LightGray;
			e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
			return;
		}

		e.CellStyle.BackColor = entry.Status switch
		{
			FillStatus.Directo => Direct,
			FillStatus.Inferido => entry.Accepted ? InferredAccepted : Inferred,
			FillStatus.SinDato => Missing,
			FillStatus.Manual => Edited,
			FillStatus.Bloqueado => Locked,
			_ => Same
		};
		if (entry.Status == FillStatus.SinDato || entry.Status == FillStatus.Igual || entry.Status == FillStatus.Conservado)
		{
			e.CellStyle.ForeColor = Color.DimGray;
		}

		if (_excluded.Contains(entry.Element.Id))
		{
			e.CellStyle.ForeColor = Color.Gray;
			e.CellStyle.Font = _strike ??= new Font(_table.Font, FontStyle.Strikeout);
		}
	}

	private string CellTip(int rowIndex, int column)
	{
		if (rowIndex < 0 || rowIndex >= _shown.Count)
		{
			return string.Empty;
		}

		FillEntry entry = Entry(_shown[rowIndex], column);
		if (entry == null)
		{
			return column >= FixedColumns ? "El elemento no tiene este parámetro." : "Doble clic: seleccionar en Revit.";
		}

		return $"{StatusText[entry.Status]}{(entry.Status == FillStatus.Inferido ? (entry.Accepted ? " (aceptado)" : " (sin aceptar)") : string.Empty)}\n" +
			$"Actual: {(entry.Current.Length > 0 ? entry.Current : "(vacío)")}\nNuevo: {entry.NewValue ?? "(sin dato)"}\n{entry.Reason}" +
			(entry.OnType ? "\nParámetro de tipo: se escribe en el tipo." : string.Empty);
	}

	private List<ElementInfo> SelectedRows() =>
		_table.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct()
			.Where(i => i >= 0 && i < _shown.Count).Select(i => _shown[i]).ToList();

	private void AcceptInferred(IEnumerable<ElementInfo> rows)
	{
		int accepted = 0;
		foreach (ElementInfo row in rows)
		{
			foreach (string target in _targets)
			{
				if (_cells.TryGetValue((row.Id, target), out FillEntry entry) && entry.Status == FillStatus.Inferido && !entry.Accepted)
				{
					entry.Accepted = true;
					accepted++;
				}
			}
		}

		_table.Invalidate();
		UpdateSummary();
		_status.Text = accepted == 0 ? "No hay valores deducidos por aceptar en esas filas." : $"{accepted} valor(es) deducidos aceptados.";
	}

	private void UpdateSummary()
	{
		List<FillEntry> entries = _cells.Values.Distinct().ToList();
		int toWrite = entries.Count(WillWrite);
		int pending = entries.Count(e => e.Status == FillStatus.Inferido && !e.Accepted);
		int missing = entries.Count(e => e.Status == FillStatus.SinDato);
		_summary.Text = $"Mostrando {_shown.Count} de {_allRows.Count} elementos  ·  se escribirán {toWrite} valor(es)  ·  deducidos por aceptar: {pending}  ·  sin dato: {missing}";
	}

	private void WriteTable()
	{
		List<FillEntry> entries = _cells.Values.Distinct().Where(WillWrite).ToList();
		if (entries.Count == 0)
		{
			MessageBox.Show(this, "No hay valores para escribir (pulsa \"Crear contenido de parámetros\" en la hoja 1, o acepta los deducidos).", Text,
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		int pending = _cells.Values.Distinct().Count(e => e.Status == FillStatus.Inferido && !e.Accepted);
		string summary = string.Join("\n", entries.GroupBy(e => e.Target).Select(g => $"  {g.Key}: {g.Count()}"));
		if (MessageBox.Show(this, $"Escribir {entries.Count} valor(es) en el modelo:\n\n{summary}" +
				(pending > 0 ? $"\n\n{pending} valor(es) deducidos sin aceptar no se escriben." : string.Empty) +
				"\n\nSe deshace con Ctrl+Z en Revit.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
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
		using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"Contenido de parámetros - {_documentTitle}.csv" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
		var header = new List<string> { "Escribir", "Id", "Categoría", "Familia : Tipo", "Nivel" };
		foreach (string target in _targets)
		{
			header.Add(target);
			header.Add(target + " (estado)");
		}

		writer.WriteLine(string.Join(";", header.Select(Escape)));
		foreach (ElementInfo row in _shown)
		{
			var cells = new List<string>
			{
				_excluded.Contains(row.Id) ? "No" : "Sí", row.Id.ToString(CultureInfo.InvariantCulture), row.Category, row.FamilyType, row.Level
			};
			foreach (string target in _targets)
			{
				FillEntry entry = _cells.TryGetValue((row.Id, target), out FillEntry e) ? e : null;
				cells.Add(entry == null ? string.Empty : entry.NewValue ?? entry.Current);
				cells.Add(entry == null ? "No tiene el parámetro" : StatusText[entry.Status] + (entry.Status == FillStatus.Inferido && entry.Accepted ? " (aceptado)" : string.Empty));
			}

			writer.WriteLine(string.Join(";", cells.Select(Escape)));
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
		_scopeChecked.Text = "las filas marcadas en la tabla (hoja 2)";
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
			ids = _shown.Where(r => !_excluded.Contains(r.Id)).Select(r => r.Id).ToList();
		}
		else
		{
			string category = _freeCategory.SelectedItem as string;
			ids = _plan.Elements.Values.Where(e => e.Category == category).Select(e => e.Id).ToList();
		}

		if (ids.Count == 0)
		{
			MessageBox.Show(this, _scopeSelection.Checked ? "Selecciona elementos en Revit y pulsa \"Leer selección\"." : "No hay elementos en el alcance (pulsa \"Crear contenido de parámetros\" en la hoja 1).",
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
			MessageBox.Show(this, "Pulsa \"Crear contenido de parámetros\" en la hoja 1: el parámetro debe estar en la lista (por ejemplo como \"Libre\").", Text,
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
