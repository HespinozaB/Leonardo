using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RevitPartidas.Commands;
using RevitPartidas.Core;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;

namespace RevitPartidas.UI;

/// <summary>
/// Ventana no modal en 3 pasos: 1 Código de montaje (presupuesto, niveles, parámetros de nivel y tabla de códigos de montaje),
/// 2 Filtros (qué niveles son fijos y cuáles salen de un parámetro, p. ej. la ETAPA, con su regla de valores) y 3 Revisión.
/// </summary>
internal sealed partial class PartidasForm : Form
{
	private const int TabMontaje = 0;
	private const int TabFilters = 1;
	private const int TabReview = 2;
	private const int TabLevels = 3;

	private const string RuleAuto = "(automático)";
	private const string RuleIgnore = "(ignorar este valor)";

	/// <summary>Opciones de origen de un nivel, en el orden en que se muestran.</summary>
	private static readonly (LinkMode Mode, string Text)[] LinkModes =
	{
		(LinkMode.Fijo, "Valor fijo"),
		(LinkMode.Parametro, "Parámetro de Revit"),
		(LinkMode.Codigo, "Múltiple (todos los parámetros)")
	};

	private static string ModeText(LinkMode mode) => LinkModes.First(m => m.Mode == mode).Text;

	private static LinkMode ModeOf(string text) => LinkModes.FirstOrDefault(m => m.Text == text).Text == null ? LinkMode.Codigo : LinkModes.First(m => m.Text == text).Mode;

	private readonly PartidasHandler _handler;
	private readonly ExternalEvent _event;
	private readonly PartidasConfig _config = PartidasConfig.Load();

	private readonly TabControl _tabs = new TabControl();
	private readonly Label _status = new Label();

	// Paso 1.
	private readonly Button _loadBudget = new Button();
	private readonly Label _budgetLabel = new Label();
	private readonly NumericUpDown _levels = new NumericUpDown();
	private readonly ListView _hierarchy = new ListView();
	private readonly TextBox _prefix = new TextBox();
	private readonly CheckBox _twoDigits = new CheckBox();
	private readonly ListView _parameters = new ListView();
	private readonly Button _createParams = new Button();
	private readonly Button _loadMontaje = new Button();
	private bool[] _parametersExist = Array.Empty<bool>();

	// Paso 2.
	private readonly DataGridView _links = new DataGridView();
	private readonly DataGridView _rules = new DataGridView();
	private readonly Button _suggest = new Button();
	private readonly Button _readValues = new Button();
	private readonly CheckBox _preferSource = new CheckBox();
	private readonly Button _analyze = new Button();
	private readonly Dictionary<int, string> _linkExamples = new Dictionary<int, string>();
	private Dictionary<int, Dictionary<string, int>> _linkValues = new Dictionary<int, Dictionary<string, int>>();

	private Budget _budget;
	private BudgetMatcher _matcher;
	private string _documentTitle = string.Empty;
	private string _scanSignature;
	private bool _busy;
	private bool _filling;

	public PartidasForm(PartidasHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_event = externalEvent;
		_handler.OnScan = (result, title, message) => OnUi(() => SetScan(result, title, message));
		_handler.OnWritten = (written, types, message) => OnUi(() => SetWritten(written, types, message));
		_handler.OnSelection = ids => OnUi(() => SetRevitSelection(ids));
		_handler.OnSuggestions = suggestions => OnUi(() => SetSuggestions(suggestions));
		_handler.OnLinkValues = values => OnUi(() => SetLinkValues(values));
		_handler.OnParameterStatus = (exist, message) => OnUi(() => SetParameterStatus(exist, message));
		_handler.OnPicked = id => OnUi(() => SetPicked(id));
		_handler.OnMessage = message => OnUi(() => Done(message));
		BuildLayout();
	}

	/// <summary>Carga el último presupuesto; si ya se configuraron los pasos, va directo a la revisión.</summary>
	public void Start()
	{
		Safe(() =>
		{
			string path = Settings.Get("budget");
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				LoadBudget(path);
			}

			if (Settings.Get("configured") == "1" && _budget != null)
			{
				_tabs.SelectedIndex = TabReview;
			}
			else
			{
				RefreshHierarchy();
			}
		});
	}

	#region Infraestructura

	/// <summary>Ejecuta en el hilo de la ventana; un error se muestra una sola vez en la barra de estado (sin ventanas de excepción).</summary>
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

	private void Raise(PartidasAction action, string busyText)
	{
		if (_busy)
		{
			_status.Text = "Espera a que termine la operación en curso…";
			return;
		}

		_handler.Action = action;
		_handler.Budget = _budget;
		_handler.Config = _config.Clone();
		_handler.DocumentTitle = _documentTitle;
		if (busyText != null)
		{
			SetBusy(true, busyText);
		}

		_event.Raise();
	}

	private void SetBusy(bool busy, string text)
	{
		_busy = busy;
		if (text != null)
		{
			_status.Text = text;
		}

		Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
		UpdateButtons();
	}

	private void Done(string message) => SetBusy(false, message);

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
		MaximumSize = wrap ? new Size(1100, 0) : Size.Empty,
		Margin = new Padding(0, 6, 6, 0)
	};

	#endregion

	#region Layout

	private void BuildLayout()
	{
		Text = "EMASY · Partidas";
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1400, 820);
		MinimumSize = new Size(950, 500);
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		_tabs.Dock = DockStyle.Fill;
		_tabs.TabPages.Add(BuildMontajePage());
		_tabs.TabPages.Add(BuildFiltersPage());
		_tabs.TabPages.Add(BuildReviewPage());
		_tabs.TabPages.Add(BuildLevelsPage());
		_tabs.SelectedIndexChanged += (s, e) => Safe(OnTabChanged);

		_status.Dock = DockStyle.Bottom;
		_status.Height = 24;
		_status.Padding = new Padding(8, 5, 0, 0);
		_status.BackColor = SystemColors.ControlLight;

		Controls.Add(_tabs);
		Controls.Add(_status);
		FormClosed += (s, e) =>
		{
			if (_resolveForm != null && !_resolveForm.IsDisposed)
			{
				_resolveForm.Close();
			}
		};
	}

	private TabPage BuildMontajePage()
	{
		var page = new TabPage("1 · Código de montaje");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 140, Padding = new Padding(10) };
		top.Controls.Add(NewLabel("Carga el presupuesto: se lee su estructura (niveles, títulos y partidas) y se genera con ella la tabla de Código de montaje y Descripción de montaje del modelo. Así, en las propiedades de tipo, el Código de montaje ofrece las partidas del presupuesto con su descripción.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);

		_loadBudget.Text = "Cargar presupuesto…";
		_loadBudget.AutoSize = true;
		_loadBudget.Click += Do(ChooseBudget);
		top.Controls.Add(_loadBudget);
		_budgetLabel.AutoSize = true;
		_budgetLabel.Margin = new Padding(6, 7, 0, 0);
		_budgetLabel.ForeColor = Color.DimGray;
		_budgetLabel.Text = "(sin presupuesto)";
		top.Controls.Add(_budgetLabel);
		top.SetFlowBreak(_budgetLabel, true);

		top.Controls.Add(NewLabel("Niveles (órdenes) de la estructura:"));
		_levels.Minimum = 1;
		_levels.Maximum = PartidaCode.MaxLevels;
		_levels.Value = Math.Min(PartidaCode.MaxLevels, Math.Max(1, _config.Levels));
		_levels.Width = 55;
		_levels.ValueChanged += (s, e) => Safe(() =>
		{
			_config.Levels = (int)_levels.Value;
			_config.Save();
			_parametersExist = Array.Empty<bool>();
			RefreshHierarchy();
			RefreshParameterNames();
			RefreshLinks();
		});
		top.Controls.Add(_levels);
		_loadMontaje.Text = "Generar y cargar Código de montaje y Descripción de montaje";
		_loadMontaje.AutoSize = true;
		_loadMontaje.Font = new Font(Font, FontStyle.Bold);
		_loadMontaje.Margin = new Padding(16, 3, 3, 3);
		_loadMontaje.Click += Do(LoadMontaje);
		top.Controls.Add(_loadMontaje);
		top.Controls.Add(NewButton("Siguiente →", Do(() => _tabs.SelectedIndex = TabFilters), bold: true));

		_hierarchy.View = View.Details;
		_hierarchy.FullRowSelect = true;
		_hierarchy.GridLines = true;
		_hierarchy.Dock = DockStyle.Fill;
		_hierarchy.Columns.Add("Nivel", 60);
		_hierarchy.Columns.Add("Ejemplo de código", 170);
		_hierarchy.Columns.Add("Descripción en el presupuesto", 560);
		_hierarchy.Columns.Add("Códigos de este nivel", 160);

		page.Controls.Add(_hierarchy);
		page.Controls.Add(top);
		return page;
	}

	/// <summary>Paso 4 (opcional): los parámetros de nivel (p. ej. DYCUSA_PARTIDA 1…6) y su escritura.</summary>
	private TabPage BuildLevelsPage()
	{
		var page = new TabPage("4 · Parámetros de nivel (opcional)");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(10) };
		top.Controls.Add(NewLabel("Opcional: guarda la partida de cada elemento en un parámetro por nivel (p. ej. DYCUSA_PARTIDA 1 = 03, 2 = 03.01 … 6 = 03.01.04.02.01.01), para tablas y filtros. A diferencia del Código de montaje (de tipo), son de ejemplar: cada elemento lleva la partida de su etapa.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		top.Controls.Add(NewLabel("Prefijo:"));
		_prefix.Width = 190;
		_prefix.Text = _config.Prefix;
		_prefix.TextChanged += (s, e) => Safe(() =>
		{
			_config.Prefix = _prefix.Text;
			_config.Save();
			_parametersExist = Array.Empty<bool>();
			RefreshParameterNames();
			RefreshLinks();
		});
		top.Controls.Add(_prefix);
		_twoDigits.Text = "número con dos cifras (01, 02…)";
		_twoDigits.AutoSize = true;
		_twoDigits.Margin = new Padding(8, 5, 0, 0);
		_twoDigits.Checked = _config.TwoDigits;
		_twoDigits.CheckedChanged += (s, e) => Safe(() =>
		{
			_config.TwoDigits = _twoDigits.Checked;
			_config.Save();
			_parametersExist = Array.Empty<bool>();
			RefreshParameterNames();
			RefreshLinks();
		});
		top.Controls.Add(_twoDigits);
		top.SetFlowBreak(_twoDigits, true);
		_createParams.Text = "Crear / actualizar parámetros en el modelo";
		_createParams.AutoSize = true;
		_createParams.Click += Do(() => Raise(PartidasAction.CreateParameters, "Creando parámetros…"));
		top.Controls.Add(_createParams);
		Setup(_writePending, "Escribir niveles", WritePending, bold: true);
		top.Controls.Add(_writePending);
		Setup(_clear, "Vaciar niveles de lo marcado en la revisión", ClearTargets);
		_clear.ForeColor = Color.Firebrick;
		top.Controls.Add(_clear);

		_parameters.View = View.Details;
		_parameters.FullRowSelect = true;
		_parameters.GridLines = true;
		_parameters.Dock = DockStyle.Fill;
		_parameters.Columns.Add("Nivel", 60);
		_parameters.Columns.Add("Parámetro", 260);
		_parameters.Columns.Add("En el modelo", 180);

		page.Controls.Add(_parameters);
		page.Controls.Add(top);
		return page;
	}

	private TabPage BuildFiltersPage()
	{
		var page = new TabPage("2 · Filtros");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10) };
		top.Controls.Add(NewLabel("Indica qué niveles son fijos y cuáles salen de un parámetro de Revit. Ejemplo: nivel 1 = valor fijo 03 (todo es ARQUITECTURA); nivel 2 = parámetro de etapa, con la regla \"ETAPA 1\" → 03.01 y \"ETAPA 2\" → 03.02. El filtro decide la rama: un código de montaje de la otra etapa se cambia por su equivalente con la misma descripción, y si no lo hay se avisa.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		_suggest.Text = "Detectar filtros automáticamente";
		_suggest.AutoSize = true;
		_suggest.Click += Do(() => Raise(PartidasAction.SuggestLinks, "Buscando parámetros que coincidan con el presupuesto…"));
		top.Controls.Add(_suggest);
		_readValues.Text = "Leer valores de los parámetros del filtro";
		_readValues.AutoSize = true;
		_readValues.Click += Do(() => Raise(PartidasAction.LinkValues, "Leyendo los valores del modelo…"));
		top.Controls.Add(_readValues);

		_links.Dock = DockStyle.Fill;
		_links.AllowUserToAddRows = false;
		_links.AllowUserToDeleteRows = false;
		_links.RowHeadersVisible = false;
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nivel", Width = 50, ReadOnly = true });
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parámetro destino", Width = 190, ReadOnly = true });
		var mode = new DataGridViewComboBoxColumn { HeaderText = "Origen", Width = 170, FlatStyle = FlatStyle.Flat };
		mode.Items.AddRange(LinkModes.Select(m => (object)m.Text).ToArray());
		_links.Columns.Add(mode);
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Código fijo / parámetro de regla", Width = 260 });
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Comprobación", Width = 560, ReadOnly = true });
		_links.CurrentCellDirtyStateChanged += (s, e) =>
		{
			if (_links.IsCurrentCellDirty && _links.CurrentCell is DataGridViewComboBoxCell)
			{
				_links.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_links.CellValueChanged += (s, e) => Safe(() => OnLinkEdited(e.RowIndex));
		_links.DataError += (s, e) => e.ThrowException = false;

		_rules.Dock = DockStyle.Fill;
		_rules.AllowUserToAddRows = false;
		_rules.AllowUserToDeleteRows = false;
		_rules.RowHeadersVisible = false;
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nivel", Width = 50, ReadOnly = true });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parámetro", Width = 190, ReadOnly = true });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Valor en el modelo", Width = 230, ReadOnly = true });
		_rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Elementos", Width = 80, ReadOnly = true });
		_rules.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Regla: código del nivel", Width = 420, FlatStyle = FlatStyle.Flat });
		_rules.CurrentCellDirtyStateChanged += (s, e) =>
		{
			if (_rules.IsCurrentCellDirty && _rules.CurrentCell is DataGridViewComboBoxCell)
			{
				_rules.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_rules.CellValueChanged += (s, e) => Safe(() => OnRuleEdited(e.RowIndex));
		_rules.DataError += (s, e) => e.ThrowException = false;

		var grids = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
		grids.Panel1.Controls.Add(_links);
		var rulesPanel = new Panel { Dock = DockStyle.Fill };
		rulesPanel.Controls.Add(_rules);
		rulesPanel.Controls.Add(new Label
		{
			Dock = DockStyle.Top,
			Height = 22,
			Padding = new Padding(4, 4, 0, 0),
			Text = "Regla de los parámetros del filtro: qué código corresponde a cada valor (pulsa \"Leer valores\" para verlos).",
			ForeColor = Color.DimGray
		});
		grids.Panel2.Controls.Add(rulesPanel);

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(10, 6, 10, 0), WrapContents = false };
		_preferSource.Text = "El código de montaje manda sobre lo ya escrito en los parámetros de nivel";
		_preferSource.AutoSize = true;
		_preferSource.Margin = new Padding(0, 5, 12, 0);
		_preferSource.Checked = _config.PreferSource;
		_preferSource.CheckedChanged += (s, e) => Safe(() =>
		{
			_config.PreferSource = _preferSource.Checked;
			_config.Save();
			ReevaluateAll();
		});
		bottom.Controls.Add(_preferSource);
		_analyze.Text = "Analizar modelo →";
		_analyze.AutoSize = true;
		_analyze.Font = new Font(Font, FontStyle.Bold);
		_analyze.Click += Do(() =>
		{
			Settings.Set("configured", "1");
			_scanSignature = null;
			_tabs.SelectedIndex = TabReview;
		});
		bottom.Controls.Add(_analyze);

		page.Controls.Add(grids);
		page.Controls.Add(bottom);
		page.Controls.Add(top);
		page.Layout += (s, e) =>
		{
			if (grids.Height > 200)
			{
				Safe(() => grids.SplitterDistance = Math.Max(120, grids.Height / 2 - 40));
			}
		};
		return page;
	}

	#endregion

	#region Pasos 1 y 2

	private void OnTabChanged()
	{
		switch (_tabs.SelectedIndex)
		{
			case TabMontaje:
				RefreshHierarchy();
				break;
			case TabLevels:
				RefreshParameterNames();
				if (!_busy)
				{
					Raise(PartidasAction.ParameterStatus, null);
				}

				break;
			case TabFilters:
				RefreshLinks();
				RefreshRules();
				break;
			case TabReview:
				if (_budget == null)
				{
					_status.Text = "Carga el presupuesto en el paso 1 para revisar contra él.";
				}

				if (_scanSignature != Signature())
				{
					RequestScan();
				}

				break;
		}
	}

	/// <summary>Lo que obliga a volver a analizar si cambia (niveles, nombres, filtros, reglas, presupuesto).</summary>
	private string Signature() =>
		string.Join("|", _config.Levels, _config.Prefix, _config.TwoDigits, _budget?.SourcePath,
			string.Join(";", Enumerable.Range(0, _config.Levels).Select(i =>
			{
				LevelLink link = _config.Link(i);
				return $"{link.Mode}:{link.FixedCode}:{link.ParameterName}:{string.Join(",", link.ValueMap.Select(p => p.Key + "=" + p.Value))}";
			})));

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
			LoadBudget(dialog.FileName);
			if (_budget != null)
			{
				_levels.Value = Math.Min(PartidaCode.MaxLevels, Math.Max(1, _budget.All.Max(i => i.Depth)));
			}
		}
	}

	private void LoadBudget(string path)
	{
		try
		{
			_budget = BudgetReader.Read(path);
			_matcher = new BudgetMatcher(_budget);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "No se pudo leer el presupuesto:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		Settings.Set("budget", path);
		_budgetLabel.Text = $"{Path.GetFileName(path)} · {_budget.All.Count(i => i.IsLeaf)} partidas · hasta {_budget.All.Max(i => i.Depth)} niveles";
		RefreshHierarchy();
		BuildBudgetTree();
	}

	private void LoadMontaje()
	{
		if (_budget == null)
		{
			MessageBox.Show(this, "Primero carga el presupuesto.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (MessageBox.Show(this, $"Se generará la tabla de Código de montaje y Descripción de montaje con los {_budget.ByCode.Count} códigos " +
				$"del presupuesto (hasta {_config.Levels} niveles) y se cargará en el modelo, reemplazando la tabla actual (p. ej. Uniformat).\n\n" +
				"Después se comprueba que cada código quedó dentro de su rama.\nLos códigos de montaje ya escritos en los tipos no se borran.\n¿Continuar?", Text, MessageBoxButtons.OKCancel,
				MessageBoxIcon.Question) == DialogResult.OK)
		{
			Raise(PartidasAction.LoadAssemblyCodes, "Generando y cargando los códigos de montaje…");
		}
	}

	private void RefreshHierarchy()
	{
		_hierarchy.BeginUpdate();
		_hierarchy.Items.Clear();
		BudgetItem example = _budget?.All.Where(i => i.IsLeaf).OrderByDescending(i => i.Depth).ThenBy(i => i.Code, StringComparer.Ordinal).FirstOrDefault();
		string[] levels = PartidaCode.Levels(example?.Code, _config.Levels);
		for (int i = 0; i < _config.Levels; i++)
		{
			BudgetItem item = _budget?.Find(levels[i]);
			int count = _budget?.All.Count(b => b.Depth == i + 1) ?? 0;
			_hierarchy.Items.Add(new ListViewItem(new[]
			{
				(i + 1).ToString(), levels[i].Length > 0 ? levels[i] : "—", item?.Description ?? string.Empty,
				_budget == null ? string.Empty : count.ToString()
			}));
		}

		_hierarchy.EndUpdate();
	}

	private void RefreshParameterNames()
	{
		_parameters.BeginUpdate();
		_parameters.Items.Clear();
		string[] names = _config.Names;
		for (int i = 0; i < names.Length; i++)
		{
			string exists = i < _parametersExist.Length ? (_parametersExist[i] ? "Sí" : "No (falta crearlo)") : "…";
			var item = new ListViewItem(new[] { (i + 1).ToString(), names[i], exists });
			if (i < _parametersExist.Length)
			{
				item.ForeColor = _parametersExist[i] ? Color.ForestGreen : Color.Firebrick;
			}

			_parameters.Items.Add(item);
		}

		_parameters.EndUpdate();
	}

	private void SetParameterStatus(bool[] exist, string message)
	{
		Done(message);
		bool wereMissing = _parametersExist.Length == 0 || _parametersExist.Any(e => !e);
		_parametersExist = exist;
		RefreshParameterNames();
		if (message != null && wereMissing && exist.All(e => e) && _rows.Count > 0)
		{
			// Recién creados: los elementos ya tienen los parámetros (vacíos); no hace falta volver a analizar.
			foreach (ElementRow row in _rows.Where(r => !r.HasDycusaParameters))
			{
				row.HasDycusaParameters = true;
				row.Current = row.Current.Select(v => v ?? string.Empty).ToArray();
			}

			ReevaluateAll();
		}
	}

	private void RefreshLinks()
	{
		_filling = true;
		_links.Rows.Clear();
		string[] names = _config.Names;
		for (int i = 0; i < _config.Levels; i++)
		{
			LevelLink link = _config.Link(i);
			string value = link.Mode == LinkMode.Fijo ? link.FixedCode : link.Mode == LinkMode.Parametro ? link.ParameterName : string.Empty;
			_links.Rows.Add((i + 1).ToString(), names[i], ModeText(link.Mode), value, LinkCheck(i, link));
		}

		_filling = false;
	}

	private string LinkCheck(int index, LevelLink link)
	{
		switch (link.Mode)
		{
			case LinkMode.Fijo:
			{
				string code = PartidaCode.Normalize(link.FixedCode);
				BudgetItem item = _budget?.Find(code);
				return code == null ? "Escribe un código (p. ej. 03)."
					: PartidaCode.Depth(code) != index + 1 ? $"El código {code} no es del nivel {index + 1}."
					: item != null ? $"✓ {code} {item.Description} para todos los elementos"
					: _budget == null ? code : $"El código {code} no está en el presupuesto.";
			}
			case LinkMode.Parametro:
				if (string.IsNullOrWhiteSpace(link.ParameterName))
				{
					return "Escribe el nombre del parámetro de regla (o pulsa Detectar).";
				}

				return _linkExamples.TryGetValue(index + 1, out string example) ? "✓ " + example : "Pulsa \"Leer valores\" para ver y ajustar su regla.";
			default:
				return "Múltiple: sale del código de montaje y, si falta, de la revisión de todos los parámetros y nombres del elemento.";
		}
	}

	private void OnLinkEdited(int rowIndex)
	{
		if (_filling || rowIndex < 0 || rowIndex >= _config.Levels)
		{
			return;
		}

		DataGridViewRow row = _links.Rows[rowIndex];
		LevelLink link = _config.Link(rowIndex);
		link.Mode = ModeOf(row.Cells[2].Value as string);
		string value = (row.Cells[3].Value as string ?? string.Empty).Trim();
		if (link.Mode == LinkMode.Fijo)
		{
			link.FixedCode = value;
		}
		else if (link.Mode == LinkMode.Parametro)
		{
			if (link.ParameterName != value)
			{
				link.ValueMap.Clear();
				_linkValues.Remove(rowIndex + 1);
			}

			link.ParameterName = value;
		}

		_config.Save();
		_filling = true;
		row.Cells[4].Value = LinkCheck(rowIndex, link);
		_filling = false;
		RefreshRules();
	}

	private void SetSuggestions(List<LinkSuggestion> suggestions)
	{
		Done(suggestions.Count == 0 ? "No se encontraron parámetros cuyo valor coincida con las descripciones del presupuesto." : null);
		if (suggestions.Count == 0)
		{
			return;
		}

		string text = string.Join("\n", suggestions.Select(s => s.Mode == LinkMode.Fijo
			? $"Nivel {s.Level}: valor fijo {s.FixedCode} ({s.Example}) para todos los elementos"
			: $"Nivel {s.Level}: parámetro \"{s.ParameterName}\" ({s.Matches} elementos; p. ej. {s.Example})"));
		if (MessageBox.Show(this, "Se encontraron estos filtros:\n\n" + text + "\n\n¿Aplicarlos?", Text, MessageBoxButtons.YesNo,
				MessageBoxIcon.Question) != DialogResult.Yes)
		{
			_status.Text = "Filtros sugeridos sin aplicar.";
			return;
		}

		foreach (LinkSuggestion suggestion in suggestions.Where(s => s.Level <= _config.Levels))
		{
			LevelLink link = _config.Link(suggestion.Level - 1);
			link.Mode = suggestion.Mode;
			if (suggestion.Mode == LinkMode.Fijo)
			{
				link.FixedCode = suggestion.FixedCode;
			}
			else
			{
				link.ParameterName = suggestion.ParameterName;
				_linkExamples[suggestion.Level] = $"{suggestion.Matches} valores coinciden; p. ej. {suggestion.Example}";
			}
		}

		_config.Save();
		RefreshLinks();
		Raise(PartidasAction.LinkValues, "Leyendo los valores del modelo…");
	}

	private void SetLinkValues(Dictionary<int, Dictionary<string, int>> values)
	{
		_linkValues = values;
		Done(values.Count == 0 ? "No hay niveles con \"Parámetro de Revit\" como origen." : $"Valores leídos: {values.Sum(v => v.Value.Count)}.");
		RefreshRules();
	}

	/// <summary>Una fila por valor del parámetro de regla: el código que le toca (automático por descripción, elegido o ignorado).</summary>
	private void RefreshRules()
	{
		_filling = true;
		_rules.Rows.Clear();
		for (int i = 0; i < _config.Levels; i++)
		{
			LevelLink link = _config.Link(i);
			if (link.Mode != LinkMode.Parametro || string.IsNullOrEmpty(link.ParameterName))
			{
				continue;
			}

			var values = _linkValues.TryGetValue(i + 1, out Dictionary<string, int> found) ? new Dictionary<string, int>(found) : new Dictionary<string, int>();
			foreach (string ruled in link.ValueMap.Keys.Where(k => !values.Keys.Any(v => BudgetReader.Simplify(v) == k)))
			{
				values[ruled] = 0;
			}

			Dictionary<string, string> auto = LevelMap(i + 1);
			var options = new List<string> { RuleAuto, RuleIgnore };
			options.AddRange(_budget?.All.Where(b => b.Depth == i + 1).OrderBy(b => b.Code, StringComparer.Ordinal).Select(b => $"{b.Code}  {b.Description}") ?? Enumerable.Empty<string>());
			foreach (KeyValuePair<string, int> value in values.OrderByDescending(v => v.Value))
			{
				string key = BudgetReader.Simplify(value.Key);
				string choice = RuleAuto;
				if (link.ValueMap.TryGetValue(key, out string code))
				{
					choice = code.Length == 0 ? RuleIgnore : options.FirstOrDefault(o => o.StartsWith(code + " ", StringComparison.Ordinal)) ?? RuleAuto;
				}

				int index = _rules.Rows.Add((i + 1).ToString(), link.ParameterName, value.Key, value.Value == 0 ? "—" : value.Value.ToString());
				var cell = (DataGridViewComboBoxCell)_rules.Rows[index].Cells[4];
				cell.Items.AddRange(options.ToArray());
				string autoCode = ElementScanner.MapValue(new LevelLink(), auto, value.Key, out _);
				cell.Items[0] = autoCode == null ? RuleAuto + ": sin coincidencia" : $"{RuleAuto}: {autoCode} {_budget?.Find(autoCode)?.Description}";
				cell.Value = choice == RuleAuto ? cell.Items[0] : choice;
				_rules.Rows[index].Tag = (i, key);
				if (choice == RuleAuto && autoCode == null)
				{
					_rules.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(255, 225, 190);
				}
			}
		}

		_filling = false;
	}

	/// <summary>Descripción simplificada (y código) → código, para un nivel del presupuesto.</summary>
	private Dictionary<string, string> LevelMap(int level)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (BudgetItem item in _budget?.All.Where(b => b.Depth == level) ?? Enumerable.Empty<BudgetItem>())
		{
			map[item.Code] = item.Code;
			string description = BudgetReader.Simplify(item.Description);
			map[description] = map.TryGetValue(description, out string other) && other != item.Code ? null : item.Code;
		}

		return map;
	}

	private void OnRuleEdited(int rowIndex)
	{
		if (_filling || rowIndex < 0 || !(_rules.Rows[rowIndex].Tag is ValueTuple<int, string> tag))
		{
			return;
		}

		LevelLink link = _config.Link(tag.Item1);
		string choice = _rules.Rows[rowIndex].Cells[4].Value as string ?? RuleAuto;
		if (choice.StartsWith(RuleAuto, StringComparison.Ordinal))
		{
			link.ValueMap.Remove(tag.Item2);
		}
		else if (choice == RuleIgnore)
		{
			link.ValueMap[tag.Item2] = string.Empty;
		}
		else
		{
			link.ValueMap[tag.Item2] = choice.Split(' ')[0];
		}

		_config.Save();
		_rules.Rows[rowIndex].DefaultCellStyle.BackColor = SystemColors.Window;
		_status.Text = "Regla guardada. Se aplicará al analizar.";
	}

	#endregion
}
