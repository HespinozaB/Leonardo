using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitPartidas.Commands;

/// <summary>Una opción de un conflicto: su código, los elementos de referencia que ya la tienen y su color en la vista.</summary>
internal sealed class Review3DOption
{
	public string Code;

	public string Description;

	public List<long> ReferenceIds = new List<long>();

	public System.Drawing.Color Color;
}

/// <summary>
/// Vista 3D "EMASY - Revisión partidas": los elementos en conflicto en rojo y, en otro color por opción, elementos que ya tienen
/// cada partida candidata. Se aíslan temporalmente; al terminar se quitan los colores y el aislamiento.
/// </summary>
internal static class Review3D
{
	public const string ViewName = "EMASY - Revisión partidas";

	public static readonly System.Drawing.Color ConflictColor = System.Drawing.Color.FromArgb(220, 40, 40);

	public static readonly System.Drawing.Color[] OptionColors =
	{
		System.Drawing.Color.FromArgb(30, 110, 220),
		System.Drawing.Color.FromArgb(40, 170, 80),
		System.Drawing.Color.FromArgb(240, 150, 30),
		System.Drawing.Color.FromArgb(150, 70, 200)
	};

	/// <summary>Elementos con colores aplicados en la vista de revisión (para quitarlos después).</summary>
	private static readonly HashSet<long> Overridden = new HashSet<long>();

	public static string Show(UIDocument uiDoc, IList<long> conflictIds, IList<Review3DOption> options)
	{
		Document doc = uiDoc.Document;
		View3D view;
		using (var transaction = new Transaction(doc, "EMASY: revisión 3D de partidas"))
		{
			transaction.Start();
			view = GetOrCreateView(doc);
			ClearOverrides(doc, view);
			ElementId solid = SolidFill(doc);

			var all = new List<ElementId>();
			Paint(doc, view, conflictIds, ConflictColor, solid, all);
			foreach (Review3DOption option in options)
			{
				Paint(doc, view, option.ReferenceIds, option.Color, solid, all);
			}

			if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
			{
				view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
			}

			if (all.Count > 0)
			{
				view.IsolateElementsTemporary(all);
			}

			transaction.Commit();
		}

		try
		{
			uiDoc.ActiveView = view;
		}
		catch (Exception)
		{
			uiDoc.RequestViewChange(view);
		}

		var ids = conflictIds.Select(id => new ElementId(id)).Where(id => doc.GetElement(id) != null).ToList();
		uiDoc.Selection.SetElementIds(ids);
		if (ids.Count > 0)
		{
			try
			{
				uiDoc.ShowElements(ids);
			}
			catch (Exception)
			{
			}
		}

		return $"Vista 3D \"{ViewName}\": {conflictIds.Count} elemento(s) en conflicto en rojo y {options.Sum(o => o.ReferenceIds.Count)} de referencia.";
	}

	/// <summary>Pide un clic en el modelo y devuelve el elemento elegido (null si se cancela con Esc).</summary>
	public static long? Pick(UIDocument uiDoc)
	{
		try
		{
			Reference reference = uiDoc.Selection.PickObject(ObjectType.Element, "Haz clic en un elemento del color de la partida correcta (Esc para cancelar)");
			return reference?.ElementId.Value;
		}
		catch (Autodesk.Revit.Exceptions.OperationCanceledException)
		{
			return null;
		}
	}

	public static void Reset(Document doc)
	{
		View3D view = FindView(doc);
		if (view == null)
		{
			Overridden.Clear();
			return;
		}

		using var transaction = new Transaction(doc, "EMASY: quitar colores de revisión");
		transaction.Start();
		ClearOverrides(doc, view);
		if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
		{
			view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
		}

		transaction.Commit();
	}

	private static void Paint(Document doc, View view, IEnumerable<long> ids, System.Drawing.Color color, ElementId solid, List<ElementId> all)
	{
		var revitColor = new Color(color.R, color.G, color.B);
		var settings = new OverrideGraphicSettings();
		settings.SetSurfaceForegroundPatternColor(revitColor);
		settings.SetCutForegroundPatternColor(revitColor);
		if (solid != null)
		{
			settings.SetSurfaceForegroundPatternId(solid);
			settings.SetCutForegroundPatternId(solid);
		}

		foreach (long id in ids)
		{
			var elementId = new ElementId(id);
			if (doc.GetElement(elementId) == null)
			{
				continue;
			}

			try
			{
				view.SetElementOverrides(elementId, settings);
				Overridden.Add(id);
				all.Add(elementId);
			}
			catch (Exception)
			{
				// Elementos que la vista no admite (p. ej. de otra fase): se omiten.
			}
		}
	}

	private static void ClearOverrides(Document doc, View view)
	{
		var empty = new OverrideGraphicSettings();
		foreach (long id in Overridden)
		{
			var elementId = new ElementId(id);
			if (doc.GetElement(elementId) != null)
			{
				try
				{
					view.SetElementOverrides(elementId, empty);
				}
				catch (Exception)
				{
				}
			}
		}

		Overridden.Clear();
	}

	private static View3D FindView(Document doc) =>
		new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName);

	private static View3D GetOrCreateView(Document doc)
	{
		View3D view = FindView(doc);
		if (view != null)
		{
			return view;
		}

		ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
			.First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
		view = View3D.CreateIsometric(doc, type.Id);
		view.Name = ViewName;
		try
		{
			view.ViewTemplateId = ElementId.InvalidElementId;
			view.DisplayStyle = DisplayStyle.ShadingWithEdges;
			view.DetailLevel = ViewDetailLevel.Fine;
		}
		catch (Exception)
		{
		}

		return view;
	}

	private static ElementId SolidFill(Document doc) =>
		new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
			.FirstOrDefault(p => p.GetFillPattern().IsSolidFill)?.Id;
}
