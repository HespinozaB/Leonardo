using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using RevitParamFill.Commands;
using RevitParamFill.Core;

namespace RevitParamFill.UI;

/// <summary>Pestaña 2: conjuntos de sectores dibujados en planta (regiones rellenas o líneas) o en un DWG.</summary>
internal sealed partial class FillForm
{
	private const string AllItems = "(todos)";

	/// <summary>Opciones de "Nombre del sector desde" (dependen del origen y de los parámetros de las regiones del modelo).</summary>
	private List<(SectorName Kind, string Parameter, string Text)> _nameOptions = new List<(SectorName, string, string)>();

	private readonly ListBox _sectorList = new ListBox();
	private readonly TextBox _secName = new TextBox();
	private readonly RadioButton _secSketch = new RadioButton();
	private readonly RadioButton _secDwgSource = new RadioButton();
	private readonly ComboBox _secView = new ComboBox();
	private readonly RadioButton _secRegions = new RadioButton();
	private readonly RadioButton _secLines = new RadioButton();
	private readonly ComboBox _secFilter = new ComboBox();
	private readonly ComboBox _secDwg = new ComboBox();
	private readonly ComboBox _secLayer = new ComboBox();
	private readonly TextBox _secPath = new TextBox();
	private readonly ComboBox _secNameFrom = new ComboBox();
	private readonly CheckBox _secOnlyLevel = new CheckBox();
	private readonly CheckBox _secLargest = new CheckBox();
	private readonly ListView _secResult = new ListView();
	private readonly Panel _sketchPanel = new FlowLayoutPanel();
	private readonly Panel _dwgPanel = new FlowLayoutPanel();

	private SectorSet Current => _sectorList.SelectedIndex >= 0 && _sectorList.SelectedIndex < _set.SectorSets.Count ? _set.SectorSets[_sectorList.SelectedIndex] : null;

	private TabPage BuildSectorsPage()
	{
		var page = new TabPage("3 · Sectorización");
		var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
		bool splitSet = false;
		split.SizeChanged += (s, e) =>
		{
			if (!splitSet && split.Width > 700)
			{
				splitSet = true;
				Safe(() => split.SplitterDistance = 250);
			}
		};

		// Izquierda: conjuntos de sectores.
		var left = new Panel { Dock = DockStyle.Fill };
		_sectorList.Dock = DockStyle.Fill;
		_sectorList.SelectedIndexChanged += (s, e) => Safe(ShowSectorSet);
		var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
		leftButtons.Controls.Add(NewButton("Añadir", Do(() =>
		{
			_set.SectorSets.Add(new SectorSet { Name = "Sectores " + (_set.SectorSets.Count + 1) });
			FillSectorList();
			_sectorList.SelectedIndex = _set.SectorSets.Count - 1;
		})));
		leftButtons.Controls.Add(NewButton("Quitar", Do(() =>
		{
			if (Current != null)
			{
				_set.SectorSets.Remove(Current);
				FillSectorList();
			}
		})));
		left.Controls.Add(_sectorList);
		left.Controls.Add(leftButtons);
		left.Controls.Add(new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 6, 4, 0), Text = "Conjuntos de sectores (el nombre es el que usan las reglas):" });
		split.Panel1.Controls.Add(left);

		// Derecha: configuración del conjunto y sectores leídos.
		var form = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(10), FlowDirection = FlowDirection.LeftToRight };
		form.Controls.Add(NewLabel("Nombre:"));
		_secName.Width = 200;
		_secName.TextChanged += (s, e) => Safe(() => Update(set =>
		{
			set.Name = _secName.Text.Trim();
			int index = _sectorList.SelectedIndex;
			_filling = true;
			_sectorList.Items[index] = set.Name;
			_filling = false;
		}));
		form.Controls.Add(_secName);
		form.Controls.Add(NewLabel("   Origen:"));
		_secSketch.Text = "Trazo en planta";
		_secSketch.AutoSize = true;
		_secDwgSource.Text = "DWG vinculado / importado";
		_secDwgSource.AutoSize = true;
		_secSketch.CheckedChanged += (s, e) => Safe(() => Update(set =>
		{
			set.Source = _secSketch.Checked ? SectorSource.Trazo : SectorSource.Dwg;
			ToggleSourcePanels();
			FillSectorChoices();
		}));
		form.Controls.AddRange(new Control[] { _secSketch, _secDwgSource });
		form.SetFlowBreak(_secDwgSource, true);

		form.Controls.Add(NewLabel("Vista de planta:"));
		_secView.DropDownStyle = ComboBoxStyle.DropDownList;
		_secView.Width = 380;
		_secView.SelectedIndexChanged += (s, e) => Safe(() => Update(set =>
		{
			set.ViewId = _secView.SelectedIndex >= 0 && _secView.SelectedIndex < _catalog.Views.Count ? _catalog.Views[_secView.SelectedIndex].Id : -1;
			FillDwgChoices();
		}));
		form.Controls.Add(_secView);
		form.Controls.Add(NewLabel("   Nombre del sector desde:"));
		_secNameFrom.DropDownStyle = ComboBoxStyle.DropDownList;
		_secNameFrom.Width = 300;
		_secNameFrom.SelectedIndexChanged += (s, e) => Safe(() => Update(set =>
		{
			int index = _secNameFrom.SelectedIndex;
			if (index >= 0 && index < _nameOptions.Count)
			{
				set.NameFrom = _nameOptions[index].Kind;
				set.NameParameter = _nameOptions[index].Parameter;
			}
		}));
		form.Controls.Add(_secNameFrom);
		form.SetFlowBreak(_secNameFrom, true);

		// Trazo.
		_sketchPanel.AutoSize = true;
		_sketchPanel.Margin = new Padding(0);
		_secRegions.Text = "Regiones rellenas";
		_secRegions.AutoSize = true;
		_secLines.Text = "Lazos de líneas de detalle";
		_secLines.AutoSize = true;
		_secRegions.CheckedChanged += (s, e) => Safe(() => Update(set =>
		{
			set.UseFilledRegions = _secRegions.Checked;
			FillSectorChoices();
		}));
		_secFilter.Width = 260;
		_secFilter.DropDownStyle = ComboBoxStyle.DropDownList;
		_secFilter.SelectedIndexChanged += (s, e) => Safe(() => Update(set => set.Filter = _secFilter.SelectedItem as string == AllItems ? string.Empty : _secFilter.SelectedItem as string ?? string.Empty));
		_sketchPanel.Controls.AddRange(new Control[] { NewLabel("Usar:"), _secRegions, _secLines, NewLabel("   Tipo / estilo:"), _secFilter });
		form.Controls.Add(_sketchPanel);
		form.SetFlowBreak(_sketchPanel, true);

		// DWG.
		_dwgPanel.AutoSize = true;
		_dwgPanel.Margin = new Padding(0);
		_secDwg.DropDownStyle = ComboBoxStyle.DropDownList;
		_secDwg.Width = 300;
		_secDwg.SelectedIndexChanged += (s, e) => Safe(() => Update(set =>
		{
			set.DwgInstanceId = (_secDwg.SelectedItem as DwgItem)?.Id ?? -1;
			_secLayer.Items.Clear();
		}));
		_secLayer.Width = 200;
		_secLayer.DropDownStyle = ComboBoxStyle.DropDown;
		_secLayer.TextChanged += (s, e) => Safe(() => Update(set => set.Layer = _secLayer.Text == AllItems ? string.Empty : _secLayer.Text.Trim()));
		_secPath.Width = 260;
		_secPath.TextChanged += (s, e) => Safe(() => Update(set => set.DwgPath = _secPath.Text.Trim()));
		var browse = NewButton("…", Do(() =>
		{
			using var dialog = new OpenFileDialog { Filter = "DWG (*.dwg)|*.dwg" };
			if (dialog.ShowDialog(this) == DialogResult.OK)
			{
				_secPath.Text = dialog.FileName;
			}
		}));
		_dwgPanel.Controls.AddRange(new Control[]
		{
			NewLabel("DWG:"), _secDwg,
			NewButton("Leer capas", Do(() => RaiseSector(FillAction.ListLayers, "Leyendo capas del DWG…"))),
			NewLabel("Capa:"), _secLayer,
			NewLabel("   Archivo (si está importado):"), _secPath, browse
		});
		form.Controls.Add(_dwgPanel);
		form.SetFlowBreak(_dwgPanel, true);

		_secOnlyLevel.Text = "Solo los elementos del nivel de la vista (si no, el sector vale para todos los niveles)";
		_secOnlyLevel.AutoSize = true;
		_secOnlyLevel.CheckedChanged += (s, e) => Safe(() => Update(set => set.OnlyViewLevel = _secOnlyLevel.Checked));
		form.Controls.Add(_secOnlyLevel);
		form.SetFlowBreak(_secOnlyLevel, true);
		_secLargest.Text = "Si un elemento cruza varios sectores, asignar el que contiene la mayor parte de su área (si no, solo cuenta su punto central)";
		_secLargest.AutoSize = true;
		_secLargest.CheckedChanged += (s, e) => Safe(() => Update(set => set.CenterOnly = !_secLargest.Checked));
		form.Controls.Add(_secLargest);
		form.SetFlowBreak(_secLargest, true);
		form.Controls.Add(NewButton("Leer sectores", Do(() => RaiseSector(FillAction.ReadSectors, "Leyendo sectores…")), bold: true));
		form.Controls.Add(NewButton("Ver en Revit", Do(() =>
		{
			_handler.Ids = (Current?.Sectors ?? new List<Polygon>()).Select(p => p.SourceId).Where(id => id >= 0).Distinct().ToList();
			Raise(FillAction.Select, null);
		})));

		_secResult.Dock = DockStyle.Fill;
		_secResult.View = View.Details;
		_secResult.FullRowSelect = true;
		_secResult.GridLines = true;
		_secResult.Columns.Add("Sector (valor que se escribirá)", 380);
		_secResult.Columns.Add("Área (m²)", 100, HorizontalAlignment.Right);
		_secResult.Columns.Add("Elemento de Revit", 140);

		var right = new Panel { Dock = DockStyle.Fill };
		right.Controls.Add(_secResult);
		right.Controls.Add(form);
		split.Panel2.Controls.Add(right);
		page.Controls.Add(split);
		return page;
	}

	private sealed class DwgItem
	{
		public long Id;

		public string Text;

		public override string ToString() => Text;
	}

	private void Update(Action<SectorSet> change)
	{
		if (!_filling && Current != null)
		{
			change(Current);
		}
	}

	private void RaiseSector(FillAction action, string busy)
	{
		if (Current == null)
		{
			MessageBox.Show(this, "Añade o elige un conjunto de sectores.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		_handler.SectorIndex = _sectorList.SelectedIndex;
		Raise(action, busy);
	}

	private void FillSectorList()
	{
		_filling = true;
		int selected = Math.Max(0, _sectorList.SelectedIndex);
		_sectorList.Items.Clear();
		foreach (SectorSet set in _set.SectorSets)
		{
			_sectorList.Items.Add(set.Name);
		}

		_filling = false;
		if (_sectorList.Items.Count > 0)
		{
			_sectorList.SelectedIndex = Math.Min(selected, _sectorList.Items.Count - 1);
		}
		else
		{
			ShowSectorSet();
		}
	}

	/// <summary>Muestra la configuración del conjunto elegido.</summary>
	private void ShowSectorSet()
	{
		if (_filling)
		{
			return;
		}

		SectorSet set = Current;
		_filling = true;
		try
		{
			_secName.Text = set?.Name ?? string.Empty;
			_secSketch.Checked = set == null || set.Source == SectorSource.Trazo;
			_secDwgSource.Checked = set != null && set.Source == SectorSource.Dwg;
			_secRegions.Checked = set == null || set.UseFilledRegions;
			_secLines.Checked = set != null && !set.UseFilledRegions;
			_secOnlyLevel.Checked = set?.OnlyViewLevel ?? false;
			_secLargest.Checked = !(set?.CenterOnly ?? false);
			_secPath.Text = set?.DwgPath ?? string.Empty;
			_secLayer.Text = set == null || set.Layer.Length == 0 ? AllItems : set.Layer;
			int view = set == null ? -1 : _catalog.Views.FindIndex(v => v.Id == set.ViewId);
			_secView.SelectedIndex = view < _secView.Items.Count ? view : -1;
		}
		finally
		{
			_filling = false;
		}

		ToggleSourcePanels();
		FillSectorChoices();
		ShowSectorResult();
	}

	/// <summary>Texto, tipo/capa y, para regiones rellenas: Marca, Comentarios y cada parámetro de las regiones del modelo.</summary>
	private void RefreshNameOptions()
	{
		SectorSet set = Current;
		_nameOptions = new List<(SectorName, string, string)>
		{
			(SectorName.Texto, string.Empty, "Texto que cae dentro"),
			(SectorName.Tipo, string.Empty, set != null && set.Source == SectorSource.Dwg ? "Capa del DWG" : "Nombre de tipo de la región")
		};
		if (set == null || set.Source == SectorSource.Trazo)
		{
			_nameOptions.Add((SectorName.Parametro, SectorSet.MarkToken, "Marca de la región"));
			_nameOptions.Add((SectorName.Comentarios, string.Empty, "Comentarios de la región"));
			_nameOptions.AddRange(_catalog.RegionParameters.Select(p => (SectorName.Parametro, p, "Parámetro: " + p)));
			if (set != null && set.NameFrom == SectorName.Parametro && set.NameParameter.Length > 0 && !_nameOptions.Any(o => o.Item2 == set.NameParameter))
			{
				_nameOptions.Add((SectorName.Parametro, set.NameParameter, "Parámetro: " + set.NameParameter));
			}
		}

		bool filling = _filling;
		_filling = true;
		_secNameFrom.Items.Clear();
		_secNameFrom.Items.AddRange(_nameOptions.Select(o => (object)o.Item3).ToArray());
		int selected = set == null ? 0 : _nameOptions.FindIndex(o => o.Item1 == set.NameFrom && (o.Item1 != SectorName.Parametro || o.Item2 == set.NameParameter));
		_secNameFrom.SelectedIndex = Math.Max(0, selected);
		_filling = filling;
		if (set != null && selected < 0)
		{
			// Opción no disponible (p. ej. Comentarios en un DWG): se vuelve al texto.
			set.NameFrom = SectorName.Texto;
		}
	}

	private void ToggleSourcePanels()
	{
		bool sketch = _secSketch.Checked;
		_sketchPanel.Visible = sketch;
		_dwgPanel.Visible = !sketch;
		_secNameFrom.Enabled = true;
	}

	/// <summary>Opciones que dependen del modelo: vistas, tipos de región / estilos de línea y DWG.</summary>
	private void FillSectorChoices()
	{
		RefreshNameOptions();
		SectorSet set = Current;
		_filling = true;
		try
		{
			if (_secView.Items.Count != _catalog.Views.Count)
			{
				_secView.Items.Clear();
				_secView.Items.AddRange(_catalog.Views.Select(v => (object)(v.Name + (v.Level.Length > 0 ? $"  [{v.Level}]" : string.Empty))).ToArray());
			}

			int view = set == null ? -1 : _catalog.Views.FindIndex(v => v.Id == set.ViewId);
			_secView.SelectedIndex = view;

			_secFilter.Items.Clear();
			_secFilter.Items.Add(AllItems);
			_secFilter.Items.AddRange((set == null || set.UseFilledRegions ? _catalog.RegionTypes : _catalog.LineStyles).Cast<object>().ToArray());
			int filter = set == null || set.Filter.Length == 0 ? 0 : _secFilter.Items.IndexOf(set.Filter);
			if (filter < 0 && set != null)
			{
				_secFilter.Items.Add(set.Filter);
				filter = _secFilter.Items.Count - 1;
			}

			_secFilter.SelectedIndex = Math.Max(0, filter);
		}
		finally
		{
			_filling = false;
		}

		FillDwgChoices();
	}

	/// <summary>Los DWG de la vista elegida (y los de modelo, visibles en todas).</summary>
	private void FillDwgChoices()
	{
		SectorSet set = Current;
		_filling = true;
		try
		{
			_secDwg.Items.Clear();
			foreach (var dwg in _catalog.Dwgs.Where(d => set == null || d.ViewId < 0 || d.ViewId == set.ViewId))
			{
				_secDwg.Items.Add(new DwgItem
				{
					Id = dwg.Id,
					Text = $"{dwg.Name} ({(dwg.Linked ? "vinculado" : "importado")}{(dwg.ViewId >= 0 ? ", solo en la vista" : string.Empty)})"
				});
			}

			DwgItem current = _secDwg.Items.Cast<DwgItem>().FirstOrDefault(d => set != null && d.Id == set.DwgInstanceId);
			if (current != null)
			{
				_secDwg.SelectedItem = current;
			}
			else if (_secDwg.Items.Count == 1 && set != null)
			{
				_secDwg.SelectedIndex = 0;
				set.DwgInstanceId = ((DwgItem)_secDwg.Items[0]).Id;
			}
		}
		finally
		{
			_filling = false;
		}
	}

	private void SetLayers(int index, List<string> layers)
	{
		SetBusy(false, $"{layers.Count} capa(s) con geometría en el DWG.");
		if (index != _sectorList.SelectedIndex)
		{
			return;
		}

		string current = _secLayer.Text;
		_secLayer.Items.Clear();
		_secLayer.Items.Add(AllItems);
		_secLayer.Items.AddRange(layers.Cast<object>().ToArray());
		_secLayer.Text = current;
		_secLayer.DroppedDown = true;
	}

	private void SetSectors(int index, List<Polygon> sectors, string message)
	{
		SetBusy(false, message);
		if (index >= 0 && index < _set.SectorSets.Count)
		{
			_set.SectorSets[index].Sectors = sectors;
		}

		ShowSectorResult();
		SaveDefault();
	}

	private void ShowSectorResult()
	{
		_secResult.BeginUpdate();
		_secResult.Items.Clear();
		foreach (Polygon polygon in (Current?.Sectors ?? new List<Polygon>()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
		{
			var item = new ListViewItem(new[]
			{
				polygon.Name,
				(polygon.Area * 0.09290304).ToString("0.00", CultureInfo.CurrentCulture),
				polygon.SourceId >= 0 ? polygon.SourceId.ToString(CultureInfo.InvariantCulture) : string.Empty
			});
			if (polygon.Name.EndsWith("(sin nombre)", StringComparison.Ordinal))
			{
				item.ForeColor = Color.Firebrick;
			}

			_secResult.Items.Add(item);
		}

		_secResult.EndUpdate();
	}
}
