using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RevitParamAudit.Tools;

namespace RevitParamAudit.Core;

/// <summary>Los cuatro depuradores, en el orden recomendado de uso.</summary>
internal enum ToolKind
{
	Sheets = 1,
	Views = 2,
	Filters = 3,
	Parameters = 4
}

/// <summary>Columna "Proceder a eliminar".</summary>
internal enum Verdict
{
	/// <summary>✓ Sin uso: se puede eliminar.</summary>
	Delete,

	/// <summary>⚠ Sin uso, pero con algo que conviene revisar antes de eliminar.</summary>
	Review,

	/// <summary>✗ En uso.</summary>
	Keep,

	/// <summary>— No aplica (no se puede eliminar desde aquí).</summary>
	NotApplicable
}

/// <summary>Símbolos de las columnas de estado: ✓ (verde), ✗ (rojo), ⚠ (ámbar), — (no aplica).</summary>
internal static class Marks
{
	public const string Yes = "✓";

	public const string No = "✗";

	public const string Alert = "⚠";

	public const string None = "—";

	public static string YesNo(bool value) => value ? Yes : No;

	public static string Of(Verdict verdict) => verdict switch
	{
		Verdict.Delete => Yes,
		Verdict.Review => Alert,
		Verdict.Keep => No,
		_ => None
	};

	/// <summary>"✓ 3: A, B, C" o "✗" si la lista está vacía.</summary>
	public static string List(ICollection<string> names) =>
		names.Count == 0 ? No : $"{Yes} {names.Count}: {string.Join(", ", names)}";
}

internal sealed class ColumnSpec
{
	public ColumnSpec(string name, int width, bool optional = false, bool hidden = false, bool center = false, bool numeric = false)
	{
		Name = name;
		Width = width;
		Optional = optional;
		HiddenByDefault = hidden;
		Center = center;
		Numeric = numeric;
	}

	public string Name { get; }

	public int Width { get; }

	/// <summary>Se puede mostrar u ocultar desde el menú "Columnas".</summary>
	public bool Optional { get; }

	public bool HiddenByDefault { get; }

	public bool Center { get; }

	public bool Numeric { get; }
}

internal sealed class FilterSpec
{
	public FilterSpec(string name, Func<DepRow, bool> predicate)
	{
		Name = name;
		Predicate = predicate;
	}

	public string Name { get; }

	public Func<DepRow, bool> Predicate { get; }
}

/// <summary>Una fila de la tabla de un depurador.</summary>
internal sealed class DepRow
{
	public long Id;

	/// <summary>Nombre corto para la confirmación de eliminar.</summary>
	public string Label = string.Empty;

	/// <summary>Un texto por columna (en el orden de <see cref="IDepurador.Columns"/>).</summary>
	public string[] Cells;

	public Verdict Verdict;

	/// <summary>No se puede marcar para eliminar (p. ej. parámetros globales).</summary>
	public bool Locked;

	/// <summary>Datos propios del depurador (para filtros y avisos).</summary>
	public object Tag;
}

/// <summary>Un depurador: qué columnas muestra, cómo lee el modelo y qué hacen sus botones.</summary>
internal interface IDepurador
{
	ToolKind Kind { get; }

	/// <summary>Título de la ventana.</summary>
	string Title { get; }

	/// <summary>Una línea de ayuda bajo la barra de búsqueda.</summary>
	string Guide { get; }

	/// <summary>Plural en minúsculas para los mensajes ("planos", "vistas"…).</summary>
	string Noun { get; }

	ColumnSpec[] Columns { get; }

	FilterSpec[] Filters { get; }

	bool CanLocate { get; }

	bool CanSelect { get; }

	/// <summary>Muestra la opción "Análisis profundo" en el menú de columnas.</summary>
	bool HasDeepOption { get; }

	List<DepRow> Collect(UIDocument uiDoc, bool deep, List<string> notes);

	/// <summary>Líneas extra para la confirmación de eliminar.</summary>
	IEnumerable<string> DeleteNotes(List<DepRow> rows);

	string Delete(UIDocument uiDoc, List<long> ids);

	string Locate(UIDocument uiDoc, List<long> ids);

	string Select(UIDocument uiDoc, List<long> ids);
}

internal static class DepuradorTools
{
	public static IDepurador Create(ToolKind kind) => kind switch
	{
		ToolKind.Sheets => new SheetsDepurador(),
		ToolKind.Views => new ViewsDepurador(),
		ToolKind.Filters => new FiltersDepurador(),
		_ => new ParametersDepurador()
	};

	public static ToolKind? Next(ToolKind kind) => kind == ToolKind.Parameters ? null : (ToolKind)((int)kind + 1);

	public static string ShortName(ToolKind kind) => kind switch
	{
		ToolKind.Sheets => "1. Planos",
		ToolKind.Views => "2. Vistas",
		ToolKind.Filters => "3. Filtros",
		_ => "4. Parámetros"
	};
}
