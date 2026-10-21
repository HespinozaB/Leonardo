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
/// Ventana no modal en 4 pestañas: Reglas (cómo se llena cada parámetro), Sectorización (sectores dibujados en planta o en un
/// DWG), Vista previa (valores directos e inferidos, a confirmar) y Libre (masivo o personalizado).
/// </summary>
internal sealed partial class FillForm : Form
{
	private const int ColEnabled = 0;
	private const int ColTarget = 1;
	private const int ColSource = 2;
	private const int ColDetail = 3;
	private const int ColPrefix = 4;
	private const int ColSuffix = 5;
	private const int ColMap = 6;
	private const int ColInfer = 7;
	private const int ColOverwrite = 8;
	private const int ColExists = 9;

	private readonly FillHandler _handler;
	private readonly ExternalEvent _event;
	private readonly TabControl _tabs = new TabControl();
	private readonly Label _status = new Label();
	private readonly DataGridView _rules = new DataGridView();
	private readonly Button _createMissing = new Button();

	private RuleSet _set = new RuleSet();
	private ModelCatalog _catalog = new ModelCatalog();
	private string _documentTitle = string.Empty;
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

	/// <summary>Carga las últimas reglas (o el preajuste SUNAT) y lee lo que ofrece el modelo.</summary>
	public void Start()
	{
		Safe(() =>
		{
			_set = File.Exists(RuleSet.DefaultPath) ? TryLoad(RuleSet.DefaultPath) ?? RuleSet.Sunat() : RuleSet.Sunat();
			FillRules();
			FillSectorList();
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
			_status.Text = message + "  ·  Vuelve a calcular para ver el estado nuevo.";
		}
	}

	private static Button NewButton(string text, EventHandler click, bool bold = false)
	{
		var button = new Button { Text = text, AutoSize = true };
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

	private void SaveDefault()
	{
		try
		{
			_set.Save(RuleSet.DefaultPath);
		}
		catch (Exception)
		{
			// Guardar las reglas es una comodidad: si falla se sigue sin ello.
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
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		_tabs.Dock = DockStyle.Fill;
		_tabs.TabPages.Add(BuildRulesPage());
		_tabs.TabPages.Add(BuildSectorsPage());
		_tabs.TabPages.Add(BuildPreviewPage());
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
		var page = new TabPage("1 · Reglas");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(10) };
		top.Controls.Add(NewLabel("Una fila por parámetro a llenar. Origen: un dato del elemento (categoría, tipo, marca, material, nivel o cualquier parámetro), un valor fijo, una sectorización (pestaña 2) o Libre (pestaña 4). \"Si falta…\" define cómo deducir el valor cuando no hay dato: se propone y tú lo confirmas en la vista previa.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		top.Controls.Add(NewButton("Añadir regla", Do(() =>
		{
			ReadRules();
			_set.Rules.Add(new FillRule { Source = SourceKind.Fijo });
			FillRules();
		})));
		top.Controls.Add(NewButton("Quitar regla", Do(() =>
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
		})));
		top.Controls.Add(NewButton("Preajuste SUNAT (Tabla 5)", Do(() =>
		{
			if (MessageBox.Show(this, "¿Reemplazar las reglas por las 10 de la Tabla 5 de SUNAT?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
			{
				RuleSet sunat = RuleSet.Sunat();
				_set.Rules = sunat.Rules;
				foreach (SectorSet sectors in sunat.SectorSets.Where(s => !_set.SectorSets.Any(o => string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase))))
				{
					_set.SectorSets.Add(sectors);
				}

				FillRules();
				FillSectorList();
			}
		})));
		top.Controls.Add(NewButton("Guardar…", Do(SaveAs)));
		top.Controls.Add(NewButton("Cargar…", Do(LoadFrom)));
		_createMissing.Text = "Crear parámetros que faltan";
		_createMissing.AutoSize = true;
		_createMissing.Click += Do(CreateMissing);
		top.Controls.Add(_createMissing);
		top.Controls.Add(NewButton("Calcular vista previa →", Do(() => _tabs.SelectedIndex = 2), bold: true));

		_rules.Dock = DockStyle.Fill;
		_rules.AllowUserToAddRows = false;
		_rules.AllowUserToDeleteRows = false;
		_rules.RowHeadersWidth = 30;
		_rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Usar", Width = 45 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parámetro destino", Width = 250 });
		var source = new DataGridViewComboBoxColumn { HeaderText = "Origen", Width = 190, FlatStyle = FlatStyle.Flat };
		source.Items.AddRange(FillRule.Sources.Select(s => (object)s.Text).ToArray());
		_rules.Columns.Add(source);
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detalle (parámetro / valor / sectores)", Width = 230 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Prefijo", Width = 65 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sufijo", Width = 65 });
		_rules.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Equivalencias", Width = 100 });
		_rules.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Si falta…", Width = 190 });
		_rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Sobrescribir", Width = 80 });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "En el modelo", Width = 120, ReadOnly = true });
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
		_rules.CellContentClick += (s, e) => Safe(() => OnRuleButton(e.RowIndex, e.ColumnIndex));
		_rules.EditingControlShowing += (s, e) => Safe(() => SetupAutoComplete(e.Control as TextBox));
		_rules.DataError += (s, e) => e.ThrowException = false;

		page.Controls.Add(_rules);
		page.Controls.Add(top);
		return page;
	}

	/// <summary>Autocompletar: nombres de parámetros en Destino y Detalle (o los conjuntos de sectores si el origen es Sectorización).</summary>
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

	#region Reglas

	private void FillRules()
	{
		_filling = true;
		_rules.Rows.Clear();
		foreach (FillRule rule in _set.Rules)
		{
			_rules.Rows.Add(rule.Enabled, rule.Target, FillRule.SourceText(rule.Source), rule.Detail, rule.Prefix, rule.Suffix,
				string.Empty, string.Empty, rule.Overwrite, string.Empty);
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
			rule.Enabled = row.Cells[ColEnabled].Value as bool? ?? true;
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
		row.Cells[ColMap].Value = rule.ValueMap.Count > 0 ? $"{rule.ValueMap.Count} equivalencia(s)" : "…";
		var infer = new List<string>();
		if (rule.InferSameType)
		{
			infer.Add("tipo");
		}

		if (rule.InferNearest)
		{
			infer.Add("cercano");
		}

		if (rule.InferKeywords)
		{
			infer.Add(rule.Keywords.Count > 0 ? $"{rule.Keywords.Count} palabras" : "nombre");
		}

		if (rule.Default.Length > 0)
		{
			infer.Add($"\"{rule.Default}\"");
		}

		row.Cells[ColInfer].Value = rule.Source == SourceKind.Libre || rule.Source == SourceKind.NoAplica || rule.Source == SourceKind.Fijo
			? "—"
			: infer.Count == 0 ? "no deducir" : string.Join(" · ", infer);
		bool exists = _catalog.Parameters.Contains(rule.Target);
		row.Cells[ColExists].Value = rule.Target.Length == 0 ? string.Empty : _catalog.Parameters.Count == 0 ? "…" : exists ? "✓" : "✗ no existe";
		row.Cells[ColExists].Style.ForeColor = exists ? Color.ForestGreen : Color.Firebrick;
		string detailHint = rule.Source switch
		{
			SourceKind.Parametro => rule.Detail.Length == 0 ? "← escribe el parámetro de origen" : null,
			SourceKind.Fijo => rule.Detail.Length == 0 ? "← escribe el valor" : null,
			SourceKind.Sector => _set.SectorSets.Any(s => string.Equals(s.Name, rule.Detail, StringComparison.OrdinalIgnoreCase)) ? null : "← conjunto de sectores (pestaña 2)",
			_ => null
		};
		row.Cells[ColDetail].Style.BackColor = detailHint != null ? Color.FromArgb(255, 235, 200) : SystemColors.Window;
		row.Cells[ColDetail].ToolTipText = detailHint ?? string.Empty;
		row.DefaultCellStyle.ForeColor = rule.Enabled && rule.Source != SourceKind.NoAplica ? SystemColors.WindowText : Color.Gray;
		_filling = filling;
	}

	private void OnRuleButton(int rowIndex, int column)
	{
		if (rowIndex < 0 || rowIndex >= _set.Rules.Count || (column != ColMap && column != ColInfer))
		{
			return;
		}

		ReadRules();
		FillRule rule = _set.Rules[rowIndex];
		if (column == ColMap)
		{
			using var dialog = new PairsForm($"Equivalencias · {rule.Target}", "Valor leído", "Valor a escribir", rule.ValueMap,
				"Ej.: Muros → MURO. El valor leído se compara sin tildes ni mayúsculas.");
			if (dialog.ShowDialog(this) == DialogResult.OK)
			{
				rule.ValueMap = dialog.Pairs.ToDictionary(p => Core.Text.Simplify(p.Key), p => p.Value, StringComparer.Ordinal);
			}
		}
		else if (rule.Source != SourceKind.Libre && rule.Source != SourceKind.NoAplica && rule.Source != SourceKind.Fijo)
		{
			using var dialog = new InferForm(rule);
			dialog.ShowDialog(this);
		}

		RefreshRow(rowIndex);
		SaveDefault();
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

	private void CreateMissing()
	{
		ReadRules();
		List<string> missing = _set.Rules.Where(r => r.Enabled && r.Target.Length > 0 && r.Source != SourceKind.NoAplica && !_catalog.Parameters.Contains(r.Target))
			.Select(r => r.Target).Distinct().ToList();
		if (missing.Count == 0)
		{
			MessageBox.Show(this, "Todos los parámetros destino ya existen en el modelo.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (MessageBox.Show(this, "Se crearán como parámetros compartidos de texto, de ejemplar, en todas las categorías de modelo:\n\n" +
				string.Join("\n", missing) + "\n\n¿Continuar?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			_handler.Names = missing;
			Raise(FillAction.CreateParameters, "Creando parámetros…");
		}
	}

	private void SaveAs()
	{
		ReadRules();
		using var dialog = new SaveFileDialog { Filter = "Reglas (*.json)|*.json", FileName = "Reglas rellenar.json" };
		if (dialog.ShowDialog(this) == DialogResult.OK)
		{
			_set.Save(dialog.FileName);
			_status.Text = "Reglas guardadas en " + dialog.FileName;
		}
	}

	private void LoadFrom()
	{
		using var dialog = new OpenFileDialog { Filter = "Reglas (*.json)|*.json" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		RuleSet loaded = TryLoad(dialog.FileName);
		if (loaded == null)
		{
			MessageBox.Show(this, "No se pudo leer el archivo de reglas.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		_set = loaded;
		FillRules();
		FillSectorList();
		_status.Text = "Reglas cargadas de " + dialog.FileName;
	}

	#endregion
}
