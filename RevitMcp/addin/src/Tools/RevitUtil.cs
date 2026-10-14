using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Tools;

internal static class RevitUtil
{
	public static UIDocument UiDoc(UIApplication app) =>
		app.ActiveUIDocument ?? throw new InvalidOperationException("No hay ningún documento abierto en Revit.");

	public static Document Doc(UIApplication app) => UiDoc(app).Document;

	public static Element Get(Document doc, ElementId id) =>
		doc.GetElement(id) ?? throw new ArgumentException($"No existe el elemento {id.Value}.");

	/// <summary>
	/// Categoría por BuiltInCategory ("OST_Walls", "Walls") o por su nombre visible (localizado:
	/// "Muros").
	/// </summary>
	public static Category FindCategory(Document doc, string name)
	{
		string trimmed = name.Trim();
		foreach (string candidate in new[] { trimmed, "OST_" + trimmed })
		{
			if (!candidate.All(char.IsDigit) && !candidate.StartsWith("-")
				&& Enum.TryParse(candidate, true, out BuiltInCategory bic) && bic != BuiltInCategory.INVALID)
			{
				Category category = Category.GetCategory(doc, bic);
				if (category != null)
				{
					return category;
				}
			}
		}

		foreach (Category category in doc.Settings.Categories)
		{
			if (string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase))
			{
				return category;
			}
		}

		throw new ArgumentException($"Categoría no encontrada: {name}. Usa el nombre visible (p. ej. \"Muros\") o un BuiltInCategory (p. ej. \"OST_Walls\").");
	}

	/// <summary>Clase de la API por nombre corto ("Wall", "FamilyInstance", "ViewSheet").</summary>
	public static Type FindApiClass(string name)
	{
		Type type = typeof(Element).Assembly.GetType("Autodesk.Revit.DB." + name.Trim(), false, true)
			?? typeof(Element).Assembly.GetTypes().FirstOrDefault(t =>
				string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) && typeof(Element).IsAssignableFrom(t));
		return type ?? throw new ArgumentException($"Clase de la API no encontrada: {name}.");
	}

	public static Level FindLevel(Document doc, string nameOrId, View fallbackView = null)
	{
		List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
		if (levels.Count == 0)
		{
			throw new InvalidOperationException("El documento no tiene niveles.");
		}

		if (string.IsNullOrWhiteSpace(nameOrId))
		{
			return fallbackView?.GenLevel ?? levels[0];
		}

		if (long.TryParse(nameOrId, out long id) && doc.GetElement(new ElementId(id)) is Level byId)
		{
			return byId;
		}

		return levels.FirstOrDefault(l => string.Equals(l.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"Nivel no encontrado: {nameOrId}. Disponibles: {string.Join(", ", levels.Select(l => l.Name))}.");
	}

	/// <summary>Tipo de una clase concreta (WallType, FloorType...) por nombre o id; si no se indica, el predeterminado.</summary>
	public static T FindType<T>(Document doc, string nameOrId, ElementTypeGroup defaultGroup) where T : ElementType
	{
		if (string.IsNullOrWhiteSpace(nameOrId))
		{
			return doc.GetElement(doc.GetDefaultElementTypeId(defaultGroup)) as T
				?? new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault()
				?? throw new InvalidOperationException($"No hay tipos {typeof(T).Name} en el documento.");
		}

		if (long.TryParse(nameOrId, out long id) && doc.GetElement(new ElementId(id)) is T byId)
		{
			return byId;
		}

		List<T> all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
		return all.FirstOrDefault(t => string.Equals(t.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"Tipo no encontrado: {nameOrId}. Disponibles: {string.Join(", ", all.Select(t => t.Name).Take(40))}.");
	}

	/// <summary>Tipo de familia por id, "Familia : Tipo" o nombre del tipo (opcionalmente dentro de una categoría).</summary>
	public static FamilySymbol FindSymbol(Document doc, string nameOrId, string category = null)
	{
		if (long.TryParse(nameOrId, out long id) && doc.GetElement(new ElementId(id)) is FamilySymbol byId)
		{
			return byId;
		}

		var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
		if (!string.IsNullOrWhiteSpace(category))
		{
			collector = collector.OfCategoryId(FindCategory(doc, category).Id);
		}

		List<FamilySymbol> symbols = collector.Cast<FamilySymbol>().ToList();
		string wanted = nameOrId.Trim();
		string[] parts = wanted.Split(':', 2);
		FamilySymbol match = parts.Length == 2
			? symbols.FirstOrDefault(s => Eq(s.FamilyName, parts[0]) && Eq(s.Name, parts[1]))
			: symbols.FirstOrDefault(s => Eq(s.Name, wanted)) ?? symbols.FirstOrDefault(s => Eq(s.FamilyName, wanted));

		return match ?? throw new ArgumentException(
			$"Tipo de familia no encontrado: {nameOrId}. Usa list_family_types para ver los disponibles.");

		static bool Eq(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Ejecuta <paramref name="action"/> en una transacción (deshacible con Ctrl+Z). Las advertencias
	/// se descartan y se devuelven; un error deshace la transacción en lugar de abrir un diálogo que
	/// bloquearía Revit.
	/// </summary>
	public static T InTransaction<T>(Document doc, string name, Func<T> action, out List<string> warnings)
	{
		var preprocessor = new CollectingFailures();
		using var transaction = new Transaction(doc, "MCP: " + name);
		FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
		options.SetFailuresPreprocessor(preprocessor);
		options.SetClearAfterRollback(true);
		transaction.SetFailureHandlingOptions(options);

		transaction.Start();
		T result;
		try
		{
			result = action();
		}
		catch
		{
			if (transaction.HasStarted())
			{
				transaction.RollBack();
			}

			throw;
		}

		TransactionStatus status = transaction.Commit();
		warnings = preprocessor.Messages;
		if (status != TransactionStatus.Committed)
		{
			throw new InvalidOperationException("Revit deshizo la operación: " +
				(warnings.Count > 0 ? string.Join(" | ", warnings) : status.ToString()));
		}

		return result;
	}

	private sealed class CollectingFailures : IFailuresPreprocessor
	{
		public List<string> Messages { get; } = new();

		public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
		{
			bool hasErrors = false;
			foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
			{
				Messages.Add(failure.GetDescriptionText());
				if (failure.GetSeverity() == FailureSeverity.Warning)
				{
					accessor.DeleteWarning(failure);
				}
				else
				{
					hasErrors = true;
				}
			}

			return hasErrors ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
		}
	}
}
