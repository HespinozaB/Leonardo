using System.Drawing;
using System.Windows.Forms;

namespace RevitDwgExploder.UI;

/// <summary>Opciones para importar imágenes: resolución, escala del dibujo y destino.</summary>
internal sealed class ImageImportDialog : Form
{
	private readonly NumericUpDown _scale = new NumericUpDown();

	private readonly NumericUpDown _dpi = new NumericUpDown();

	private readonly CheckBox _fileDpi = new CheckBox();

	private readonly RadioButton _toDrafting = new RadioButton();

	private readonly RadioButton _toCurrent = new RadioButton();

	/// <param name="fileLabel">Nombre del archivo (o "N archivos").</param>
	/// <param name="fileDpi">Resolución guardada en el archivo (0 si son varios archivos).</param>
	/// <param name="pixelSize">"1920 × 1080 px" o null.</param>
	/// <param name="currentViewName">Vista actual si admite líneas de detalle (null si no o si son varios archivos).</param>
	public ImageImportDialog(string fileLabel, int fileDpi, string pixelSize, string currentViewName)
	{
		Text = "EMASY · Importar imagen";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(500, 330);
		Font = new Font("Segoe UI", 9f);

		var title = new Label { Text = fileLabel, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(16, 12), Size = new Size(470, 24), AutoEllipsis = true };
		var size = new Label { Text = pixelSize ?? string.Empty, Location = new Point(18, 38), AutoSize = true, ForeColor = SystemColors.GrayText };

		var scaleBox = new GroupBox { Text = "Tamaño del dibujo", Location = new Point(16, 62), Size = new Size(468, 130) };
		var dpiLabel = new Label { Text = "Resolución:", Location = new Point(12, 28), AutoSize = true };
		_dpi.Location = new Point(96, 25);
		_dpi.Width = 70;
		_dpi.Minimum = 10;
		_dpi.Maximum = 2400;
		_dpi.Value = fileDpi > 0 ? fileDpi : 96;
		var dpiUnit = new Label { Text = "ppp (píxeles por pulgada del papel)", Location = new Point(172, 28), AutoSize = true };
		_fileDpi.Text = "Usar la resolución guardada en cada archivo";
		_fileDpi.Location = new Point(96, 52);
		_fileDpi.AutoSize = true;
		_fileDpi.Visible = fileDpi <= 0;
		_fileDpi.Checked = fileDpi <= 0;
		_fileDpi.CheckedChanged += (s, e) => _dpi.Enabled = !_fileDpi.Checked;
		_dpi.Enabled = fileDpi > 0;

		var one = new Label { Text = "Escala:   1 :", Location = new Point(12, 84), AutoSize = true };
		_scale.Location = new Point(96, 81);
		_scale.Width = 70;
		_scale.Minimum = 1;
		_scale.Maximum = 10000;
		_scale.Value = 100;
		var scaleHint = new Label
		{
			Text = "El papel (píxeles ÷ ppp) se multiplica por la escala;\nla vista usa la misma escala.",
			Location = new Point(172, 78),
			Size = new Size(290, 34),
			ForeColor = SystemColors.GrayText
		};
		scaleBox.Controls.AddRange(new Control[] { dpiLabel, _dpi, dpiUnit, _fileDpi, one, _scale, scaleHint });

		var destBox = new GroupBox { Text = "Destino", Location = new Point(16, 200), Size = new Size(468, 72) };
		_toDrafting.Text = "Una vista de dibujo nueva por imagen";
		_toDrafting.Checked = true;
		_toDrafting.Location = new Point(12, 20);
		_toDrafting.AutoSize = true;
		_toCurrent.Text = currentViewName != null ? $"En la vista actual ({currentViewName})" : "En la vista actual (no disponible)";
		_toCurrent.Location = new Point(12, 44);
		_toCurrent.AutoSize = true;
		_toCurrent.Enabled = currentViewName != null;
		destBox.Controls.AddRange(new Control[] { _toDrafting, _toCurrent });

		var ok = new Button { Text = "Importar", DialogResult = DialogResult.OK, Location = new Point(300, 288), Size = new Size(90, 30) };
		ok.Font = new Font(ok.Font, FontStyle.Bold);
		var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(396, 288), Size = new Size(90, 30) };

		Controls.AddRange(new Control[] { title, size, scaleBox, destBox, ok, cancel });
		AcceptButton = ok;
		CancelButton = cancel;
	}

	public int DrawingScale => (int)_scale.Value;

	/// <summary>Resolución elegida; 0 = la guardada en cada archivo.</summary>
	public int Dpi => _fileDpi.Visible && _fileDpi.Checked ? 0 : (int)_dpi.Value;

	public bool IntoCurrentView => _toCurrent.Enabled && _toCurrent.Checked;
}
