using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitDwgExploder.Pdf;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Busca el archivo de un PDF insertado en Revit cuando ya no está en la ruta que Revit recuerda (se insertó en otro
/// equipo, se movió o se descargó otra vez como "archivo (5).pdf"): por nombre, junto al modelo, en Descargas,
/// Escritorio, Documentos y en las carpetas que el usuario ya eligió en esta sesión. Solo acepta un archivo si su
/// página tiene las mismas proporciones que la imagen insertada (así no se usa otra versión del plano).
/// </summary>
internal sealed class PdfFileLocator
{
	/// <summary>Carpetas elegidas por el usuario en esta sesión de Revit (se buscan primero la próxima vez).</summary>
	private static readonly List<string> RememberedFolders = new List<string>();

	private const int MaxFoldersPerRoot = 300;

	private readonly Document _doc;

	private readonly List<string> _roots = new List<string>();

	private Dictionary<string, List<string>> _byKey;

	public PdfFileLocator(Document doc)
	{
		_doc = doc;
	}

	/// <summary>Ruta guardada en el tipo de imagen, absoluta si se puede (puede no existir).</summary>
	public static string StoredPath(Document doc, ImageType type)
	{
		string path;
		try
		{
			path = type?.Path;
		}
		catch (Exception)
		{
			return null;
		}

		try
		{
			if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && ModelFolder(doc) is string folder)
			{
				path = Path.GetFullPath(Path.Combine(folder, path));
			}
		}
		catch (Exception)
		{
		}

		return path;
	}

	/// <summary>Recuerda la carpeta de un archivo elegido a mano: se busca ahí para los demás PDF.</summary>
	public static void Remember(string file)
	{
		string folder = Path.GetDirectoryName(file);
		if (string.IsNullOrEmpty(folder))
		{
			return;
		}

		RememberedFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
		RememberedFolders.Insert(0, folder);
	}

	/// <summary>Añade una carpeta a la búsqueda (vuelve a indexar en la próxima búsqueda).</summary>
	public void AddFolder(string folder)
	{
		if (!string.IsNullOrEmpty(folder) && !_roots.Contains(folder, StringComparer.OrdinalIgnoreCase))
		{
			_roots.Insert(0, folder);
			_byKey = null;
		}
	}

	/// <summary>Archivo del PDF de la imagen, o null si no aparece ninguno que coincida.</summary>
	public string Find(ImageType type, ImageInstance sample)
	{
		string stored = StoredPath(_doc, type);
		if (!string.IsNullOrEmpty(stored) && File.Exists(stored))
		{
			return stored;
		}

		var names = new List<string>();
		if (!string.IsNullOrEmpty(stored))
		{
			names.Add(Path.GetFileName(stored));
		}

		names.Add(type.Name);
		List<string> keys = names.Select(Key).Where(k => k.Length > 0).Distinct().ToList();
		if (keys.Count == 0)
		{
			return null;
		}

		EnsureIndex(stored);
		var candidates = keys.SelectMany(k => _byKey.TryGetValue(k, out List<string> list) ? list : Enumerable.Empty<string>())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(f => names.Any(n => string.Equals(Path.GetFileName(f), EnsurePdf(n), StringComparison.OrdinalIgnoreCase)
				|| f.EndsWith("\\" + EnsurePdf(n), StringComparison.OrdinalIgnoreCase)))
			.ThenByDescending(SafeWriteTime)
			.ToList();
		return candidates.FirstOrDefault(f => Matches(f, type, sample));
	}

	/// <summary>
	/// ¿El archivo puede ser el PDF de la imagen? Tiene la página indicada y sus proporciones coinciden con las de la
	/// imagen insertada (±3 %).
	/// </summary>
	public static bool Matches(string file, ImageType type, ImageInstance sample)
	{
		try
		{
			var (pages, width, height) = PdfPageReader.PageInfo(file, PdfExploder.PageOf(type));
			if (pages < PdfExploder.PageOf(type) || width <= 0 || height <= 0)
			{
				return false;
			}

			if (sample == null || sample.Width <= 0 || sample.Height <= 0)
			{
				return true;
			}

			double expected = sample.Width / sample.Height;
			double actual = width / height;
			return Math.Abs(actual - expected) / expected <= 0.03;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Nombre normalizado: sin carpeta, sin ".pdf", sin el " (5)" de las copias, en minúsculas.</summary>
	private static string Key(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return string.Empty;
		}

		// Sin Path.GetFileName: el nombre de un tipo puede tener caracteres que no valen en una ruta.
		string key = name.Trim();
		int slash = Math.Max(key.LastIndexOf('\\'), key.LastIndexOf('/'));
		if (slash >= 0)
		{
			key = key.Substring(slash + 1);
		}

		key = Regex.Replace(key, @"\s*\(\d+\)\s*$", string.Empty); // nombre del tipo: "plano.pdf (5)"
		if (key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
		{
			key = key.Substring(0, key.Length - 4);
		}

		key = Regex.Replace(key, @"\s*\(\d+\)\s*$", string.Empty); // archivo descargado: "plano (5).pdf"
		return key.Trim().ToLowerInvariant();
	}

	private static string EnsurePdf(string name)
	{
		string clean = Regex.Replace(name.Trim(), @"\s*\(\d+\)\s*$", string.Empty);
		return clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".pdf";
	}

	private static DateTime SafeWriteTime(string file)
	{
		try
		{
			return File.GetLastWriteTimeUtc(file);
		}
		catch (Exception)
		{
			return DateTime.MinValue;
		}
	}

	private static string ModelFolder(Document doc)
	{
		try
		{
			string path = doc?.PathName;
			return !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) ? Path.GetDirectoryName(path) : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Índice nombre normalizado → archivos PDF de las carpetas de búsqueda (y una subcarpeta de profundidad).</summary>
	private void EnsureIndex(string stored)
	{
		if (_byKey != null)
		{
			return;
		}

		var roots = new List<string>(_roots);
		roots.AddRange(RememberedFolders);
		try
		{
			if (!string.IsNullOrEmpty(stored) && Path.IsPathRooted(stored))
			{
				roots.Add(Path.GetDirectoryName(stored));
			}
		}
		catch (Exception)
		{
		}

		string model = ModelFolder(_doc);
		if (model != null)
		{
			roots.Add(model);
			roots.Add(Path.GetDirectoryName(model));
		}

		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		roots.Add(Path.Combine(profile, "Downloads"));
		roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
		roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
		string oneDrive = Environment.GetEnvironmentVariable("OneDrive");
		if (!string.IsNullOrEmpty(oneDrive))
		{
			roots.Add(Path.Combine(oneDrive, "Downloads"));
			roots.Add(Path.Combine(oneDrive, "Descargas"));
		}

		_byKey = new Dictionary<string, List<string>>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r)))
		{
			if (!seen.Add(root) || !Directory.Exists(root))
			{
				continue;
			}

			var folders = new List<string> { root };
			try
			{
				folders.AddRange(Directory.EnumerateDirectories(root).Take(MaxFoldersPerRoot));
			}
			catch (Exception)
			{
			}

			foreach (string folder in folders)
			{
				if (folder != root && !seen.Add(folder))
				{
					continue;
				}

				try
				{
					foreach (string file in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly))
					{
						string key = Key(file);
						if (!_byKey.TryGetValue(key, out List<string> list))
						{
							list = new List<string>();
							_byKey[key] = list;
						}

						list.Add(file);
					}
				}
				catch (Exception)
				{
				}
			}
		}
	}
}
