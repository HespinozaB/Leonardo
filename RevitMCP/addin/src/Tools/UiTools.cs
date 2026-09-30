using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Bridge;
using static RevitMCP.Tools.RevitHelpers;

namespace RevitMCP.Tools;

internal static class UiTools
{
	public static object Select(UIApplication app, Args a)
	{
		UIDocument uidoc = ActiveUiDoc(app);
		List<ElementId> ids = a.Ids("ids").Select(Id).Where(id => uidoc.Document.GetElement(id) != null).ToList();
		uidoc.Selection.SetElementIds(ids);
		if (ids.Count > 0 && a.Bool("enfocar", true))
		{
			uidoc.ShowElements(ids);
		}
		return new Dictionary<string, object> { ["seleccionados"] = ids.Count };
	}

	public static object OpenView(UIApplication app, Args a)
	{
		UIDocument uidoc = ActiveUiDoc(app);
		View view = ResolveView(uidoc.Document, a.ReqStr("vista"));
		uidoc.RequestViewChange(view);
		return new Dictionary<string, object> { ["id"] = view.Id.Value, ["nombre"] = view.Name };
	}

	/// <summary>Exporta una vista a PNG y la devuelve en base64 para que Claude pueda "ver" el modelo.</summary>
	public static object CaptureView(UIApplication app, Args a)
	{
		UIDocument uidoc = ActiveUiDoc(app);
		Document doc = uidoc.Document;
		View view = a.Has("vista") ? ResolveView(doc, a.Str("vista")) : uidoc.ActiveView;
		if (!view.CanBePrinted)
		{
			throw new InvalidOperationException("La vista '" + view.Name + "' no se puede exportar como imagen.");
		}

		string folder = Path.Combine(Path.GetTempPath(), "RevitMCP");
		Directory.CreateDirectory(folder);
		string baseName = "captura_" + Guid.NewGuid().ToString("N");
		var options = new ImageExportOptions
		{
			ExportRange = ExportRange.SetOfViews,
			FilePath = Path.Combine(folder, baseName),
			FitDirection = FitDirectionType.Horizontal,
			HLRandWFViewsFileType = ImageFileType.PNG,
			ShadowViewsFileType = ImageFileType.PNG,
			ImageResolution = ImageResolution.DPI_150,
			ZoomType = ZoomFitType.FitToPage,
			PixelSize = Math.Max(256, Math.Min(4096, a.Int("ancho_px", 1600)))
		};
		options.SetViewsAndSheets(new List<ElementId> { view.Id });
		doc.ExportImage(options);

		// Revit añade el nombre de la vista al archivo: buscamos el generado.
		FileInfo file = new DirectoryInfo(folder).GetFiles(baseName + "*.png")
			.OrderByDescending(f => f.LastWriteTimeUtc)
			.FirstOrDefault()
			?? throw new InvalidOperationException("Revit no generó la imagen.");
		byte[] bytes = File.ReadAllBytes(file.FullName);
		try
		{
			file.Delete();
		}
		catch
		{
			// Archivo temporal: no es crítico.
		}
		return new Dictionary<string, object>
		{
			["vista"] = view.Name,
			["formato"] = "png",
			["base64"] = Convert.ToBase64String(bytes)
		};
	}

	private static View ResolveView(Document doc, string nameOrId)
	{
		List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
		if (long.TryParse(nameOrId, out long id))
		{
			View byId = views.FirstOrDefault(v => v.Id.Value == id);
			if (byId != null)
			{
				return byId;
			}
		}
		return views.FirstOrDefault(v => Matches(v.Name, nameOrId))
			?? views.FirstOrDefault(v => v is ViewSheet s && Matches(s.SheetNumber, nameOrId))
			?? views.FirstOrDefault(v => v.Name.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0)
			?? throw new ArgumentException("Vista no encontrada: '" + nameOrId + "'. Usa listar_vistas.");
	}
}
