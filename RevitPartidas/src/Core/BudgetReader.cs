using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace RevitPartidas.Core;

/// <summary>Una partida (o título) del presupuesto.</summary>
public sealed class BudgetItem
{
	public string Code;

	public string Description;

	public string Unit;

	public double? Metrado;

	/// <summary>Columna "Tabla BIM / Observación" del comparativo (p. ej. "Sin tabla en el modelo BIM").</summary>
	public string Observation;

	public BudgetItem Parent;

	public readonly List<BudgetItem> Children = new List<BudgetItem>();

	/// <summary>Partida final: tiene unidad (se metra). Los títulos solo agrupan.</summary>
	public bool IsLeaf => !string.IsNullOrEmpty(Unit);

	public int Depth => PartidaCode.Depth(Code);

	public bool WithoutBimTable =>
		Observation != null && Observation.IndexOf("sin tabla", StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>El presupuesto cargado desde Excel, indexado por código.</summary>
public sealed class Budget
{
	public string SourcePath;

	public readonly List<BudgetItem> Roots = new List<BudgetItem>();

	public readonly Dictionary<string, BudgetItem> ByCode = new Dictionary<string, BudgetItem>(StringComparer.Ordinal);

	public IEnumerable<BudgetItem> All => ByCode.Values;

	public BudgetItem Find(string code) => code != null && ByCode.TryGetValue(code, out BudgetItem item) ? item : null;
}

/// <summary>
/// Lee el presupuesto de un .xlsx sin librerías externas (un .xlsx es un ZIP con XML). Busca, en cualquier hoja, la fila de
/// cabecera con "Item" y "Descripción", y toma las columnas Und., Metrado y "Tabla BIM / Observación" si existen.
/// </summary>
public static class BudgetReader
{
	private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

	private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

	private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

	public static Budget Read(string path)
	{
		// FileShare.ReadWrite: el Excel puede estar abierto.
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		List<string> sharedStrings = ReadSharedStrings(zip);
		foreach (string sheetPath in SheetPaths(zip))
		{
			ZipArchiveEntry entry = zip.GetEntry(sheetPath);
			if (entry == null)
			{
				continue;
			}

			List<Dictionary<int, string>> rows = ReadRows(entry, sharedStrings);
			Budget budget = Parse(rows);
			if (budget != null)
			{
				budget.SourcePath = path;
				return budget;
			}
		}

		throw new InvalidDataException("No se encontró una hoja con las columnas \"Item\" y \"Descripción\".");
	}

	private static Budget Parse(List<Dictionary<int, string>> rows)
	{
		int header = -1;
		int item = -1, description = -1, unit = -1, metrado = -1, observation = -1;
		for (int r = 0; r < rows.Count && r < 60 && header < 0; r++)
		{
			foreach (KeyValuePair<int, string> cell in rows[r])
			{
				string key = Simplify(cell.Value);
				if (key == "item")
				{
					item = cell.Key;
				}
				else if (key.StartsWith("descripcion", StringComparison.Ordinal))
				{
					description = cell.Key;
				}
			}

			if (item >= 0 && description >= 0)
			{
				header = r;
				foreach (KeyValuePair<int, string> cell in rows[r])
				{
					string key = Simplify(cell.Value);
					if (unit < 0 && (key == "und" || key == "und." || key == "unidad"))
					{
						unit = cell.Key;
					}
					else if (metrado < 0 && key == "metrado")
					{
						metrado = cell.Key;
					}
					else if (observation < 0 && (key.Contains("tabla bim") || key.StartsWith("observacion", StringComparison.Ordinal)))
					{
						observation = cell.Key;
					}
				}
			}
			else
			{
				item = description = -1;
			}
		}

		if (header < 0)
		{
			return null;
		}

		var budget = new Budget();
		for (int r = header + 1; r < rows.Count; r++)
		{
			string code = PartidaCode.Normalize(Get(rows[r], item));
			if (code == null || budget.ByCode.ContainsKey(code))
			{
				continue;
			}

			var budgetItem = new BudgetItem
			{
				Code = code,
				Description = (Get(rows[r], description) ?? string.Empty).Trim(),
				Unit = (Get(rows[r], unit) ?? string.Empty).Trim(),
				Observation = Get(rows[r], observation)?.Trim()
			};
			if (double.TryParse(Get(rows[r], metrado), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
			{
				budgetItem.Metrado = value;
			}

			budget.ByCode[code] = budgetItem;
		}

		if (budget.ByCode.Count == 0)
		{
			return null;
		}

		// Árbol por prefijos (si falta un título intermedio, cuelga del ancestro más cercano).
		foreach (BudgetItem budgetItem in budget.ByCode.Values.OrderBy(i => i.Code, StringComparer.Ordinal))
		{
			string[] levels = PartidaCode.Levels(budgetItem.Code);
			for (int i = budgetItem.Depth - 2; i >= 0 && budgetItem.Parent == null; i--)
			{
				budgetItem.Parent = budget.Find(levels[i]);
			}

			if (budgetItem.Parent != null)
			{
				budgetItem.Parent.Children.Add(budgetItem);
			}
			else
			{
				budget.Roots.Add(budgetItem);
			}
		}

		return budget;
	}

	private static string Get(Dictionary<int, string> row, int column) =>
		column >= 0 && row.TryGetValue(column, out string value) ? value : null;

	/// <summary>Minúsculas, sin tildes ni espacios sobrantes (para reconocer cabeceras).</summary>
	internal static string Simplify(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		string decomposed = text.Replace(' ', ' ').Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(decomposed.Length);
		foreach (char c in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
			{
				builder.Append(c);
			}
		}

		return builder.ToString().Normalize(NormalizationForm.FormC);
	}

	private static IEnumerable<string> SheetPaths(ZipArchive zip)
	{
		var paths = new List<string>();
		XDocument workbook = Load(zip, "xl/workbook.xml");
		XDocument relations = Load(zip, "xl/_rels/workbook.xml.rels");
		if (workbook != null && relations != null)
		{
			Dictionary<string, string> targets = relations.Root.Elements(PackageRel + "Relationship")
				.ToDictionary(e => (string)e.Attribute("Id"), e => (string)e.Attribute("Target"));
			foreach (XElement sheet in workbook.Root.Element(Main + "sheets")?.Elements(Main + "sheet") ?? Enumerable.Empty<XElement>())
			{
				if (targets.TryGetValue((string)sheet.Attribute(Rel + "id") ?? string.Empty, out string target))
				{
					target = target.TrimStart('/');
					paths.Add(target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target);
				}
			}
		}

		if (paths.Count == 0)
		{
			paths.AddRange(zip.Entries.Select(e => e.FullName)
				.Where(n => n.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))
				.OrderBy(n => n, StringComparer.Ordinal));
		}

		return paths;
	}

	private static XDocument Load(ZipArchive zip, string name)
	{
		ZipArchiveEntry entry = zip.GetEntry(name);
		if (entry == null)
		{
			return null;
		}

		using Stream stream = entry.Open();
		return XDocument.Load(stream);
	}

	private static List<string> ReadSharedStrings(ZipArchive zip)
	{
		var strings = new List<string>();
		XDocument document = Load(zip, "xl/sharedStrings.xml");
		if (document == null)
		{
			return strings;
		}

		foreach (XElement si in document.Root.Elements(Main + "si"))
		{
			// Texto simple (<t>) o con formato (<r><t>…</t></r>); se ignoran las guías fonéticas (<rPh>).
			strings.Add(string.Concat(si.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value)));
		}

		return strings;
	}

	private static List<Dictionary<int, string>> ReadRows(ZipArchiveEntry entry, List<string> sharedStrings)
	{
		XDocument document;
		using (Stream stream = entry.Open())
		{
			document = XDocument.Load(stream);
		}

		var rows = new List<Dictionary<int, string>>();
		XElement data = document.Root.Element(Main + "sheetData");
		if (data == null)
		{
			return rows;
		}

		foreach (XElement row in data.Elements(Main + "row"))
		{
			var cells = new Dictionary<int, string>();
			int next = 0;
			foreach (XElement cell in row.Elements(Main + "c"))
			{
				int column = ColumnIndex((string)cell.Attribute("r"), next);
				next = column + 1;
				string type = (string)cell.Attribute("t");
				string value;
				if (type == "inlineStr")
				{
					value = string.Concat(cell.Descendants(Main + "t").Select(t => t.Value));
				}
				else
				{
					value = cell.Element(Main + "v")?.Value;
					if (type == "s" && int.TryParse(value, out int index) && index >= 0 && index < sharedStrings.Count)
					{
						value = sharedStrings[index];
					}
				}

				if (!string.IsNullOrEmpty(value))
				{
					cells[column] = value;
				}
			}

			rows.Add(cells);
		}

		return rows;
	}

	/// <summary>"C12" → 2 (base 0). Sin referencia, la celda sigue a la anterior.</summary>
	private static int ColumnIndex(string reference, int fallback)
	{
		if (string.IsNullOrEmpty(reference))
		{
			return fallback;
		}

		int column = 0;
		foreach (char c in reference)
		{
			if (c < 'A' || c > 'Z')
			{
				break;
			}

			column = column * 26 + (c - 'A' + 1);
		}

		return column == 0 ? fallback : column - 1;
	}
}
