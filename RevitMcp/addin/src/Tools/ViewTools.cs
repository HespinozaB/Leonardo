using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Tools;

internal static class ViewTools
{
	public static object SetSelection(UIApplication app, JsonObject args)
	{
		UIDocument uidoc = RevitUtil.UiDoc(app);
		var ids = Args.Ids(args, "ids").Where(id => uidoc.Document.GetElement(id) != null).ToList();
		uidoc.Selection.SetElementIds(ids);
		if (ids.Count > 0 && Args.Bool(args, "zoom", true))
		{
			uidoc.ShowElements(ids);
		}

		return new { selected = ids.Count };
	}

	public static object ActivateView(UIApplication app, JsonObject args)
	{
		UIDocument uidoc = RevitUtil.UiDoc(app);
		if (RevitUtil.Get(uidoc.Document, Args.RequiredId(args, "viewId")) is not View view || view.IsTemplate)
		{
			throw new ArgumentException("El id no corresponde a una vista abrible.");
		}

		uidoc.ActiveView = view;
		return new { activeView = view.Name };
	}

	/// <summary>Exporta una vista o plano a PNG y lo devuelve en base64 para que Claude lo vea.</summary>
	public static object ExportViewImage(UIApplication app, JsonObject args)
	{
		UIDocument uidoc = RevitUtil.UiDoc(app);
		Document doc = uidoc.Document;
		ElementId viewId = Args.Id(args, "viewId") ?? uidoc.ActiveView.Id;
		if (RevitUtil.Get(doc, viewId) is not View view || !view.CanBePrinted)
		{
			throw new ArgumentException("La vista no se puede exportar como imagen.");
		}

		string folder = Path.Combine(Path.GetTempPath(), "RevitMcp", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			var options = new ImageExportOptions
			{
				ExportRange = ExportRange.SetOfViews,
				FilePath = Path.Combine(folder, "vista"),
				HLRandWFViewsFileType = ImageFileType.PNG,
				ShadowViewsFileType = ImageFileType.PNG,
				ImageResolution = ImageResolution.DPI_150,
				ZoomType = ZoomFitType.FitToPage,
				FitDirection = FitDirectionType.Horizontal,
				PixelSize = Math.Clamp(Args.Int(args, "pixelSize", 1600), 256, 4096)
			};
			options.SetViewsAndSheets(new[] { viewId });
			doc.ExportImage(options);

			string file = Directory.GetFiles(folder).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
				?? throw new InvalidOperationException("Revit no generó la imagen.");

			string savePath = Args.Str(args, "savePath");
			if (!string.IsNullOrWhiteSpace(savePath))
			{
				File.Copy(file, savePath, true);
			}

			return new
			{
				view = view.Name,
				imageBase64 = Convert.ToBase64String(File.ReadAllBytes(file)),
				mimeType = "image/png",
				savedTo = string.IsNullOrWhiteSpace(savePath) ? null : savePath
			};
		}
		finally
		{
			try
			{
				Directory.Delete(folder, true);
			}
			catch (Exception)
			{
			}
		}
	}
}
