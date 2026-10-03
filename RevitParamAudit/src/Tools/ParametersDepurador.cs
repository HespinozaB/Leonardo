using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitParamAudit.Core;

namespace RevitParamAudit.Tools;

/// <summary>Paso 4: parámetros activos en planos, en tablas y los que se pueden eliminar.</summary>
internal sealed class ParametersDepurador : IDepurador
{
	private const string WithInfo = "Con información";

	private const string Empty = "Vacío";

	public ToolKind Kind => ToolKind.Parameters;

	public string Title => "4. Depurador de parámetros sin uso";

	public string Guide => "Paso 4 de 4 · Parámetros activos en planos y tablas. Los que tienen información o vista 3D salen con alerta.";

	public string Noun => "parámetros";

	public bool CanLocate => false;

	public bool CanSelect => false;

	public bool HasDeepOption => true;

	public ColumnSpec[] Columns { get; } =
	{
		new ColumnSpec("Parámetro", 240),
		new ColumnSpec("Activo en plano", 105, center: true),
		new ColumnSpec("Activo en tabla", 105, center: true),
		new ColumnSpec("Proceder a eliminar", 125, center: true),
		new ColumnSpec("Valores", 115),
		new ColumnSpec("Origen", 85, optional: true, hidden: true),
		new ColumnSpec("Tipo de dato", 110, optional: true, hidden: true),
		new ColumnSpec("Grupo", 130, optional: true, hidden: true),
		new ColumnSpec("Vínculo", 85, optional: true, hidden: true),
		new ColumnSpec("Categorías", 260, optional: true, hidden: true),
		new ColumnSpec("Detalle en planos", 280, optional: true),
		new ColumnSpec("Detalle en tablas", 280, optional: true),
		new ColumnSpec("Advertencias", 340, optional: true),
		new ColumnSpec("GUID", 250, optional: true, hidden: true),
		new ColumnSpec("Id", 80, optional: true, hidden: true, numeric: true)
	};

	public FilterSpec[] Filters { get; } =
	{
		new FilterSpec("Todos", r => true),
		new FilterSpec(Marks.Yes + " Proceder a eliminar (vacíos, sin uso)", r => r.Verdict == Verdict.Delete),
		new FilterSpec(Marks.Alert + " Revisar antes (sin uso, con alertas)", r => r.Verdict == Verdict.Review),
		new FilterSpec(Marks.No + " En uso (plano o tabla)", r => r.Verdict == Verdict.Keep),
		new FilterSpec("Activos en plano", r => Entry(r).InSheets),
		new FilterSpec("Activos en tabla", r => Entry(r).InSchedules),
		new FilterSpec("Con información", r => !Entry(r).IsGlobal && Entry(r).HasValues),
		new FilterSpec("Vacíos", r => !Entry(r).IsGlobal && !Entry(r).HasValues),
		new FilterSpec("En vistas 3D", r => Entry(r).In3D)
	};

	private static ParamEntry Entry(DepRow row) => (ParamEntry)row.Tag;

	public List<DepRow> Collect(UIDocument uiDoc, bool deep, List<string> notes)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		AuditResult result = ParameterAuditor.Run(uiDoc.Document, deep);
		notes.AddRange(result.Notes);
		notes.Add($"analizado en {watch.Elapsed.TotalSeconds:0.0} s");
		return result.Entries.Select(MakeRow).ToList();
	}

	private static DepRow MakeRow(ParamEntry e) => new DepRow
	{
		Id = e.Id,
		Label = e.Name,
		Verdict = e.Verdict,
		Locked = e.IsGlobal,
		Tag = e,
		Cells = new[]
		{
			e.Name,
			e.IsGlobal ? Marks.None : Marks.YesNo(e.InSheets),
			e.IsGlobal ? Marks.None : Marks.YesNo(e.InSchedules),
			Marks.Of(e.Verdict),
			e.IsGlobal ? Marks.None : e.HasValues ? WithInfo : Empty,
			e.Scope,
			e.DataType,
			e.Group,
			e.Binding,
			e.Categories,
			e.SheetsText,
			e.SchedulesText,
			e.IsGlobal ? "Parámetro global: no se evalúa ni se elimina desde aquí" : e.WarningsText,
			e.Guid,
			e.Id.ToString()
		}
	};

	public IEnumerable<string> DeleteNotes(List<DepRow> rows)
	{
		int withValues = rows.Count(r => Entry(r).HasValues);
		if (withValues > 0)
		{
			yield return $"{withValues} tienen información escrita en elementos: se perderá.";
		}

		int in3D = rows.Count(r => Entry(r).In3D);
		if (in3D > 0)
		{
			yield return $"{in3D} aparecen en vistas 3D.";
		}

		int families = rows.Count(r => Entry(r).Binding == ParamEntry.FamiliesOnly);
		if (families > 0)
		{
			yield return $"{families} vienen de familias: reaparecen si se vuelven a cargar esas familias.";
		}
	}

	/// <summary>Elimina los parámetros elegidos (la decisión es del usuario; los globales nunca se borran).</summary>
	public string Delete(UIDocument uiDoc, List<long> ids) =>
		RevitUtil.DeleteElements(uiDoc.Document, ids, "EMASY: eliminar parámetros", "parámetro(s)",
			e => e is ParameterElement && !(e is GlobalParameter) ? null : "no es un parámetro de proyecto o compartido",
			DeleteParameter);

	private static bool DeleteParameter(Document doc, Element element)
	{
		Definition definition = ((ParameterElement)element).GetDefinition();
		try
		{
			return doc.Delete(element.Id).Count > 0;
		}
		catch (System.Exception)
		{
			// Algunos parámetros de proyecto solo se pueden quitar retirando su enlace a categorías.
			return definition != null && doc.ParameterBindings.Remove(definition);
		}
	}

	public string Locate(UIDocument uiDoc, List<long> ids) => null;

	public string Select(UIDocument uiDoc, List<long> ids) => null;
}
