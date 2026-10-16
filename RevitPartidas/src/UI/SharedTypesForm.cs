using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RevitPartidas.Core;

namespace RevitPartidas.UI;

/// <summary>
/// Tipos con elementos en varios valores del filtro (p. ej. ETAPA 1 y ETAPA 2). Se elige si el tipo nuevo lleva el valor como
/// sufijo o prefijo, qué valor se queda con el tipo original y si el tipo nuevo toma el código de montaje de su etapa.
/// </summary>
internal sealed class SharedTypesForm : Form
{
	private const string Majority = "El valor con más elementos";

	private readonly List<SharedType> _types;
	private readonly Func<string, string, string> _codeFor;
	private readonly RadioButton _suffix = new RadioButton();
	private readonly RadioButton _prefix = new RadioButton();
	private readonly TextBox _template = new TextBox();
	private readonly ComboBox _keep = new ComboBox();
	private readonly CheckBox _setCode = new CheckBox();
	private readonly ListView _list = new ListView();
	private readonly Label _summary = new Label();

	/// <param name="codeFor">(código de montaje del tipo original, valor del filtro) → código para el tipo nuevo.</param>
	public SharedTypesForm(List<SharedType> types, string parameterName, Func<string, string, string> codeFor)
	{
		_types = types;
		_codeFor = codeFor;
		Text = $"Tipos en varias etapas · {types.Count} tipo(s) · {parameterName}";
		Font = new Font("Segoe UI", 9f);
		StartPosition = FormStartPosition.CenterParent;
		Size = new Size(1250, 700);
		MinimumSize = new Size(800, 400);
		ShowInTaskbar = false;
		MinimizeBox = false;

		var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(10) };
		top.Controls.Add(new Label
		{
			AutoSize = true,
			MaximumSize = new Size(1180, 0),
			Text = $"Estos tipos tienen elementos con distintos valores de {parameterName}. El Código de montaje es del tipo, así que no puede ser " +
				"correcto para todos. Duplica el tipo para los elementos de la otra etapa: el tipo nuevo lleva la etapa en el nombre."
		});
		top.SetFlowBreak(top.Controls[0], true);
		top.Controls.Add(new Label { Text = "Nombre del tipo nuevo:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
		_suffix.Text = "Sufijo";
		_suffix.AutoSize = true;
		_suffix.Checked = Settings.Get("splitPrefix") != "1";
		_prefix.Text = "Prefijo";
		_prefix.AutoSize = true;
		_prefix.Checked = !_suffix.Checked;
		_template.Width = 140;
		_template.Text = Settings.Get(_suffix.Checked ? "splitSuffixText" : "splitPrefixText") ?? (_suffix.Checked ? "-{valor}" : "{valor}-");
		_suffix.CheckedChanged += (s, e) =>
		{
			_template.Text = _suffix.Checked ? Settings.Get("splitSuffixText") ?? "-{valor}" : Settings.Get("splitPrefixText") ?? "{valor}-";
			Preview();
		};
		_template.TextChanged += (s, e) => Preview();
		top.Controls.AddRange(new Control[] { _suffix, _prefix, _template });
		top.Controls.Add(new Label { Text = "({valor} = p. ej. ETAPA 1)", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(4, 6, 20, 0) });
		top.Controls.Add(new Label { Text = "El tipo original se queda con:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
		_keep.DropDownStyle = ComboBoxStyle.DropDownList;
		_keep.Width = 200;
		_keep.Items.Add(Majority);
		_keep.Items.AddRange(types.SelectMany(t => t.ByValue.Keys).Distinct().OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray());
		_keep.SelectedIndex = 0;
		_keep.SelectedIndexChanged += (s, e) => Preview();
		top.Controls.Add(_keep);
		_setCode.Text = "Poner al tipo nuevo el código de montaje de su etapa";
		_setCode.AutoSize = true;
		_setCode.Checked = true;
		_setCode.Margin = new Padding(16, 5, 0, 0);
		_setCode.CheckedChanged += (s, e) => Preview();
		top.Controls.Add(_setCode);

		_list.Dock = DockStyle.Fill;
		_list.View = View.Details;
		_list.FullRowSelect = true;
		_list.GridLines = true;
		_list.CheckBoxes = true;
		_list.Columns.Add("Categoría", 140);
		_list.Columns.Add("Familia : Tipo", 330);
		_list.Columns.Add("Elementos por valor", 230);
		_list.Columns.Add("Tipo(s) nuevo(s)", 330);
		_list.Columns.Add("Código de montaje del nuevo", 190);
		_list.ItemCheck += (s, e) =>
		{
			if (_list.Items[e.Index].Tag is SharedType { CanDuplicate: false })
			{
				e.NewValue = CheckState.Unchecked;
			}
		};
		_list.ItemChecked += (s, e) => UpdateSummary();
		foreach (SharedType type in types)
		{
			var item = new ListViewItem(new[] { type.Category, type.FamilyType, string.Empty, string.Empty, string.Empty })
			{
				Tag = type,
				Checked = type.CanDuplicate,
				ForeColor = type.CanDuplicate ? SystemColors.WindowText : Color.Gray
			};
			_list.Items.Add(item);
		}

		var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
		var all = new Button { Text = "Marcar todos", AutoSize = true };
		all.Click += (s, e) => SetAll(true);
		var none = new Button { Text = "Desmarcar todos", AutoSize = true };
		none.Click += (s, e) => SetAll(false);
		var ok = new Button { Text = "Duplicar marcados", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
		ok.Click += (s, e) => Accept();
		var cancel = new Button { Text = "Cerrar", AutoSize = true, DialogResult = DialogResult.Cancel };
		_summary.AutoSize = true;
		_summary.Margin = new Padding(16, 8, 0, 0);
		bottom.Controls.AddRange(new Control[] { all, none, ok, cancel, _summary });
		CancelButton = cancel;

		Controls.Add(_list);
		Controls.Add(top);
		Controls.Add(bottom);
		Preview();
	}

	/// <summary>Los tipos a duplicar (uno por cada valor que no se queda con el original).</summary>
	public List<SplitRequest> Requests { get; private set; } = new List<SplitRequest>();

	private string Kept(SharedType type)
	{
		string chosen = _keep.SelectedItem as string;
		return chosen != Majority && chosen != null && type.ByValue.ContainsKey(chosen)
			? chosen
			: type.ByValue.OrderByDescending(v => v.Value.Count).First().Key;
	}

	private string NewName(SharedType type, string value)
	{
		string part = (_template.Text ?? string.Empty).Replace("{valor}", value);
		return _prefix.Checked ? part + type.TypeName : type.TypeName + part;
	}

	private List<SplitRequest> Build(SharedType type)
	{
		string kept = Kept(type);
		return type.ByValue.Where(v => v.Key != kept).Select(v => new SplitRequest
		{
			TypeId = type.TypeId,
			Value = v.Key,
			NewName = NewName(type, v.Key),
			Elements = v.Value,
			AssemblyCode = _setCode.Checked && !string.IsNullOrEmpty(type.AssemblyCode) ? _codeFor(type.AssemblyCode, v.Key) : null
		}).ToList();
	}

	private void Preview()
	{
		_list.BeginUpdate();
		foreach (ListViewItem item in _list.Items)
		{
			var type = (SharedType)item.Tag;
			string kept = Kept(type);
			List<SplitRequest> requests = Build(type);
			item.SubItems[2].Text = string.Join(" · ", type.ByValue.OrderByDescending(v => v.Value.Count)
				.Select(v => $"{v.Key} ×{v.Value.Count}" + (v.Key == kept ? " (original)" : string.Empty)));
			item.SubItems[3].Text = type.CanDuplicate ? string.Join(" | ", requests.Select(r => r.NewName)) : "(este tipo no se puede duplicar)";
			item.SubItems[4].Text = string.Join(" | ", requests.Select(r => r.AssemblyCode ?? (string.IsNullOrEmpty(type.AssemblyCode) ? "(vacío)" : type.AssemblyCode + " (igual)")));
		}

		_list.EndUpdate();
		UpdateSummary();
	}

	private void UpdateSummary()
	{
		List<SplitRequest> requests = _list.CheckedItems.Cast<ListViewItem>().SelectMany(i => Build((SharedType)i.Tag)).ToList();
		_summary.Text = $"{_list.CheckedItems.Count} tipo(s) marcados → {requests.Count} tipo(s) nuevo(s), {requests.Sum(r => r.Elements.Count)} elemento(s) cambian de tipo.";
	}

	private void SetAll(bool value)
	{
		foreach (ListViewItem item in _list.Items)
		{
			item.Checked = value && ((SharedType)item.Tag).CanDuplicate;
		}
	}

	private void Accept()
	{
		Requests = _list.CheckedItems.Cast<ListViewItem>().SelectMany(i => Build((SharedType)i.Tag)).ToList();
		if (Requests.Count == 0)
		{
			MessageBox.Show(this, "Marca al menos un tipo.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (!_template.Text.Contains("{valor}") && MessageBox.Show(this, "El texto no contiene {valor}: todos los tipos nuevos tendrán el mismo agregado. ¿Seguir?",
				Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
		{
			return;
		}

		if (MessageBox.Show(this, $"Se crearán {Requests.Count} tipo(s) nuevo(s) y {Requests.Sum(r => r.Elements.Count)} elemento(s) pasarán a ellos.\n" +
				"Si ya existe un tipo con ese nombre en la familia, se reutiliza.\n\nSe deshace con Ctrl+Z en Revit.", Text,
				MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}

		Settings.Set("splitPrefix", _prefix.Checked ? "1" : "0");
		Settings.Set(_prefix.Checked ? "splitPrefixText" : "splitSuffixText", _template.Text);
		DialogResult = DialogResult.OK;
		Close();
	}
}
