using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EmasyInstaller;

/// <summary>
/// Instalador de EMASY DWG Tools para Revit 2024: copia el addin a la carpeta de addins del usuario,
/// elimina versiones anteriores y quita la marca "descargado de Internet" de todos los archivos, para
/// no tener que desbloquearlos uno a uno.
/// </summary>
internal static class Program
{
	private const string RevitYear = "2024";

	private static readonly string AddinsFolder = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", RevitYear);

	/// <summary>Archivos y carpetas de esta versión y de las anteriores (RevitDwgExploder).</summary>
	private static readonly string[] ManagedFiles = { "EMASY.addin", "RevitDwgExploder.addin" };

	private static readonly string[] ManagedFolders = { "EMASY-" + RevitYear, "RevitDwgExploder-" + RevitYear };

	[STAThread]
	private static int Main(string[] args)
	{
		bool silent = args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/s", StringComparison.OrdinalIgnoreCase));
		bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
		if (silent)
		{
			try
			{
				string result = uninstall ? Uninstall() : Install();
				Console.WriteLine(result);
				return 0;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 1;
			}
		}

		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);
		Application.Run(new InstallerForm());
		return 0;
	}

	internal static string TargetFolder => AddinsFolder;

	internal static bool IsInstalled => File.Exists(Path.Combine(AddinsFolder, "EMASY.addin"));

	internal static bool IsRevitRunning => Process.GetProcessesByName("Revit").Length > 0;

	internal static string Install()
	{
		Directory.CreateDirectory(AddinsFolder);
		RemoveManaged();

		using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
		using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
		{
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				string destination = Path.GetFullPath(Path.Combine(AddinsFolder, entry.FullName));
				if (!destination.StartsWith(Path.GetFullPath(AddinsFolder), StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (string.IsNullOrEmpty(entry.Name))
				{
					Directory.CreateDirectory(destination);
					continue;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(destination));
				entry.ExtractToFile(destination, overwrite: true);
			}
		}

		// Los archivos que escribe el instalador no llevan la marca de "descargado"; por si acaso, se quita.
		UnblockAll();
		return $"EMASY DWG Tools instalado en:\n{AddinsFolder}\n\nTodos los archivos quedaron listos (sin bloqueo de Windows). Abre Revit {RevitYear}: pestaña EMASY → DWG Tools.";
	}

	internal static string Uninstall()
	{
		int removed = RemoveManaged();
		return removed > 0
			? $"EMASY DWG Tools se desinstaló de Revit {RevitYear}."
			: "No se encontró EMASY DWG Tools instalado.";
	}

	private static int RemoveManaged()
	{
		int removed = 0;
		foreach (string file in ManagedFiles.Select(f => Path.Combine(AddinsFolder, f)))
		{
			if (File.Exists(file))
			{
				File.Delete(file);
				removed++;
			}
		}

		foreach (string folder in ManagedFolders.Select(f => Path.Combine(AddinsFolder, f)))
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
				removed++;
			}
		}

		return removed;
	}

	/// <summary>
	/// Borra el flujo "Zone.Identifier" (la marca de archivo descargado) de todos los archivos del addin:
	/// es exactamente lo que hace el botón "Desbloquear" de Propiedades.
	/// </summary>
	private static int UnblockAll()
	{
		int count = 0;
		var files = ManagedFolders
			.Select(f => Path.Combine(AddinsFolder, f))
			.Where(Directory.Exists)
			.SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories))
			.Concat(ManagedFiles.Select(f => Path.Combine(AddinsFolder, f)).Where(File.Exists));
		foreach (string file in files)
		{
			if (DeleteFile(file + ":Zone.Identifier"))
			{
				count++;
			}
		}

		return count;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteFile(string name);
}

internal sealed class InstallerForm : Form
{
	private readonly Label _status = new Label();

	private readonly Button _install = new Button();

	private readonly Button _uninstall = new Button();

	public InstallerForm()
	{
		Text = "EMASY DWG Tools · Revit 2024";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		StartPosition = FormStartPosition.CenterScreen;
		ClientSize = new Size(520, 300);
		Font = new Font("Segoe UI", 9.5f);
		BackColor = Color.White;
		try
		{
			Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
		}
		catch (Exception)
		{
		}

		var logo = new PictureBox { Location = new Point(20, 20), Size = new Size(96, 96), SizeMode = PictureBoxSizeMode.Zoom };
		using (Stream png = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
		{
			if (png != null)
			{
				logo.Image = Image.FromStream(png);
			}
		}

		var title = new Label
		{
			Text = "EMASY · DWG Tools",
			Font = new Font("Segoe UI", 16f, FontStyle.Bold),
			Location = new Point(135, 22),
			AutoSize = true
		};
		var subtitle = new Label
		{
			Text = "Explotar en Vista Actual y Explotar Varios DWG's · Revit 2024",
			Location = new Point(137, 58),
			AutoSize = true,
			ForeColor = Color.DimGray
		};
		var folder = new Label
		{
			Text = "Se instalará en:\n" + Program.TargetFolder,
			Location = new Point(137, 86),
			Size = new Size(370, 44),
			ForeColor = Color.DimGray
		};

		_status.Location = new Point(20, 140);
		_status.Size = new Size(480, 100);

		_install.Text = Program.IsInstalled ? "Actualizar" : "Instalar";
		_install.Location = new Point(200, 252);
		_install.Size = new Size(100, 32);
		_install.Click += (s, e) => Run(Program.Install);

		_uninstall.Text = "Desinstalar";
		_uninstall.Location = new Point(308, 252);
		_uninstall.Size = new Size(100, 32);
		_uninstall.Enabled = Program.IsInstalled;
		_uninstall.Click += (s, e) => Run(Program.Uninstall);

		var close = new Button { Text = "Cerrar", Location = new Point(416, 252), Size = new Size(84, 32) };
		close.Click += (s, e) => Close();

		Controls.AddRange(new Control[] { logo, title, subtitle, folder, _status, _install, _uninstall, close });
		AcceptButton = _install;
		CancelButton = close;
		_status.Text = Program.IsInstalled
			? "Ya hay una versión instalada. \"Actualizar\" la reemplaza por esta."
			: "Pulsa \"Instalar\". No se necesitan permisos de administrador.";
	}

	private void Run(Func<string> action)
	{
		while (Program.IsRevitRunning)
		{
			DialogResult answer = MessageBox.Show(this,
				"Revit está abierto. Ciérralo (guarda tu trabajo) y pulsa Reintentar, porque mientras está abierto los archivos del addin están en uso.",
				"EMASY DWG Tools", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
			if (answer == DialogResult.Cancel)
			{
				return;
			}
		}

		string result;
		try
		{
			UseWaitCursor = true;
			result = action();
			_status.ForeColor = Color.DarkGreen;
			_status.Text = result;
		}
		catch (Exception ex)
		{
			_status.ForeColor = Color.Firebrick;
			_status.Text = "No se pudo completar: " + ex.Message;
			return;
		}
		finally
		{
			UseWaitCursor = false;
			_install.Text = Program.IsInstalled ? "Actualizar" : "Instalar";
			_uninstall.Enabled = Program.IsInstalled;
		}

		// Aviso final con un único botón "Cerrar", que cierra también el instalador.
		using (var done = new DoneDialog(result))
		{
			done.ShowDialog(this);
		}

		Close();
	}
}

/// <summary>Ventana final: confirma el resultado y ofrece solo "Cerrar".</summary>
internal sealed class DoneDialog : Form
{
	public DoneDialog(string message)
	{
		Text = "EMASY DWG Tools";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(440, 190);
		Font = new Font("Segoe UI", 9.5f);
		BackColor = Color.White;

		var title = new Label
		{
			Text = "✔  Proceso completado",
			Font = new Font("Segoe UI", 12f, FontStyle.Bold),
			ForeColor = Color.DarkGreen,
			Location = new Point(20, 16),
			AutoSize = true
		};
		var text = new Label
		{
			Text = message,
			Location = new Point(22, 50),
			Size = new Size(400, 90)
		};
		var close = new Button
		{
			Text = "Cerrar",
			DialogResult = DialogResult.OK,
			Location = new Point(330, 146),
			Size = new Size(90, 30)
		};

		Controls.AddRange(new Control[] { title, text, close });
		AcceptButton = close;
		CancelButton = close;
	}
}
