using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RevitParamFill.Commands;
using RevitParamFill.Core;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;

namespace RevitParamFill.UI;

/// <summary>
/// Ventana no modal en 4 hojas: Parámetros (qué parámetro se llena y con qué), Validar contenido (tabla por elemento con el valor
/// que quedaría), Sectorización (sectores dibujados en planta o en un DWG) y Libre (masivo o personalizado).
/// </summary>
internal sealed partial class FillForm : Form
{
	private const int TabRules = 0;
	private const int TabValidate = 1;

	private const int ColTarget = 0;
	private const int ColSource = 1;
	private const int ColDetail = 2;
	private const int ColPrefix = 3;
	private const int ColSuffix = 4;
	private const int ColOverwrite = 5;
	private const int ColExists = 6;

	private readonly FillHandler _handler;
	private readonly ExternalEvent _event;
	private readonly TabControl _tabs = new TabControl();
	private readonly Label _status = new Label();
	private readonly DataGridView _rules = new DataGridView();
	private readonly Label _configLabel = new Label();

	private RuleSet _set = new RuleSet();
	private ModelCatalog _catalog = new ModelCatalog();
	private string _documentTitle = string.Empty;
	private string _configName = "Última usada";
	private string _configPath;
	private bool _busy;
	private bool _filling;

	public FillForm(FillHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_event = externalEvent;
		_handler.OnCatalog = (catalog, title) => OnUi(() => SetCatalog(catalog, title));
		_handler.OnSectors = (index, sectors, message) => OnUi(() => SetSectors(index, sectors, message));
		_handler.OnLayers = (index, layers) => OnUi(() => SetLayers(index, layers));
		_handler.OnPlan = (plan, title) => OnUi(() => SetPlan(plan, title));
		_handler.OnSelection = ids => OnUi(() => SetRevitSelection(ids));
		_handler.OnMessage = (message, written) => OnUi(() => SetMessage(message, written));
		BuildLayout();
	}

	/// <summary>Carga la última configuración (o el preajuste SUNAT) y lee lo que ofrece el modelo.</summary>
	public void Start()
	{
		Safe(() =>
		{
			RuleSet last = File.Exists(RuleSet.DefaultPath) ? TryLoad(RuleSet.DefaultPath) : null;
			_set = last ?? RuleSet.Sunat();
			_configName = last != null ? Settings.Get("configName") ?? "Última usada" : "SUNAT (Tabla 5)";
			_configPath = last != null ? Settings.Get("configPath") : null;
			Simplify(_set);
			FillRules();
			FillSectorList();
			ShowConfigName();
			Raise(FillAction.Prepare, "Leyendo parámetros, vistas y DWG del modelo…");
		});
	}

	#region Infraestructura

	private void OnUi(Action action)
	{
		if (IsDisposed)
		{
			return;
		}

		if (InvokeRequired)
		{
			BeginInvoke(new Action(() => Safe(action)));
		}
		else
		{
			Safe(action);
		}
	}

	/// <summary>Un error se muestra una sola vez en la barra de estado (sin ventanas de excepción).</summary>
	private void Safe(Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			SetBusy(false, "Error: " + ex.Message);
		}
	}

	private EventHandler Do(Action action) => (s, e) => Safe(action);

	private void Raise(FillAction action, string busyText)
	{
		if (_busy)
		{
			_status.Text = "Espera a que termine la operación en curso…";
			return;
		}

		ReadRules();
		SaveDefault();
		_handler.Action = action;
		_handler.Rules = CloneSet();
		_handler.DocumentTitle = _documentTitle;
		if (busyText != null)
		{
			SetBusy(true, busyText);
		}

		_event.Raise();
	}

	/// <summary>Copia de las reglas para el hilo de Revit (los sectores leídos se comparten).</summary>
	private RuleSet CloneSet() => new RuleSet
	{
		Rules = _set.Rules.Select(r => r.Clone()).ToList(),
		SectorSets = _set.SectorSets.Select(s => s.Clone()).ToList()
	};

	private void SetBusy(bool busy, string text)
	{
		_busy = busy;
		if (text != null)
		{
			_status.Text = text;
		}

		Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
	}

	private void SetMessage(string message, bool written)
	{
		SetBusy(false, message);
		if (written)
		{
			_status.Text = message + "  ·  Pulsa \"Recalcular\" para ver el estado nuevo.";
		}
	}

	private static Button NewButton(string text, EventHandler click, bool bold = false)
	{
		var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
		button.Click += click;
		if (bold)
		{
			button.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
		}

		return button;
	}

	private static Label NewLabel(string text, bool wrap = false) => new Label
	{
		Text = text,
		AutoSize = true,
		MaximumSize = wrap ? new Size(1150, 0) : Size.Empty,
		Margin = new Padding(0, 6, 6, 0)
	};

	private static RuleSet TryLoad(string path)
	{
		try
		{
			return RuleSet.Load(path);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Sin equivalencias ni deducción configurable: la deducción queda siempre con sus valores por defecto.</summary>
	private static void Simplify(RuleSet set)
	{
		foreach (FillRule rule in set.Rules)
		{
			rule.Enabled = true;
			rule.ValueMap.Clear();
			rule.Keywords.Clear();
			rule.Default = string.Empty;
			rule.InferSameType = true;
			rule.InferNearest = true;
			rule.InferKeywords = true;
		}
	}

	private void SaveDefault()
	{
		try
		{
			_set.Save(RuleSet.DefaultPath);
			Settings.Set("configName", _configName);
			Settings.Set("configPath", _configPath ?? string.Empty);
		}
		catch (Exception)
		{
			// Guardar la configuración es una comodidad: si falla se sigue sin ello.
		}
	}

	#endregion

	#region Layout

	private void BuildLayout()
	{
		Text = "EMASY · Rellenar parámetros";
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1400, 800);
		MinimumSize = new Size(950, 500);
		Font = new Font("Segoe UI", 9.5f);
		ShowInTaskbar = false;

		_tabs.Dock = DockStyle.Fill;
		_tabs.Padding = new Point(12, 5);
		_tabs.TabPages.Add(BuildRulesPage());
		_tabs.TabPages.Add(BuildValidatePage());
		_tabs.TabPages.Add(BuildSectorsPage());
		_tabs.TabPages.Add(BuildFreePage());
		_tabs.Selecting += (s, e) => Safe(ReadRules);

		_status.Dock = DockStyle.Bottom;
		_status.Height = 24;
		_status.Padding = new Padding(8, 5, 0, 0);
		_status.BackColor = SystemColors.ControlLight;

		Controls.Add(_tabs);
		Controls.Add(_status);
		FormClosing += (s, e) => Safe(() =>
		{
			ReadRules();
			SaveDefault();
		});
	}

	private TabPage BuildRulesPage()
	{
		var page = new TabPage("1 · Parámetros") { BackColor = Color.White };
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(12, 10, 12, 0), BackColor = Color.White };
		top.Controls.Add(NewLabel("Indica qué parámetro se llena y con qué: un dato del elemento (categoría, tipo, nivel, material u otro parámetro), un valor fijo o una sectorización. Pulsa \"Crear contenido de parámetros\" para calcularlo y validarlo en la hoja 2 antes de escribir.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		top.Controls.Add(NewButton("+ Añadir parámetro", Do(() =>
		{
			ReadRules();
			_set.Rules.Add(new FillRule { Source = SourceKind.Fijo });
			FillRules();
			_rules.CurrentCell = _rules.Rows[_rules.Rows.Count - 1].Cells[ColTarget];
			_rules.BeginEdit(true);
		})));
		top.Controls.Add(NewButton("− Quitar parámetro", Do(RemoveSelected)));
		Button create = NewButton("Crear contenido de parámetros  →", Do(CreateContent), bold: true);
		create.BackColor = Color.FromArgb(30, 110, 170);
		create.ForeColor = Color.White;
		create.FlatStyle = FlatStyle.Flat;
		create.Margin = new Padding(24, 3, 3, 3);
		top.Controls.Add(create);

		_rules.Dock = DockStyle.Fill;
		_rules.AllowUserToAddRows = false;
		_rules.AllowUserToDeleteRows = false;
		_rules.AllowUserToResizeRows = false;
		_rules.RowHeadersVisible = false;
		_rules.BackgroundColor = Color.White;
		_rules.BorderStyle = BorderStyle.None;
		_rules.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
		_rules.GridColor = Color.FromArgb(225, 225, 225);
		_rules.SelectionMode = DataGridViewSelectionMode.CellSelect;
		_rules.EnableHeadersVisualStyles = false;
		_rules.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 244, 248);
		_rules.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
		_rules.ColumnHeadersDefaultCellStyle.Padding = new Padding(4);
		_rules.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
		_rules.RowTemplate.Height = 30;
		_rules.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
		_rules.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 235, 250);
		_rules.DefaultCellStyle.SelectionForeColor = Color.Black;
		_rules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parámetro destino", FillWeight = 26 });
		var source = new DataGridViewComboBoxColumn { HeaderText = "Parámetro enlazado", FillWeight = 20, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
		source.Items.AddRange(FillRule.Sources.Select(s => (object)s.Text).ToArray());
		_rules.Columns.Add(source);
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Contenido de parámetro", FillWeight = 26 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Prefijo", FillWeight = 8 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sufijo", FillWeight = 8 });
		_rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Sobrescribir", FillWeight = 7 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "En el modelo", FillWeight = 9, ReadOnly = true });
		_rules.CurrentCellDirtyStateChanged += (s, e) =>
		{
			if (_rules.IsCurrentCellDirty && (_rules.CurrentCell is DataGridViewComboBoxCell || _rules.CurrentCell is DataGridViewCheckBoxCell))
			{
				_rules.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_rules.CellValueChanged += (s, e) => Safe(() =>
		{
			if (!_filling && e.RowIndex >= 0)
			{
				ReadRules();
				RefreshRow(e.RowIndex);
			}
		});
		_rules.EditingControlShowing += (s, e) => Safe(() => SetupAutoComplete(e.Control as TextBox));
		_rules.DataError += (s, e) => e.ThrowException = false;

		// Barra inferior: configuración actual, cargar y guardar.
		var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 52, ColumnCount = 3, Padding = new Padding(12, 8, 12, 8), BackColor = Color.FromArgb(245, 247, 250) };
		bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		_configLabel.AutoSize = true;
		_configLabel.Anchor = AnchorStyles.Left;
		_configLabel.Font = new Font(Font, FontStyle.Bold);
		bottom.Controls.Add(_configLabel, 0, 0);
		var load = NewButton("Cargar configuración de parámetros ▾", null);
		var menu = new ContextMenuStrip();
		menu.Items.Add("Desde archivo…", null, Do(LoadFrom));
		menu.Items.Add("Preajuste SUNAT (Tabla 5)", null, Do(LoadSunat));
		load.Click += (s, e) => menu.Show(load, new Point(0, load.Height));
		bottom.Controls.Add(load, 1, 0);
		bottom.Controls.Add(NewButton("Guardar configuración", Do(Save)), 2, 0);

		page.Controls.Add(_rules);
		page.Controls.Add(bottom);
		page.Controls.Add(top);
		return page;
	}

	/// <summary>Autocompletar: nombres de parámetros, o los conjuntos de sectores si el origen es Sectorización.</summary>
	private void SetupAutoComplete(TextBox box)
	{
		if (box == null)
		{
			return;
		}

		int column = _rules.CurrentCell?.ColumnIndex ?? -1;
		int row = _rules.CurrentCell?.RowIndex ?? -1;
		box.AutoCompleteMode = AutoCompleteMode.None;
		if (column != ColTarget && column != ColDetail)
		{
			return;
		}

		var source = new AutoCompleteStringCollection();
		bool sectors = column == ColDetail && row >= 0 && row < _set.Rules.Count && _set.Rules[row].Source == SourceKind.Sector;
		source.AddRange(sectors ? _set.SectorSets.Select(s => s.Name).ToArray() : _catalog.Parameters.ToArray());
		box.AutoCompleteCustomSource = source;
		box.AutoCompleteSource = AutoCompleteSource.CustomSource;
		box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
	}

	#endregion

	#region Parámetros

	private void FillRules()
	{
		_filling = true;
		_rules.Rows.Clear();
		foreach (FillRule rule in _set.Rules)
		{
			_rules.Rows.Add(rule.Target, FillRule.SourceText(rule.Source), rule.Detail, rule.Prefix, rule.Suffix, rule.Overwrite, string.Empty);
		}

		for (int i = 0; i < _set.Rules.Count; i++)
		{
			RefreshRow(i);
		}

		_filling = false;
	}

	/// <summary>Pasa lo editado en la tabla a las reglas.</summary>
	private void ReadRules()
	{
		if (_filling)
		{
			return;
		}

		_rules.EndEdit();
		for (int i = 0; i < _rules.Rows.Count && i < _set.Rules.Count; i++)
		{
			DataGridViewRow row = _rules.Rows[i];
			FillRule rule = _set.Rules[i];
			rule.Enabled = true;
			rule.Target = (row.Cells[ColTarget].Value as string ?? string.Empty).Trim();
			rule.Source = FillRule.SourceOf(row.Cells[ColSource].Value as string);
			rule.Detail = (row.Cells[ColDetail].Value as string ?? string.Empty).Trim();
			rule.Prefix = row.Cells[ColPrefix].Value as string ?? string.Empty;
			rule.Suffix = row.Cells[ColSuffix].Value as string ?? string.Empty;
			rule.Overwrite = row.Cells[ColOverwrite].Value as bool? ?? false;
		}
	}

	private void RefreshRow(int index)
	{
		if (index < 0 || index >= _set.Rules.Count || index >= _rules.Rows.Count)
		{
			return;
		}

		bool filling = _filling;
		_filling = true;
		FillRule rule = _set.Rules[index];
		DataGridViewRow row = _rules.Rows[index];
		bool exists = _catalog.Parameters.Contains(rule.Target);
		row.Cells[ColExists].Value = rule.Target.Length == 0 ? string.Empty : _catalog.Parameters.Count == 0 ? "…" : exists ? "✓ existe" : "✗ se creará";
		row.Cells[ColExists].Style.ForeColor = exists ? Color.ForestGreen : Color.DarkOrange;

		// El contenido solo aplica a Parámetro, Valor fijo y Sectorización.
		bool needsDetail = rule.Source == SourceKind.Parametro || rule.Source == SourceKind.Fijo || rule.Source == SourceKind.Sector;
		string hint = !needsDetail ? null
			: rule.Source == SourceKind.Parametro && rule.Detail.Length == 0 ? "Escribe el parámetro de origen"
			: rule.Source == SourceKind.Fijo && rule.Detail.Length == 0 ? "Escribe el valor"
			: rule.Source == SourceKind.Sector && !_set.SectorSets.Any(s => string.Equals(s.Name, rule.Detail, StringComparison.OrdinalIgnoreCase)) ? "Nombre de un conjunto de la hoja 3 (Sectorización)"
			: null;
		DataGridViewCell detail = row.Cells[ColDetail];
		detail.ReadOnly = !needsDetail;
		detail.Style.BackColor = !needsDetail ? Color.FromArgb(248, 248, 248) : hint != null ? Color.FromArgb(255, 240, 215) : Color.White;
		detail.ToolTipText = hint ?? string.Empty;
		row.DefaultCellStyle.ForeColor = rule.Source == SourceKind.NoAplica ? Color.Gray : SystemColors.WindowText;
		_filling = filling;
	}

	private void RemoveSelected()
	{
		ReadRules();
		foreach (int index in _rules.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct().OrderByDescending(i => i))
		{
			if (index >= 0 && index < _set.Rules.Count)
			{
				_set.Rules.RemoveAt(index);
			}
		}

		FillRules();
	}

	/// <summary>Crea los parámetros que faltan (si los hay), calcula el contenido y pasa a la hoja 2 para validarlo.</summary>
	private void CreateContent()
	{
		ReadRules();
		List<FillRule> rules = _set.Rules.Where(r => r.Target.Length > 0 && r.Source != SourceKind.NoAplica).ToList();
		if (rules.Count == 0)
		{
			MessageBox.Show(this, "Añade al menos un parámetro.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		List<string> missing = _catalog.Parameters.Count == 0 ? new List<string>()
			: rules.Where(r => !_catalog.Parameters.Contains(r.Target)).Select(r => r.Target).Distinct().ToList();
		if (missing.Count > 0)
		{
			DialogResult answer = MessageBox.Show(this, "Estos parámetros no existen en el modelo:\n\n" + string.Join("\n", missing) +
					"\n\n¿Crearlos (texto, de ejemplar, en todas las categorías de modelo) antes de calcular?\n" +
					"No = calcular solo con los que existen.", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
			if (answer == DialogResult.Cancel)
			{
				return;
			}

			_handler.Names = answer == DialogResult.Yes ? missing : new List<string>();
		}
		else
		{
			_handler.Names = new List<string>();
		}

		_tabs.SelectedIndex = TabValidate;
		Raise(FillAction.CreateAndCompute, missing.Count > 0 && _handler.Names.Count > 0
			? "Creando parámetros y calculando el contenido de todos los elementos…"
			: "Calculando el contenido de todos los elementos…");
	}

	private void SetCatalog(ModelCatalog catalog, string title)
	{
		_catalog = catalog;
		_documentTitle = title;
		Text = "EMASY · Rellenar parámetros · " + title;
		SetBusy(false, $"Modelo leído: {catalog.Parameters.Count} parámetros, {catalog.Views.Count} plantas, {catalog.Dwgs.Count} DWG.");
		for (int i = 0; i < _set.Rules.Count; i++)
		{
			RefreshRow(i);
		}

		FillSectorChoices();
	}

	private void ShowConfigName() => _configLabel.Text = "Configuración actual:  " + _configName;

	private void Save()
	{
		ReadRules();
		string path = _configPath;
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			using var dialog = new SaveFileDialog { Filter = "Configuración (*.json)|*.json", FileName = _configName + ".json" };
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}

			path = dialog.FileName;
		}

		_set.Save(path);
		_configPath = path;
		_configName = Path.GetFileNameWithoutExtension(path);
		ShowConfigName();
		SaveDefault();
		_status.Text = "Configuración guardada en " + path;
	}

	private void LoadFrom()
	{
		using var dialog = new OpenFileDialog { Filter = "Configuración (*.json)|*.json" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		RuleSet loaded = TryLoad(dialog.FileName);
		if (loaded == null)
		{
			MessageBox.Show(this, "No se pudo leer el archivo de configuración.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		Simplify(loaded);
		_set = loaded;
		_configPath = dialog.FileName;
		_configName = Path.GetFileNameWithoutExtension(dialog.FileName);
		FillRules();
		FillSectorList();
		ShowConfigName();
		SaveDefault();
		_status.Text = "Configuración cargada de " + dialog.FileName;
	}

	private void LoadSunat()
	{
		if (MessageBox.Show(this, "¿Reemplazar los parámetros por los 10 de la Tabla 5 de SUNAT?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}

		RuleSet sunat = RuleSet.Sunat();
		_set.Rules = sunat.Rules;
		foreach (SectorSet sectors in sunat.SectorSets.Where(s => !_set.SectorSets.Any(o => string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase))))
		{
			_set.SectorSets.Add(sectors);
		}

		_configName = "SUNAT (Tabla 5)";
		_configPath = null;
		FillRules();
		FillSectorList();
		ShowConfigName();
		SaveDefault();
	}

	#endregion
}
