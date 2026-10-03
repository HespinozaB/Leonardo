using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using RevitParamAudit.Core;

namespace RevitParamAudit.UI;

/// <summary>
/// Ventana no modal común a los cuatro depuradores: tabla con casillas para elegir qué eliminar, columnas de estado
/// (✓ verde, ✗ rojo, ⚠ ámbar), columnas opcionales que se activan una a una, y acciones Eliminar / Ubicar /
/// Seleccionar / Exportar / Siguiente paso. Las acciones se envían a Revit mediante un ExternalEvent.
/// </summary>
internal sealed class DepuradorForm : Form
{
	/// <summary>Columnas visibles de cada depurador durante la sesión de Revit.</summary>
	private static readonly Dictionary<ToolKind, bool[]> RememberedColumns = new Dictionary<ToolKind, bool[]>();

	private static readonly Color Green = Color.FromArgb(0, 140, 60);

	private static readonly Color Red = Color.FromArgb(210, 35, 35);

	private static readonly Color Amber = Color.FromArgb(225, 135, 0);

	private readonly IDepurador _tool;

	private readonly DepuradorHandler _handler;

	private readonly ExternalEvent _event;

	private readonly TextBox _search = new TextBox();

	private readonly ComboBox _filter = new ComboBox();

	private readonly Button _columns = new Button();

	private readonly ContextMenuStrip _columnsMenu = new ContextMenuStrip();

	private readonly ToolStripMenuItem _deep = new ToolStripMenuItem();

	private readonly Button _refresh = new Button();

	private readonly ListView _list = new BufferedListView();

	private readonly Button _checkShown = new Button();

	private readonly Button _checkNone = new Button();

	private readonly Button _delete = new Button();

	private readonly Button _locate = new Button();

	private readonly Button _select = new Button();

	private readonly Button _export = new Button();

	private readonly Button _next = new Button();

	private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();

	private readonly Font _markFont = new Font("Segoe UI Symbol", 10f, FontStyle.Bold);

	private readonly HashSet<long> _checked = new HashSet<long>();

	private readonly bool[] _visible;

	private int[] _shown = new int[0];

	private List<DepRow> _rows = new List<DepRow>();

	private string _documentTitle = string.Empty;

	private bool _filling;

	private bool _busy;

	private int _sortColumn = -1;

	private bool _sortAscending = true;

	public DepuradorForm(IDepurador tool, DepuradorHandler handler, ExternalEvent externalEvent)
	{
		_tool = tool;
		_handler = handler;
		_event = externalEvent;
		_visible = RememberedColumns.TryGetValue(tool.Kind, out bool[] remembered) && remembered.Length == tool.Columns.Length
			? (bool[])remembered.Clone()
			: tool.Columns.Select(c => !c.HiddenByDefault).ToArray();
		_handler.OnRows = (rows, title, message) => RunOnUi(() => SetRows(rows, title, message));
		_handler.OnMessage = message => RunOnUi(() => SetMessage(message));
		BuildLayout();
		RebuildColumns();
		FormClosed += (s, e) =>
		{
			_event.Dispose();
			_markFont.Dispose();
		};
	}

	private void RunOnUi(Action action)
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

	// ----------------------------------------------------------------------------------------------------------------
	// Diseño

	private void BuildLayout()
	{
		Text = "EMASY · " + _tool.Title;
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1250, 640);
		MinimumSize = new Size(820, 400);
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
		top.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_search.Width = 240;
		_search.TextChanged += (s, e) => Populate();
		top.Controls.Add(_search);
		top.Controls.Add(new Label { Text = "Mostrar:", AutoSize = true, Margin = new Padding(16, 6, 4, 0) });
		_filter.DropDownStyle = ComboBoxStyle.DropDownList;
		_filter.Width = 230;
		_filter.Items.AddRange(_tool.Filters.Select(f => (object)f.Name).ToArray());
		_filter.SelectedIndex = 0;
		_filter.SelectedIndexChanged += (s, e) => Populate();
		top.Controls.Add(_filter);
		_columns.Text = _tool.HasDeepOption ? "Columnas y opciones ▾" : "Columnas ▾";
		_columns.AutoSize = true;
		_columns.Margin = new Padding(16, 1, 0, 0);
		_columns.Click += (s, e) => _columnsMenu.Show(_columns, new Point(0, _columns.Height));
		top.Controls.Add(_columns);
		_refresh.Text = "Actualizar";
		_refresh.AutoSize = true;
		_refresh.Margin = new Padding(8, 1, 0, 0);
		_refresh.Click += (s, e) => RequestRefresh();
		top.Controls.Add(_refresh);
		BuildColumnsMenu();

		var guide = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(10, 2, 8, 0), WrapContents = false };
		guide.Controls.Add(new Label { Text = _tool.Guide, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 18, 0) });
		guide.Controls.Add(LegendLabel(Marks.Yes + " se puede eliminar", Green));
		guide.Controls.Add(LegendLabel(Marks.Alert + " revisar antes", Amber));
		guide.Controls.Add(LegendLabel(Marks.No + " en uso", Red));

		_list.Dock = DockStyle.Fill;
		_list.View = View.Details;
		_list.FullRowSelect = true;
		_list.MultiSelect = true;
		_list.HideSelection = false;
		_list.GridLines = true;
		_list.CheckBoxes = true;
		_list.ColumnClick += (s, e) =>
		{
			int column = _shown[e.Column];
			_sortAscending = column != _sortColumn || !_sortAscending;
			_sortColumn = column;
			Populate();
		};
		_list.ItemCheck += (s, e) =>
		{
			if (!_filling && e.NewValue == CheckState.Checked && _list.Items[e.Index].Tag is DepRow row && row.Locked)
			{
				e.NewValue = CheckState.Unchecked;
			}
		};
		_list.ItemChecked += (s, e) =>
		{
			if (_filling || !(e.Item.Tag is DepRow row))
			{
				return;
			}

			if (e.Item.Checked)
			{
				_checked.Add(row.Id);
			}
			else
			{
				_checked.Remove(row.Id);
			}

			UpdateButtons();
		};
		_list.SelectedIndexChanged += (s, e) => UpdateButtons();
		_list.DoubleClick += (s, e) =>
		{
			if (_tool.CanLocate)
			{
				Raise(DepAction.Locate, TargetIds(), "Abriendo…");
			}
		};

		var bottom = new Panel { Dock = DockStyle.Bottom, Height = 42 };
		var left = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 7, 0, 0), WrapContents = false };
		var right = new FlowLayoutPanel
		{
			Dock = DockStyle.Right,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Padding = new Padding(0, 7, 8, 0),
			WrapContents = false
		};
		AddButton(left, _checkShown, "Marcar los mostrados", (s, e) => SetShownChecked(true));
		AddButton(left, _checkNone, "Desmarcar todos", (s, e) => UncheckAll());
		AddButton(left, _delete, "Eliminar marcados", (s, e) => DeleteChecked());
		_delete.ForeColor = Color.Firebrick;
		if (_tool.CanLocate)
		{
			AddButton(left, _locate, "Ubicar", (s, e) => Raise(DepAction.Locate, TargetIds(), "Abriendo…"));
		}

		if (_tool.CanSelect)
		{
			AddButton(left, _select, "Seleccionar", (s, e) => Raise(DepAction.Select, TargetIds(), "Seleccionando…"));
		}

		AddButton(left, _export, "Exportar CSV…", (s, e) => ExportCsv());
		ToolKind? next = DepuradorTools.Next(_tool.Kind);
		if (next != null)
		{
			AddButton(right, _next, $"Siguiente: {DepuradorTools.ShortName(next.Value)} ▸",
				(s, e) => Raise(DepAction.OpenNext, new List<long>(), "Abriendo el siguiente paso…"));
			_next.Font = new Font(Font, FontStyle.Bold);
		}

		var tips = new ToolTip();
		tips.SetToolTip(_locate, "Abre la vista o plano de la fila elegida (también con doble clic).");
		tips.SetToolTip(_select, "Selecciona en Revit los elementos de las filas elegidas (o de las marcadas si no hay filas elegidas).");
		tips.SetToolTip(_delete, "Elimina del modelo todo lo marcado con la casilla. Pide confirmación y se deshace con Ctrl+Z.");

		bottom.Controls.Add(left);
		bottom.Controls.Add(right);

		var strip = new StatusStrip { ShowItemToolTips = true };
		_status.Spring = true;
		_status.TextAlign = ContentAlignment.MiddleLeft;
		strip.Items.Add(_status);

		Controls.Add(_list);
		Controls.Add(guide);
		Controls.Add(top);
		Controls.Add(bottom);
		Controls.Add(strip);
		UpdateButtons();
	}

	private static void AddButton(FlowLayoutPanel panel, Button button, string text, EventHandler click)
	{
		button.Text = text;
		button.AutoSize = true;
		button.Margin = new Padding(0, 0, 6, 0);
		button.Click += click;
		panel.Controls.Add(button);
	}

	private static Label LegendLabel(string text, Color color) =>
		new Label { Text = text, AutoSize = true, ForeColor = color, Font = new Font("Segoe UI Symbol", 9f, FontStyle.Bold), Margin = new Padding(0, 3, 14, 0) };

	private void BuildColumnsMenu()
	{
		_columnsMenu.Items.Add(new ToolStripMenuItem("Mostrar columnas:") { Enabled = false });
		for (int i = 0; i < _tool.Columns.Length; i++)
		{
			if (!_tool.Columns[i].Optional)
			{
				continue;
			}

			int index = i;
			var item = new ToolStripMenuItem(_tool.Columns[i].Name) { CheckOnClick = true, Checked = _visible[i] };
			item.CheckedChanged += (s, e) =>
			{
				_visible[index] = item.Checked;
				RememberedColumns[_tool.Kind] = (bool[])_visible.Clone();
				RebuildColumns();
				Populate();
			};
			_columnsMenu.Items.Add(item);
		}

		if (_tool.HasDeepOption)
		{
			_columnsMenu.Items.Add(new ToolStripSeparator());
			_deep.Text = "Análisis profundo (valores en elementos visibles en planos; más lento)";
			_deep.CheckOnClick = true;
			_deep.CheckedChanged += (s, e) => RequestRefresh();
			_columnsMenu.Items.Add(_deep);
		}

		// El menú queda abierto para activar varias columnas una a una.
		_columnsMenu.Closing += (s, e) =>
		{
			if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
			{
				e.Cancel = true;
			}
		};
	}

	private void RebuildColumns()
	{
		_shown = Enumerable.Range(0, _tool.Columns.Length).Where(i => _visible[i]).ToArray();
		_filling = true;
		_list.BeginUpdate();
		_list.Items.Clear();
		_list.Columns.Clear();
		foreach (int i in _shown)
		{
			ColumnSpec spec = _tool.Columns[i];
			_list.Columns.Add(spec.Name, spec.Width, spec.Center ? HorizontalAlignment.Center : HorizontalAlignment.Left);
		}

		_list.EndUpdate();
		_filling = false;
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Datos

	public void RequestRefresh() => Raise(DepAction.Refresh, new List<long>(), "Analizando…");

	private void Raise(DepAction action, List<long> ids, string busyText)
	{
		if (_busy)
		{
			return;
		}

		_handler.Action = action;
		_handler.Deep = _deep.Checked;
		_handler.DocumentTitle = _documentTitle;
		_handler.Ids = ids;
		_busy = true;
		SetStatus(busyText);
		Cursor = Cursors.WaitCursor;
		UpdateButtons();
		Update();
		_event.Raise();
	}

	public void SetRows(List<DepRow> rows, string title, string message)
	{
		_rows = rows ?? new List<DepRow>();
		_documentTitle = title ?? string.Empty;
		_checked.IntersectWith(_rows.Where(r => !r.Locked).Select(r => r.Id));
		_busy = false;
		Cursor = Cursors.Default;
		Populate();
		SetStatus(string.IsNullOrEmpty(message) ? Summary() : Summary() + "  —  " + message);
	}

	private void SetMessage(string message)
	{
		_busy = false;
		Cursor = Cursors.Default;
		SetStatus(string.IsNullOrEmpty(message) ? Summary() : message);
		UpdateButtons();
	}

	private void SetStatus(string text)
	{
		_status.Text = text;
		_status.ToolTipText = text;
	}

	private string Summary() =>
		$"{_rows.Count} {_tool.Noun}  ·  {Marks.Yes} {_rows.Count(r => r.Verdict == Verdict.Delete)} para eliminar  ·  " +
		$"{Marks.Alert} {_rows.Count(r => r.Verdict == Verdict.Review)} por revisar  ·  {Marks.No} {_rows.Count(r => r.Verdict == Verdict.Keep)} en uso";

	private List<DepRow> ShownRows()
	{
		IEnumerable<DepRow> rows = _rows;
		if (_filter.SelectedIndex > 0)
		{
			rows = rows.Where(_tool.Filters[_filter.SelectedIndex].Predicate);
		}

		string text = _search.Text.Trim();
		if (text.Length > 0)
		{
			rows = rows.Where(r => r.Cells.Any(c => c != null && c.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0));
		}

		if (_sortColumn >= 0)
		{
			int column = _sortColumn;
			var comparer = new CellComparer(_tool.Columns[column].Numeric);
			rows = _sortAscending ? rows.OrderBy(r => r.Cells[column], comparer) : rows.OrderByDescending(r => r.Cells[column], comparer);
		}

		return rows.ToList();
	}

	private void Populate()
	{
		List<DepRow> rows = ShownRows();
		_filling = true;
		_list.BeginUpdate();
		_list.Items.Clear();
		_list.Items.AddRange(rows.Select(MakeItem).ToArray());
		_list.EndUpdate();
		_filling = false;
		UpdateButtons();
	}

	private ListViewItem MakeItem(DepRow row)
	{
		var item = new ListViewItem { Tag = row, UseItemStyleForSubItems = false, Checked = _checked.Contains(row.Id) };
		for (int k = 0; k < _shown.Length; k++)
		{
			string text = row.Cells[_shown[k]] ?? string.Empty;
			ListViewItem.ListViewSubItem sub;
			if (k == 0)
			{
				item.Text = text;
				sub = item.SubItems[0];
			}
			else
			{
				sub = item.SubItems.Add(text);
			}

			Color? color = MarkColor(text);
			if (color != null)
			{
				sub.ForeColor = color.Value;
				if (text.Length <= 2)
				{
					sub.Font = _markFont;
				}
			}
		}

		if (row.Locked)
		{
			item.SubItems[0].ForeColor = SystemColors.GrayText;
		}

		return item;
	}

	private static Color? MarkColor(string text)
	{
		if (text.StartsWith(Marks.Yes, StringComparison.Ordinal))
		{
			return Green;
		}

		if (text.StartsWith(Marks.No, StringComparison.Ordinal))
		{
			return Red;
		}

		if (text.StartsWith(Marks.Alert, StringComparison.Ordinal))
		{
			return Amber;
		}

		return null;
	}

	private void UpdateButtons()
	{
		int marked = _rows.Count(r => !r.Locked && _checked.Contains(r.Id));
		bool hasTarget = _list.SelectedItems.Count > 0 || marked > 0;
		_delete.Text = marked > 0 ? $"Eliminar marcados ({marked})" : "Eliminar marcados";
		_delete.Enabled = !_busy && marked > 0;
		_locate.Enabled = !_busy && hasTarget;
		_select.Enabled = !_busy && hasTarget;
		_refresh.Enabled = !_busy;
		_next.Enabled = !_busy;
		_checkShown.Enabled = !_busy;
		_checkNone.Enabled = !_busy && marked > 0;
	}

	/// <summary>Filas elegidas (resaltadas) o, si no hay, las marcadas con la casilla.</summary>
	private List<long> TargetIds()
	{
		List<long> selected = _list.SelectedItems.Cast<ListViewItem>().Select(i => ((DepRow)i.Tag).Id).ToList();
		return selected.Count > 0 ? selected : _rows.Where(r => _checked.Contains(r.Id)).Select(r => r.Id).ToList();
	}

	private void SetShownChecked(bool value)
	{
		_filling = true;
		_list.BeginUpdate();
		foreach (ListViewItem item in _list.Items)
		{
			var row = (DepRow)item.Tag;
			if (row.Locked)
			{
				continue;
			}

			item.Checked = value;
			if (value)
			{
				_checked.Add(row.Id);
			}
			else
			{
				_checked.Remove(row.Id);
			}
		}

		_list.EndUpdate();
		_filling = false;
		UpdateButtons();
	}

	private void UncheckAll()
	{
		_checked.Clear();
		SetShownChecked(false);
	}

	// ----------------------------------------------------------------------------------------------------------------
	// Acciones

	private void DeleteChecked()
	{
		List<DepRow> chosen = _rows.Where(r => !r.Locked && _checked.Contains(r.Id)).ToList();
		if (chosen.Count == 0)
		{
			return;
		}

		var text = new StringBuilder();
		text.AppendLine($"Se eliminarán {chosen.Count} {_tool.Noun} del modelo:");
		foreach (DepRow row in chosen.Take(12))
		{
			text.AppendLine("     · " + row.Label);
		}

		if (chosen.Count > 12)
		{
			text.AppendLine($"     … y {chosen.Count - 12} más");
		}

		var notes = new List<string>();
		int inUse = chosen.Count(r => r.Verdict == Verdict.Keep);
		int review = chosen.Count(r => r.Verdict == Verdict.Review);
		if (inUse > 0)
		{
			notes.Add($"{inUse} están en uso ({Marks.No}): se eliminarán igualmente porque los marcaste.");
		}

		if (review > 0)
		{
			notes.Add($"{review} tienen alertas ({Marks.Alert}): revísalas antes de continuar.");
		}

		notes.AddRange(_tool.DeleteNotes(chosen));
		if (notes.Count > 0)
		{
			text.AppendLine();
			foreach (string note in notes)
			{
				text.AppendLine("• " + note);
			}
		}

		text.AppendLine();
		text.AppendLine("Se puede deshacer con Ctrl+Z en Revit. ¿Continuar?");
		if (MessageBox.Show(this, text.ToString(), "EMASY · Eliminar " + _tool.Noun, MessageBoxButtons.YesNo,
			MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
		{
			return;
		}

		Raise(DepAction.Delete, chosen.Select(r => r.Id).ToList(), "Eliminando…");
	}

	private void ExportCsv()
	{
		if (_rows.Count == 0)
		{
			MessageBox.Show(this, "No hay datos para exportar.", "EMASY", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		string baseName = string.Concat((_documentTitle.Length > 0 ? _documentTitle : "modelo").Split(Path.GetInvalidFileNameChars()));
		string toolName = string.Concat(DepuradorTools.ShortName(_tool.Kind).Where(char.IsLetter));
		using (var dialog = new SaveFileDialog
		{
			Filter = "CSV (*.csv)|*.csv",
			FileName = $"{toolName}_{baseName}.csv",
			Title = "Exportar " + _tool.Title
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}

			try
			{
				var csv = new StringBuilder();
				csv.AppendLine(string.Join(";", _tool.Columns.Select(c => Quote(c.Name))));
				foreach (DepRow row in _rows)
				{
					csv.AppendLine(string.Join(";", row.Cells.Select(c => Quote(CsvText(c)))));
				}

				// UTF-8 con BOM para que Excel respete tildes y eñes.
				File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
				SetStatus("Exportado: " + dialog.FileName);
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, "No se pudo guardar el archivo:\n" + ex.Message, "EMASY", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}
	}

	/// <summary>En el CSV los símbolos pasan a texto: ✓ → Sí, ✗ → No, ⚠ → Revisar.</summary>
	private static string CsvText(string text)
	{
		text ??= string.Empty;
		foreach ((string mark, string word) in new[] { (Marks.Yes, "Sí"), (Marks.No, "No"), (Marks.Alert, "Revisar") })
		{
			if (text == mark)
			{
				return word;
			}

			if (text.StartsWith(mark + " ", StringComparison.Ordinal))
			{
				return word + " " + text.Substring(mark.Length + 1);
			}
		}

		return text;
	}

	private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

	/// <summary>Orden natural ("Nivel 2" antes que "Nivel 10"); numérico en las columnas de números.</summary>
	private sealed class CellComparer : IComparer<string>
	{
		private readonly bool _numeric;

		public CellComparer(bool numeric)
		{
			_numeric = numeric;
		}

		public int Compare(string a, string b)
		{
			a ??= string.Empty;
			b ??= string.Empty;
			int rankA = MarkRank(a);
			int rankB = MarkRank(b);
			if (rankA >= 0 && rankB >= 0)
			{
				return rankA.CompareTo(rankB);
			}

			if (_numeric && long.TryParse(a, out long na) && long.TryParse(b, out long nb))
			{
				return na.CompareTo(nb);
			}

			int i = 0;
			int j = 0;
			while (i < a.Length && j < b.Length)
			{
				if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
				{
					int si = i;
					int sj = j;
					while (i < a.Length && char.IsDigit(a[i]))
					{
						i++;
					}

					while (j < b.Length && char.IsDigit(b[j]))
					{
						j++;
					}

					string da = a.Substring(si, i - si).TrimStart('0');
					string db = b.Substring(sj, j - sj).TrimStart('0');
					int byLength = da.Length.CompareTo(db.Length);
					if (byLength != 0)
					{
						return byLength;
					}

					int byDigits = string.CompareOrdinal(da, db);
					if (byDigits != 0)
					{
						return byDigits;
					}
				}
				else
				{
					int byChar = string.Compare(a.Substring(i, 1), b.Substring(j, 1), StringComparison.CurrentCultureIgnoreCase);
					if (byChar != 0)
					{
						return byChar;
					}

					i++;
					j++;
				}
			}

			return (a.Length - i).CompareTo(b.Length - j);
		}
	}

	/// <summary>Orden de las columnas de estado: ✓, ⚠, ✗, — (-1 si no es una marca sola).</summary>
	private static int MarkRank(string text) => text switch
	{
		Marks.Yes => 0,
		Marks.Alert => 1,
		Marks.No => 2,
		Marks.None => 3,
		_ => -1
	};

	private sealed class BufferedListView : ListView
	{
		public BufferedListView()
		{
			DoubleBuffered = true;
		}
	}
}
