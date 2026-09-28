using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RevitDwgExploder.UI;

/// <summary>Opciones para importar un PDF: páginas, escala del dibujo y destino.</summary>
internal sealed class PdfImportDialog : Form
{
	private readonly RadioButton _allPages = new RadioButton();

	private readonly RadioButton _rangePages = new RadioButton();

	private readonly TextBox _range = new TextBox();

	private readonly NumericUpDown _scale = new NumericUpDown();

	private readonly CheckBox _autoScale = new CheckBox();

	private readonly RadioButton _toDrafting = new RadioButton();

	private readonly RadioButton _toCurrent = new RadioButton();

	/// <param name="fileLabel">Nombre del archivo (o "N archivos").</param>
	/// <param name="pageCount">Páginas del archivo (0 si son varios archivos: se importan todas las páginas).</param>
	/// <param name="detectedScale">Escala encontrada en los textos del PDF (0 si no se encontró).</param>
	/// <param name="currentViewName">Vista actual si admite líneas de detalle (null si no: solo vistas de dibujo).</param>
	public PdfImportDialog(string fileLabel, int pageCount, int detectedScale, string currentViewName)
	{
		Text = "EMASY · Importar PDF";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(500, 340);
		Font = new Font("Segoe UI", 9f);

		var title = new Label { Text = fileLabel, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(16, 12), Size = new Size(470, 24), AutoEllipsis = true };

		var pagesBox = new GroupBox { Text = "Páginas", Location = new Point(16, 44), Size = new Size(468, 80) };
		_allPages.Text = pageCount > 0 ? $"Todas ({pageCount})" : "Todas las páginas de cada archivo";
		_allPages.Checked = true;
		_allPages.Location = new Point(12, 22);
		_allPages.AutoSize = true;
		_rangePages.Text = "Solo:";
		_rangePages.Location = new Point(12, 48);
		_rangePages.AutoSize = true;
		_rangePages.Enabled = pageCount > 1;
		_range.Location = new Point(70, 46);
		_range.Width = 150;
		_range.Text = "1";
		_range.Enabled = pageCount > 1;
		_range.TextChanged += (s, e) => _rangePages.Checked = true;
		var rangeHint = new Label { Text = "p.ej. 1-3, 5", Location = new Point(228, 49), AutoSize = true, ForeColor = SystemColors.GrayText };
		pagesBox.Controls.AddRange(new Control[] { _allPages, _rangePages, _range, rangeHint });

		var scaleBox = new GroupBox { Text = "Escala del dibujo en el PDF", Location = new Point(16, 132), Size = new Size(468, 84) };
		var one = new Label { Text = "1 :", Location = new Point(12, 26), AutoSize = true };
		_scale.Location = new Point(36, 23);
		_scale.Width = 80;
		_scale.Minimum = 1;
		_scale.Maximum = 10000;
		_scale.Value = detectedScale > 0 ? detectedScale : 100;
		var scaleHint = new Label
		{
			Text = "Las medidas del papel se multiplican por esta escala (dibujo a tamaño real)\ny la vista usa la misma escala, así los textos conservan su tamaño.",
			Location = new Point(126, 18),
			Size = new Size(336, 34),
			ForeColor = SystemColors.GrayText
		};
		_autoScale.Text = detectedScale > 0 ? $"Detectada en el PDF: 1:{detectedScale}" : "Detectar la escala en el texto de cada PDF (\"ESC 1:50\"…)";
		_autoScale.Location = new Point(12, 56);
		_autoScale.AutoSize = true;
		_autoScale.Checked = detectedScale > 0 || pageCount == 0;
		_autoScale.Enabled = pageCount == 0;
		scaleBox.Controls.AddRange(new Control[] { one, _scale, scaleHint, _autoScale });

		var destBox = new GroupBox { Text = "Destino", Location = new Point(16, 224), Size = new Size(468, 72) };
		_toDrafting.Text = "Una vista de dibujo nueva por página";
		_toDrafting.Checked = true;
		_toDrafting.Location = new Point(12, 20);
		_toDrafting.AutoSize = true;
		_toCurrent.Text = currentViewName != null ? $"En la vista actual ({currentViewName}), solo 1 página" : "En la vista actual (no admite líneas de detalle)";
		_toCurrent.Location = new Point(12, 44);
		_toCurrent.AutoSize = true;
		_toCurrent.Enabled = currentViewName != null && pageCount > 0;
		destBox.Controls.AddRange(new Control[] { _toDrafting, _toCurrent });

		var ok = new Button { Text = "Importar", DialogResult = DialogResult.OK, Location = new Point(300, 302), Size = new Size(90, 30) };
		ok.Font = new Font(ok.Font, FontStyle.Bold);
		var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(396, 302), Size = new Size(90, 30) };

		Controls.AddRange(new Control[] { title, pagesBox, scaleBox, destBox, ok, cancel });
		AcceptButton = ok;
		CancelButton = cancel;
	}

	public bool AllPages => _allPages.Checked;

	public string PageRange => _range.Text;

	/// <summary>Escala elegida; 0 = detectar en cada PDF (solo al importar varios archivos).</summary>
	public int DrawingScale => _autoScale.Enabled && _autoScale.Checked ? 0 : (int)_scale.Value;

	public bool IntoCurrentView => _toCurrent.Enabled && _toCurrent.Checked;
}
