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
/// Ventana no modal en 4 pasos: 1 Jerarquía (presupuesto y número de niveles), 2 Parámetros (nombres y creación),
/// 3 Enlaces (niveles que salen de un dato fijo o de un parámetro de Revit, p. ej. la ETAPA) y 4 Revisión.
/// </summary>
internal sealed partial class PartidasForm : Form
{
	private const int TabHierarchy = 0;
	private const int TabParameters = 1;
	private const int TabLinks = 2;
	private const int TabReview = 3;

	private static readonly string[] LinkModeText = { "Del código de partida", "Valor fijo", "Parámetro de Revit" };

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

	// Paso 2.
	private readonly TextBox _prefix = new TextBox();
	private readonly CheckBox _twoDigits = new CheckBox();
	private readonly ListView _parameters = new ListView();
	private readonly Button _createParams = new Button();
	private bool[] _parametersExist = Array.Empty<bool>();

	// Paso 3.
	private readonly DataGridView _links = new DataGridView();
	private readonly Button _suggest = new Button();
	private readonly TextBox _keywords = new TextBox();
	private readonly CheckBox _preferSource = new CheckBox();
	private readonly Button _analyze = new Button();
	private readonly Dictionary<int, string> _linkExamples = new Dictionary<int, string>();

	private Budget _budget;
	private string _documentTitle = string.Empty;
	private string _scanSignature;
	private bool _busy;
	private bool _filling;

	public PartidasForm(PartidasHandler handler, ExternalEvent externalEvent)
	{
		_handler = handler;
		_event = externalEvent;
		_handler.OnScan = (result, title, message) => OnUi(() => SetScan(result, title, message));
		_handler.OnWritten = (written, message) => OnUi(() => SetWritten(written, message));
		_handler.OnSelection = ids => OnUi(() => SetRevitSelection(ids));
		_handler.OnSuggestions = suggestions => OnUi(() => SetSuggestions(suggestions));
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
		MaximumSize = wrap ? new Size(900, 0) : Size.Empty,
		Margin = new Padding(0, 6, 6, 0)
	};

	#endregion

	#region Layout

	private void BuildLayout()
	{
		Text = "EMASY · Partidas";
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(1400, 800);
		MinimumSize = new Size(950, 500);
		Font = new Font("Segoe UI", 9f);
		ShowInTaskbar = false;

		_tabs.Dock = DockStyle.Fill;
		_tabs.TabPages.Add(BuildHierarchyPage());
		_tabs.TabPages.Add(BuildParametersPage());
		_tabs.TabPages.Add(BuildLinksPage());
		_tabs.TabPages.Add(BuildReviewPage());
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

	private TabPage BuildHierarchyPage()
	{
		var page = new TabPage("1 · Jerarquía");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 140, Padding = new Padding(10), FlowDirection = FlowDirection.LeftToRight };
		top.Controls.Add(NewLabel("Elige el presupuesto y cuántos niveles (órdenes) tiene su codificación. Ejemplo con 6 niveles: 03 / 03.01 / 03.01.04 / 03.01.04.02 / 03.01.04.02.01 / 03.01.04.02.01.01.", wrap: true));
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
		top.Controls.Add(NewLabel("Número de niveles:"));
		_levels.Minimum = 1;
		_levels.Maximum = PartidaCode.MaxLevels;
		_levels.Value = _config.Levels;
		_levels.Width = 60;
		_levels.ValueChanged += (s, e) => Safe(() =>
		{
			_config.Levels = (int)_levels.Value;
			_config.Save();
			RefreshHierarchy();
			RefreshParameterNames();
			RefreshLinks();
		});
		top.Controls.Add(_levels);
		top.Controls.Add(NewButton("Detectar del presupuesto", Do(() =>
		{
			if (_budget != null)
			{
				_levels.Value = Math.Min(PartidaCode.MaxLevels, Math.Max(1, _budget.All.Max(i => i.Depth)));
			}
		})));
		top.Controls.Add(NewButton("Siguiente →", Do(() => _tabs.SelectedIndex = TabParameters), bold: true));

		_hierarchy.Dock = DockStyle.Fill;
		_hierarchy.View = View.Details;
		_hierarchy.FullRowSelect = true;
		_hierarchy.GridLines = true;
		_hierarchy.Columns.Add("Nivel", 60);
		_hierarchy.Columns.Add("Ejemplo de código", 170);
		_hierarchy.Columns.Add("Descripción en el presupuesto", 560);
		_hierarchy.Columns.Add("Códigos de este nivel en el presupuesto", 230);

		page.Controls.Add(_hierarchy);
		page.Controls.Add(top);
		return page;
	}

	private TabPage BuildParametersPage()
	{
		var page = new TabPage("2 · Parámetros");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(10) };
		top.Controls.Add(NewLabel("Nombre de los parámetros: el prefijo más el número de nivel (p. ej. \"DYCUSA_PARTIDA \" → DYCUSA_PARTIDA 1 … 6, o \"DYCUSA_\" con dos cifras → DYCUSA_01 … 06). Son compartidos, de texto y de ejemplar, en Datos de identidad, en todas las categorías de modelo.", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		top.Controls.Add(NewLabel("Prefijo:"));
		_prefix.Width = 220;
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
		_twoDigits.Text = "Número con dos cifras (01, 02…)";
		_twoDigits.AutoSize = true;
		_twoDigits.Margin = new Padding(12, 5, 0, 0);
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
		_createParams.Font = new Font(Font, FontStyle.Bold);
		_createParams.Click += Do(() => Raise(PartidasAction.CreateParameters, "Creando parámetros…"));
		top.Controls.Add(_createParams);
		top.Controls.Add(NewButton("Siguiente →", Do(() => _tabs.SelectedIndex = TabLinks), bold: true));

		_parameters.Dock = DockStyle.Fill;
		_parameters.View = View.Details;
		_parameters.FullRowSelect = true;
		_parameters.GridLines = true;
		_parameters.Columns.Add("Nivel", 60);
		_parameters.Columns.Add("Parámetro", 260);
		_parameters.Columns.Add("En el modelo", 160);
		_parameters.Columns.Add("Ejemplo de valor", 200);

		page.Controls.Add(_parameters);
		page.Controls.Add(top);
		return page;
	}

	private TabPage BuildLinksPage()
	{
		var page = new TabPage("3 · Enlaces");
		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10) };
		top.Controls.Add(NewLabel("Cada nivel sale del código de partida, de un valor fijo (p. ej. nivel 1 = 03 ARQUITECTURA para todo) o de un parámetro de Revit cuyo valor es la descripción del nivel (p. ej. PR_Elemento_Etapa = \"ETAPA 1\" → 03.01). Los niveles enlazados ordenan la información y descartan los códigos que no corresponden (p. ej. un código de ETAPA 2 en un elemento de ETAPA 1).", wrap: true));
		top.SetFlowBreak(top.Controls[0], true);
		_suggest.Text = "Detectar enlaces automáticamente";
		_suggest.AutoSize = true;
		_suggest.Click += Do(() => Raise(PartidasAction.SuggestLinks, "Buscando parámetros que coincidan con el presupuesto…"));
		top.Controls.Add(_suggest);

		_links.Dock = DockStyle.Fill;
		_links.AllowUserToAddRows = false;
		_links.AllowUserToDeleteRows = false;
		_links.RowHeadersVisible = false;
		_links.SelectionMode = DataGridViewSelectionMode.CellSelect;
		_links.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nivel", Width = 50, ReadOnly = true });
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parámetro destino", Width = 200, ReadOnly = true });
		var mode = new DataGridViewComboBoxColumn { HeaderText = "Origen", Width = 180, FlatStyle = FlatStyle.Flat };
		mode.Items.AddRange(LinkModeText);
		_links.Columns.Add(mode);
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Código fijo / nombre del parámetro de Revit", Width = 300 });
		_links.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Comprobación", Width = 480, ReadOnly = true });
		_links.CurrentCellDirtyStateChanged += (s, e) =>
		{
			if (_links.IsCurrentCellDirty && _links.CurrentCell is DataGridViewComboBoxCell)
			{
				_links.CommitEdit(DataGridViewDataErrorContexts.Commit);
			}
		};
		_links.CellValueChanged += (s, e) => Safe(() => OnLinkEdited(e.RowIndex));
		_links.DataError += (s, e) => e.ThrowException = false;

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(10, 6, 10, 0) };
		bottom.Controls.Add(NewLabel("Parámetros donde ya hay códigos de partida (el nombre contiene):"));
		_keywords.Width = 180;
		_keywords.Text = _config.Keywords;
		_keywords.TextChanged += (s, e) =>
		{
			_config.Keywords = _keywords.Text;
			_config.Save();
		};
		bottom.Controls.Add(_keywords);
		_preferSource.Text = "Los parámetros de origen mandan sobre lo ya escrito";
		_preferSource.AutoSize = true;
		_preferSource.Margin = new Padding(12, 5, 0, 0);
		_preferSource.Checked = _config.PreferSource;
		_preferSource.CheckedChanged += (s, e) => Safe(() =>
		{
			_config.PreferSource = _preferSource.Checked;
			_config.Save();
			ReevaluateAll();
		});
		bottom.Controls.Add(_preferSource);
		bottom.SetFlowBreak(_preferSource, true);
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

		page.Controls.Add(_links);
		page.Controls.Add(bottom);
		page.Controls.Add(top);
		return page;
	}

	#endregion

	#region Pasos 1 a 3

	private void OnTabChanged()
	{
		switch (_tabs.SelectedIndex)
		{
			case TabHierarchy:
				RefreshHierarchy();
				break;
			case TabParameters:
				RefreshParameterNames();
				if (!_busy)
				{
					Raise(PartidasAction.ParameterStatus, null);
				}

				break;
			case TabLinks:
				RefreshLinks();
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

	/// <summary>Lo que obliga a volver a analizar si cambia (niveles, nombres, enlaces, palabras clave, presupuesto).</summary>
	private string Signature() =>
		string.Join("|", _config.Levels, _config.Prefix, _config.TwoDigits, _config.Keywords, _budget?.SourcePath,
			string.Join(";", Enumerable.Range(0, _config.Levels).Select(i => $"{_config.Link(i).Mode}:{_config.Link(i).FixedCode}:{_config.Link(i).ParameterName}")));

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
		if (_budget != null && example != null && example.Depth > _config.Levels)
		{
			_status.Text = $"Atención: el presupuesto tiene códigos de {example.Depth} niveles y elegiste {_config.Levels}; los niveles más profundos no se guardarán.";
		}
	}

	private void RefreshParameterNames()
	{
		_parameters.BeginUpdate();
		_parameters.Items.Clear();
		string[] names = _config.Names;
		BudgetItem example = _budget?.All.Where(i => i.IsLeaf).OrderByDescending(i => i.Depth).FirstOrDefault();
		string[] levels = PartidaCode.Levels(example?.Code, _config.Levels);
		for (int i = 0; i < names.Length; i++)
		{
			string exists = i < _parametersExist.Length ? (_parametersExist[i] ? "Sí" : "No (falta crearlo)") : "…";
			var item = new ListViewItem(new[] { (i + 1).ToString(), names[i], exists, levels[i] });
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
		if (message != null && wereMissing && exist.All(e => e))
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
			_links.Rows.Add((i + 1).ToString(), names[i], LinkModeText[(int)link.Mode], value, LinkCheck(i, link));
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
					: item != null ? $"✓ {code} {item.Description} (para todos los elementos)"
					: _budget == null ? code : $"El código {code} no está en el presupuesto.";
			}
			case LinkMode.Parametro:
				if (string.IsNullOrWhiteSpace(link.ParameterName))
				{
					return "Escribe el nombre del parámetro de Revit (o pulsa Detectar).";
				}

				return _linkExamples.TryGetValue(index + 1, out string example) ? "✓ " + example : "Se comprobará al analizar.";
			default:
				return "Se toma del código de partida de cada elemento.";
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
		link.Mode = (LinkMode)Math.Max(0, Array.IndexOf(LinkModeText, row.Cells[2].Value as string));
		string value = (row.Cells[3].Value as string ?? string.Empty).Trim();
		if (link.Mode == LinkMode.Fijo)
		{
			link.FixedCode = value;
		}
		else if (link.Mode == LinkMode.Parametro)
		{
			link.ParameterName = value;
		}

		_config.Save();
		_filling = true;
		row.Cells[4].Value = LinkCheck(rowIndex, link);
		_filling = false;
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
		if (MessageBox.Show(this, "Se encontraron estos enlaces:\n\n" + text + "\n\n¿Aplicarlos?", Text, MessageBoxButtons.YesNo,
				MessageBoxIcon.Question) != DialogResult.Yes)
		{
			_status.Text = "Enlaces sugeridos sin aplicar.";
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
				_linkExamples[suggestion.Level] = $"{suggestion.Matches} elementos con valor que coincide; p. ej. {suggestion.Example}";
			}
		}

		_config.Save();
		RefreshLinks();
		_status.Text = "Enlaces aplicados. Pulsa \"Analizar modelo →\".";
	}

	#endregion
}
