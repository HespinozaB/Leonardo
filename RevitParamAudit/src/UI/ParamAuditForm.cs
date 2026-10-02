using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using RevitParamAudit.Commands;

namespace RevitParamAudit.UI;

/// <summary>
/// Ventana no modal con el resultado de la auditoría: parámetros usados en planos, usados en tablas, residuales y
/// el total. Los residuales se pueden marcar y eliminar; todo se puede exportar a CSV.
/// </summary>
internal sealed class ParamAuditForm : Form
{
	private static readonly string[] ColumnNames =
	{
		"Nombre", "Origen", "Tipo de dato", "Grupo", "Vínculo", "Categorías", "Usos en planos", "Tablas", "Advertencias", "Id"
	};

	private static readonly int[] ColumnWidths = { 220, 80, 110, 130, 80, 240, 280, 280, 320, 70 };

	private readonly ParamAuditHandler _handler;

	private readonly ExternalEvent _event;

	private readonly TextBox _search = new TextBox();

	private readonly CheckBox _deep = new CheckBox();

	private readonly Button _refresh = new Button();

	private readonly TabControl _tabs = new TabControl();

	private readonly ListView _sheets = NewList(false);

	private readonly ListView _schedules = NewList(false);

	private readonly ListView _unused = NewList(true);

	private readonly ListView _all = NewList(false);

	private readonly Label _status = new Label();

	private readonly Button _checkAll = new Button();

	private readonly Button _checkNone = new Button();

	private readonly Button _delete = new Button();

	private readonly Button _export = new Button();

	private List<ParamEntry> _entries = new List<ParamEntry>();

	private string _documentTitle = string.Empty;

	public ParamAuditForm(ParamAuditHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_event = externalEvent;
		_handler.OnResult = (result, title, message) =>
		{
			if (IsDisposed)
			{
				return;
			}

			if (InvokeRequired)
			{
				BeginInvoke(new Action(() => SetResult(result, title, message)));
			}
			else
			{
				SetResult(result, title, message);
			}
		};

		BuildLayout();
	}

	private static ListView NewList(bool checkBoxes)
	{
		var list = new ListView
		{
			Dock = DockStyle.Fill,
			View = View.Details,
			FullRowSelect = true,
			MultiSelect = true,
			HideSelection = false,
			GridLines = true,
			CheckBoxes = checkBoxes
		};
		for (int i = 0; i < ColumnNames.Length; i++)
		{
			list.Columns.Add(ColumnNames[i], ColumnWidths[i]);
		}

		list.ColumnClick += (s, e) =>
		{
			var sorter = list.ListViewItemSorter as ColumnSorter;
			bool ascending = sorter == null || sorter.Column != e.Column || !sorter.Ascending;
			list.ListViewItemSorter = new ColumnSorter(e.Column, ascending);
			list.Sort();
		};
		return list;
	}

	private void BuildLayout()
	{
		Text = "EMASY · Auditoría de parámetros";
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1250, 620);
		MinimumSize = new Size(760, 380);
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
		top.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_search.Width = 300;
		_search.TextChanged += (s, e) => Populate();
		top.Controls.Add(_search);
		_deep.Text = "Análisis profundo (valores en elementos visibles en planos; más lento)";
		_deep.AutoSize = true;
		_deep.Margin = new Padding(16, 5, 0, 0);
		_deep.CheckedChanged += (s, e) => RequestRefresh();
		top.Controls.Add(_deep);
		_refresh.Text = "Actualizar";
		_refresh.AutoSize = true;
		_refresh.Margin = new Padding(16, 1, 0, 0);
		_refresh.Click += (s, e) => RequestRefresh();
		top.Controls.Add(_refresh);

		AddTab(_sheets, "En planos");
		AddTab(_schedules, "En tablas");
		AddTab(_unused, "Sin uso (residuales)");
		AddTab(_all, "Todos");
		_tabs.Dock = DockStyle.Fill;
		_tabs.SelectedIndexChanged += (s, e) => UpdateButtons();

		_unused.ItemChecked += (s, e) => UpdateButtons();

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
		_checkAll.Text = "Marcar todos";
		_checkAll.AutoSize = true;
		_checkAll.Click += (s, e) => SetAllChecked(true);
		_checkNone.Text = "Desmarcar";
		_checkNone.AutoSize = true;
		_checkNone.Click += (s, e) => SetAllChecked(false);
		_delete.Text = "Eliminar marcados";
		_delete.AutoSize = true;
		_delete.ForeColor = Color.Firebrick;
		_delete.Click += (s, e) => DeleteChecked();
		_export.Text = "Exportar CSV…";
		_export.AutoSize = true;
		_export.Click += (s, e) => ExportCsv();
		_status.AutoSize = true;
		_status.Margin = new Padding(16, 7, 0, 0);
		bottom.Controls.AddRange(new Control[] { _checkAll, _checkNone, _delete, _export, _status });

		Controls.Add(_tabs);
		Controls.Add(bottom);
		Controls.Add(top);
		UpdateButtons();
	}

	private void AddTab(ListView list, string title)
	{
		var page = new TabPage(title);
		page.Controls.Add(list);
		_tabs.TabPages.Add(page);
	}

	private bool OnUnusedTab => _tabs.SelectedTab != null && _tabs.SelectedTab.Controls.Contains(_unused);

	private void UpdateButtons()
	{
		bool unusedTab = OnUnusedTab;
		_checkAll.Visible = unusedTab;
		_checkNone.Visible = unusedTab;
		_delete.Visible = unusedTab;
		_delete.Enabled = unusedTab && _unused.CheckedItems.Count > 0;
	}

	public void RequestRefresh()
	{
		_handler.Action = AuditAction.Refresh;
		_handler.Deep = _deep.Checked;
		_handler.DocumentTitle = _documentTitle;
		SetBusy("Analizando parámetros…");
		_event.Raise();
	}

	private void SetBusy(string text)
	{
		_status.Text = text;
		_refresh.Enabled = false;
		_delete.Enabled = false;
		Cursor = Cursors.WaitCursor;
		Update();
	}

	public void SetResult(AuditResult result, string title, string message)
	{
		_entries = result.Entries;
		_documentTitle = title ?? string.Empty;
		_refresh.Enabled = true;
		Cursor = Cursors.Default;
		Populate();
		string summary = $"{_entries.Count} parámetros · {_entries.Count(e => e.InSheets)} en planos · " +
			$"{_entries.Count(e => e.InSchedules)} en tablas · {_entries.Count(e => e.IsUnused)} residuales";
		_status.Text = string.IsNullOrEmpty(message) ? summary : summary + "  —  " + message;
		UpdateButtons();
	}

	private IEnumerable<ParamEntry> Filtered()
	{
		string text = _search.Text.Trim();
		if (text.Length == 0)
		{
			return _entries;
		}

		return _entries.Where(e => ContainsText(e.Name, text) || ContainsText(e.Categories, text) || ContainsText(e.Group, text)
			|| ContainsText(e.SchedulesText, text) || ContainsText(e.SheetsText, text));
	}

	private static bool ContainsText(string value, string text) =>
		value != null && value.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;

	private void Populate()
	{
		List<ParamEntry> visible = Filtered().ToList();
		Fill(_sheets, visible.Where(e => e.InSheets && !e.IsGlobal).ToList(), "En planos");
		Fill(_schedules, visible.Where(e => e.InSchedules && !e.IsGlobal).ToList(), "En tablas");
		Fill(_unused, visible.Where(e => e.IsUnused).ToList(), "Sin uso (residuales)");
		Fill(_all, visible, "Todos");
	}

	private void Fill(ListView list, List<ParamEntry> items, string title)
	{
		var checkedIds = new HashSet<long>(list.CheckedItems.Cast<ListViewItem>().Select(i => ((ParamEntry)i.Tag).Id));
		list.BeginUpdate();
		list.Items.Clear();
		foreach (ParamEntry entry in items)
		{
			var item = new ListViewItem(entry.Name)
			{
				Tag = entry,
				Checked = checkedIds.Contains(entry.Id)
			};
			item.SubItems.Add(entry.Scope);
			item.SubItems.Add(entry.DataType);
			item.SubItems.Add(entry.Group);
			item.SubItems.Add(entry.Binding);
			item.SubItems.Add(entry.Categories);
			item.SubItems.Add(entry.SheetsText);
			item.SubItems.Add(entry.SchedulesText);
			item.SubItems.Add(entry.WarningsText);
			item.SubItems.Add(entry.Id.ToString());
			if (entry.IsUnused && entry.Warnings.Count > 0)
			{
				item.ForeColor = Color.DarkOrange;
			}

			list.Items.Add(item);
		}

		list.EndUpdate();
		((TabPage)list.Parent).Text = $"{title} ({items.Count})";
	}

	private void SetAllChecked(bool value)
	{
		_unused.BeginUpdate();
		foreach (ListViewItem item in _unused.Items)
		{
			item.Checked = value;
		}

		_unused.EndUpdate();
		UpdateButtons();
	}

	private void DeleteChecked()
	{
		List<ParamEntry> chosen = _unused.CheckedItems.Cast<ListViewItem>().Select(i => (ParamEntry)i.Tag).ToList();
		if (chosen.Count == 0)
		{
			return;
		}

		int withValues = chosen.Count(e => e.HasValues);
		int warned = chosen.Count(e => e.Warnings.Count > 0);
		var text = new StringBuilder();
		text.AppendLine($"Se eliminarán {chosen.Count} parámetro(s) del proyecto.");
		text.AppendLine("Los valores guardados en los elementos se perderán y la acción afecta a todo el modelo.");
		if (withValues > 0)
		{
			text.AppendLine();
			text.AppendLine($"• {withValues} tienen valores escritos en elementos.");
		}

		if (warned > 0)
		{
			text.AppendLine($"• {warned} tienen advertencias (naranja): filtros, cajetín o familias. Revísalas antes de continuar.");
		}

		text.AppendLine();
		text.AppendLine("Se puede deshacer con Ctrl+Z en Revit. ¿Continuar?");
		if (MessageBox.Show(this, text.ToString(), "EMASY · Eliminar parámetros", MessageBoxButtons.YesNo,
			MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
		{
			return;
		}

		_handler.Action = AuditAction.DeleteUnused;
		_handler.Deep = _deep.Checked;
		_handler.DocumentTitle = _documentTitle;
		_handler.Ids = chosen.Select(e => e.Id).ToList();
		SetBusy("Eliminando parámetros…");
		_event.Raise();
	}

	private void ExportCsv()
	{
		if (_entries.Count == 0)
		{
			MessageBox.Show(this, "No hay datos para exportar.", "EMASY", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		string baseName = string.Concat((_documentTitle ?? "modelo").Split(Path.GetInvalidFileNameChars()));
		using (var dialog = new SaveFileDialog
		{
			Filter = "CSV (*.csv)|*.csv",
			FileName = $"Parametros_{baseName}.csv",
			Title = "Exportar auditoría de parámetros"
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}

			try
			{
				var csv = new StringBuilder();
				csv.AppendLine(string.Join(";", new[]
				{
					"Estado", "Nombre", "Origen", "Tipo de dato", "Grupo", "Vínculo", "Categorías", "Usos en planos", "Tablas",
					"Advertencias", "Con valores", "Id", "GUID"
				}.Select(Quote)));
				foreach (ParamEntry e in _entries.OrderBy(x => x.Status).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
				{
					csv.AppendLine(string.Join(";", new[]
					{
						e.Status, e.Name, e.Scope, e.DataType, e.Group, e.Binding, e.Categories, e.SheetsText, e.SchedulesText,
						e.WarningsText, e.HasValues ? "Sí" : "No", e.Id.ToString(), e.Guid
					}.Select(Quote)));
				}

				// UTF-8 con BOM para que Excel respete tildes y eñes.
				File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
				_status.Text = "Exportado: " + dialog.FileName;
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "No se pudo guardar el archivo:\n" + ex.Message, "EMASY", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}
	}

	private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

	private sealed class ColumnSorter : IComparer
	{
		public ColumnSorter(int column, bool ascending)
		{
			Column = column;
			Ascending = ascending;
		}

		public int Column { get; }

		public bool Ascending { get; }

		public int Compare(object x, object y)
		{
			string a = ((ListViewItem)x).SubItems[Column].Text;
			string b = ((ListViewItem)y).SubItems[Column].Text;
			int result = Column == ColumnNames.Length - 1 && long.TryParse(a, out long na) && long.TryParse(b, out long nb)
				? na.CompareTo(nb)
				: string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
			return Ascending ? result : -result;
		}
	}
}
