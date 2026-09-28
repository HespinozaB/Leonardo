using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using ExternalEventRequest = Autodesk.Revit.UI.ExternalEventRequest;
using RevitDwgExploder.Commands;

namespace RevitDwgExploder.UI;

/// <summary>
/// Ventana no modal "Explotar Varios DWG's": lista todos los CAD del modelo y permite explotarlos en lote,
/// seleccionarlos, ubicarlos o eliminarlos. Las acciones se envían a Revit mediante un ExternalEvent.
/// </summary>
internal sealed class DwgFinderForm : Form
{
	private readonly DwgFinderHandler _handler;

	private readonly ExternalEvent _event;

	private readonly TextBox _search = new TextBox();

	private readonly ComboBox _kindFilter = new ComboBox();

	private readonly ListView _list = new ListView();

	private readonly Label _status = new Label();

	private readonly Button _explode = new Button();

	private readonly Button _select = new Button();

	private readonly CheckBox _moreData = new CheckBox();

	/// <summary>Columnas de detalle (Nivel, Estado, Fijado, Id), ocultas salvo que se marque "Más datos".</summary>
	private static readonly int[] DetailColumns = { 4, 5, 6, 7 };

	private static readonly int[] DetailWidths = { 90, 90, 55, 80 };

	private readonly Button _locate = new Button();

	private readonly Button _delete = new Button();

	private readonly Button _refresh = new Button();

	private List<DwgFinderEntry> _entries = new List<DwgFinderEntry>();

	private int _sortColumn;

	private bool _sortAscending = true;

	private string _documentTitle = string.Empty;

	public DwgFinderForm(DwgFinderHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_event = externalEvent;
		_handler.OnResult = (entries, title, message) =>
		{
			if (IsDisposed)
			{
				return;
			}

			if (InvokeRequired)
			{
				BeginInvoke(new Action(() => SetEntries(entries, title, message)));
			}
			else
			{
				SetEntries(entries, title, message);
			}
		};

		BuildLayout();
	}

	private void BuildLayout()
	{
		Text = "EMASY · Explotar Varios DWG's";
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1150, 560);
		MinimumSize = new Size(700, 360);
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
		top.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
		_search.Width = 320;
		_search.TextChanged += (s, e) => Populate();
		top.Controls.Add(_search);
		top.Controls.Add(new Label { Text = "Mostrar:", AutoSize = true, Margin = new Padding(16, 6, 4, 0) });
		_kindFilter.DropDownStyle = ComboBoxStyle.DropDownList;
		_kindFilter.Width = 180;
		_kindFilter.Items.AddRange(new object[] { "Todos", "Vinculados", "Importados", "Sin instancias" });
		_kindFilter.SelectedIndex = 0;
		_kindFilter.SelectedIndexChanged += (s, e) => Populate();
		top.Controls.Add(_kindFilter);
		_moreData.Text = "Más datos (nivel, estado, fijado, Id)";
		_moreData.AutoSize = true;
		_moreData.Margin = new Padding(16, 5, 0, 0);
		_moreData.CheckedChanged += (s, e) => ShowDetailColumns(_moreData.Checked);
		top.Controls.Add(_moreData);

		_list.Dock = DockStyle.Fill;
		_list.View = System.Windows.Forms.View.Details;
		_list.FullRowSelect = true;
		_list.MultiSelect = true;
		_list.HideSelection = false;
		_list.GridLines = true;
		_list.Columns.Add("Archivo", 230);
		_list.Columns.Add("Tipo", 150);
		_list.Columns.Add("Ubicación", 110);
		_list.Columns.Add("Vista", 220);
		_list.Columns.Add("Nivel", 90);
		_list.Columns.Add("Estado", 90);
		_list.Columns.Add("Fijado", 55);
		_list.Columns.Add("Id", 70);
		_list.Columns.Add("Ruta", 300);
		ShowDetailColumns(false);
		_list.ColumnWidthChanging += (s, e) =>
		{
			// Las columnas ocultas no se pueden abrir arrastrando: se usan con "Más datos".
			if (!_moreData.Checked && DetailColumns.Contains(e.ColumnIndex))
			{
				e.Cancel = true;
				e.NewWidth = 0;
			}
		};
		_list.ColumnClick += (s, e) =>
		{
			_sortAscending = _sortColumn != e.Column || !_sortAscending;
			_sortColumn = e.Column;
			Populate();
		};
		_list.DoubleClick += (s, e) => Run(DwgFinderAction.Locate);
		_list.SelectedIndexChanged += (s, e) => UpdateButtons();

		var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8) };
		var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
		SetupButton(_explode, "Explotar…", ConfirmExplode);
		_explode.Font = new Font(Font, FontStyle.Bold);
		SetupButton(_select, "Seleccionar", () => Run(DwgFinderAction.Select));
		SetupButton(_locate, "Ubicar", () => Run(DwgFinderAction.Locate));
		SetupButton(_delete, "Eliminar…", ConfirmDelete);
		SetupButton(_refresh, "Actualizar", () => Run(DwgFinderAction.Refresh));
		var close = new Button { Text = "Cerrar", Width = 90, Height = 28 };
		close.Click += (s, e) => Close();
		buttons.Controls.AddRange(new Control[] { _explode, _select, _locate, _delete, _refresh, close });
		_status.Dock = DockStyle.Fill;
		_status.TextAlign = ContentAlignment.MiddleLeft;
		bottom.Controls.Add(_status);
		bottom.Controls.Add(buttons);

		Controls.Add(_list);
		Controls.Add(top);
		Controls.Add(bottom);
		CancelButton = close;
		UpdateButtons();
	}

	private void ShowDetailColumns(bool show)
	{
		for (int i = 0; i < DetailColumns.Length; i++)
		{
			_list.Columns[DetailColumns[i]].Width = show ? DetailWidths[i] : 0;
		}
	}

	private void ConfirmExplode()
	{
		List<DwgFinderEntry> selected = SelectedEntries().Where(e => e.IsInstance).ToList();
		if (selected.Count == 0)
		{
			return;
		}

		using (var dialog = new ExplodeOptionsDialog(selected.Count))
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}

			_handler.AdjustScale = dialog.AdjustScale;
			_handler.DeleteOriginals = dialog.DeleteOriginals;
		}

		Run(DwgFinderAction.Explode);
	}

	private static void SetupButton(Button button, string text, Action action)
	{
		button.Text = text;
		button.Width = 100;
		button.Height = 28;
		button.Click += (s, e) => action();
	}

	public void SetEntries(List<DwgFinderEntry> entries, string documentTitle, string message)
	{
		_entries = entries ?? new List<DwgFinderEntry>();
		_documentTitle = documentTitle ?? string.Empty;
		Populate();
		int linked = _entries.Count(e => e.IsInstance && e.Kind == "Vinculado");
		int imported = _entries.Count(e => e.IsInstance && e.Kind == "Importado");
		int orphan = _entries.Count(e => !e.IsInstance);
		string summary = $"{_documentTitle}: {linked + imported} CAD en el modelo ({linked} vinculados, {imported} importados)" +
			(orphan > 0 ? $", {orphan} archivo(s) sin instancias" : string.Empty) + ".";
		_status.Text = string.IsNullOrEmpty(message) ? summary : message + "   " + summary;
		SetBusy(false);
	}

	public void RequestRefresh() => Run(DwgFinderAction.Refresh);

	private void Populate()
	{
		string term = _search.Text.Trim();
		IEnumerable<DwgFinderEntry> rows = _entries;
		if (term.Length > 0)
		{
			rows = rows.Where(e => Columns(e).Any(v => v.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0));
		}

		rows = _kindFilter.SelectedIndex switch
		{
			1 => rows.Where(e => e.Kind.StartsWith("Vinculado", StringComparison.Ordinal)),
			2 => rows.Where(e => e.Kind.StartsWith("Importado", StringComparison.Ordinal)),
			3 => rows.Where(e => !e.IsInstance),
			_ => rows
		};

		rows = _sortColumn == 7
			? (_sortAscending ? rows.OrderBy(e => e.Id) : rows.OrderByDescending(e => e.Id))
			: (_sortAscending
				? rows.OrderBy(e => Columns(e)[_sortColumn], StringComparer.CurrentCultureIgnoreCase)
				: rows.OrderByDescending(e => Columns(e)[_sortColumn], StringComparer.CurrentCultureIgnoreCase));

		var selected = new HashSet<long>(SelectedEntries().Select(e => e.Id));
		_list.BeginUpdate();
		_list.Items.Clear();
		foreach (DwgFinderEntry entry in rows)
		{
			var item = new ListViewItem(Columns(entry)) { Tag = entry, Selected = selected.Contains(entry.Id) };
			if (!entry.IsInstance)
			{
				item.ForeColor = SystemColors.GrayText;
			}
			else if (entry.Status == "No encontrado" || entry.Status == "Descargado")
			{
				item.ForeColor = Color.Firebrick;
			}

			_list.Items.Add(item);
		}

		_list.EndUpdate();
		UpdateButtons();
	}

	private static string[] Columns(DwgFinderEntry e) => new[]
	{
		e.Name ?? string.Empty,
		e.Kind ?? string.Empty,
		e.Location ?? string.Empty,
		e.Views ?? string.Empty,
		e.Level ?? string.Empty,
		e.Status ?? string.Empty,
		e.Pinned ? "Sí" : "No",
		e.Id.ToString(CultureInfo.InvariantCulture),
		e.Path ?? string.Empty
	};

	private List<DwgFinderEntry> SelectedEntries() =>
		_list.SelectedItems.Cast<ListViewItem>().Select(i => (DwgFinderEntry)i.Tag).ToList();

	private void UpdateButtons()
	{
		List<DwgFinderEntry> selected = SelectedEntries();
		bool anyInstance = selected.Any(e => e.IsInstance);
		_explode.Enabled = anyInstance;
		_select.Enabled = anyInstance;
		_locate.Enabled = anyInstance;
		_delete.Enabled = selected.Count > 0;
	}

	private void ConfirmDelete()
	{
		List<DwgFinderEntry> selected = SelectedEntries();
		if (selected.Count == 0)
		{
			return;
		}

		int pinned = selected.Count(e => e.Pinned);
		string text = $"¿Eliminar {selected.Count} DWG del modelo?" +
			(pinned > 0 ? $"\n\n{pinned} está(n) fijado(s); se desfijarán para eliminarlos." : string.Empty) +
			"\n\nTambién se quita el archivo DWG del proyecto cuando no le quedan más instancias, así no queda en la lista." +
			"\n\nLa acción se puede deshacer con Ctrl+Z en Revit.";
		if (MessageBox.Show(this, text, "EMASY · Eliminar DWG", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
		{
			return;
		}

		_handler.DeleteFiles = true;
		Run(DwgFinderAction.Delete);
	}

	private void Run(DwgFinderAction action)
	{
		List<DwgFinderEntry> selected = SelectedEntries();
		if (action != DwgFinderAction.Refresh && selected.Count == 0)
		{
			return;
		}

		_handler.Action = action;
		_handler.DocumentTitle = _documentTitle;
		_handler.Ids = selected.Select(e => e.Id).ToList();
		SetBusy(true);
		ExternalEventRequest request = _event.Raise();
		if (request != ExternalEventRequest.Accepted && request != ExternalEventRequest.Pending)
		{
			SetBusy(false);
			_status.Text = "Revit está ocupado; vuelve a intentarlo en un momento.";
		}
	}

	private void SetBusy(bool busy)
	{
		UseWaitCursor = busy;
		_refresh.Enabled = !busy;
		if (busy)
		{
			_explode.Enabled = _select.Enabled = _locate.Enabled = _delete.Enabled = false;
		}
		else
		{
			UpdateButtons();
		}
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_handler.OnResult = null;
		_event.Dispose();
		base.OnFormClosed(e);
	}
}
