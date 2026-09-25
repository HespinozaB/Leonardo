using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using View = Autodesk.Revit.DB.View;

namespace RevitDwgExploder.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ExplodeDwgCommand : IExternalCommand
{
	/// <summary>Mínimo de respaldo (0.4 mm) si no se puede averiguar el mínimo real que acepta Revit.</summary>
	private const double FallbackMinTextSizeFeet = 1.0 / 768.0;

	private const double MaxTextSizeFeet = 1.0;

	/// <summary>
	/// Corrección de altura del texto: a igual "Tamaño de texto", las letras de Revit (Arial) salen algo
	/// más grandes que las del DWG, así que se reduce un 10 % para que quepan en sus recuadros.
	/// </summary>
	private const double TextHeightFactor = 0.9;

	private const string DefaultFont = "Arial";

	/// <summary>Cantidad de curvas por llamada a NewDetailCurveArray.</summary>
	private const int CurveBatchSize = 500;

	/// <summary>Redondeo (en pies) usado para detectar segmentos duplicados.</summary>
	private const double DedupTolerance = 1E-05;

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		UIDocument uiDoc = commandData.Application.ActiveUIDocument;
		Document doc = uiDoc.Document;
		View view = uiDoc.ActiveView;

		if (!ExplodeDwgAvailability.SupportsDetailCurves(view))
		{
			TaskDialog.Show("Explotar DWGs", "La vista activa no admite líneas de detalle (por ejemplo vistas 3D, plantillas o tablas). Abre una planta, alzado, sección o vista de dibujo.");
			return Result.Cancelled;
		}

		List<ImportInstance> targets = GetSelectedImportInstances(uiDoc);
		if (targets.Count == 0)
		{
			targets = GetImportInstancesInView(doc, view);
		}

		if (targets.Count == 0)
		{
			TaskDialog.Show("Explotar DWGs", "No se encontraron instancias de DWG importado/vinculado en la selección actual ni en la vista activa.");
			return Result.Cancelled;
		}

		double minLength = commandData.Application.Application.ShortCurveTolerance * 1.01;
		Dictionary<ElementId, string> manualPaths = AskForManualDwgPaths(doc, targets);
		var stats = new Stats();

		// 1) Leer textos (fuera de la transacción principal: exportar requiere documento no modificable).
		var textsByInstance = new Dictionary<ElementId, (List<DwgTextImporter.DwgTextEntry> Texts, bool IsOcr)>();
		var fileCache = new DwgTextImporter.FileCache();
		using (var roundTrip = new DwgRoundTripTextExtractor(doc, view))
		{
			foreach (ImportInstance instance in targets)
			{
				manualPaths.TryGetValue(instance.Id, out string path);
				DwgTextImporter.ReadStatus status = DwgTextImporter.TryReadTexts(doc, instance, fileCache, out List<DwgTextImporter.DwgTextEntry> texts, path);
				switch (status)
				{
					case DwgTextImporter.ReadStatus.NotLinked:
						stats.NotLinked++;
						break;
					case DwgTextImporter.ReadStatus.FileNotFound:
					case DwgTextImporter.ReadStatus.ReadError:
						stats.Unreadable++;
						break;
				}

				// Si los textos leídos del DWG no caen sobre el CAD (unidades o escala de inserción distintas),
				// se descartan y se usa la reexportación, que ya viene a la escala real del modelo.
				if (status == DwgTextImporter.ReadStatus.Ok && texts.Count > 0 && FractionInside(texts, instance, view) >= 0.5)
				{
					textsByInstance[instance.Id] = (texts, false);
					continue;
				}

				List<DwgTextImporter.DwgTextEntry> roundTripTexts = roundTrip.Extract(instance);
				if (roundTripTexts.Count > 0)
				{
					textsByInstance[instance.Id] = (roundTripTexts, false);
					continue;
				}

				textsByInstance[instance.Id] = (DwgTextImageOcr.Recognize(doc, view, instance), true);
			}
		}

		// 2) Crear la geometría y los textos nativos en una sola transacción (un solo "deshacer").
		var createdTextKeys = new HashSet<string>(StringComparer.Ordinal);
		using (var tx = new Transaction(doc, "Explotar DWGs a Detail Lines"))
		{
			FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
			failureOptions.SetFailuresPreprocessor(new WarningSwallower());
			failureOptions.SetClearAfterRollback(true);
			tx.SetFailureHandlingOptions(failureOptions);
			tx.Start();

			// Textos del DWG más pequeños que lo que Revit permite a la escala de la vista: se ofrece
			// ajustar la escala de la vista para que conserven su tamaño relativo al dibujo.
			double minTextSize = TextNoteTypeCache.ProbeMinimumTextSize(doc);
			AdjustViewScaleIfNeeded(view, textsByInstance.Values.SelectMany(v => v.Texts), minTextSize, stats);
			var textTypeCache = new TextNoteTypeCache(doc, view.Scale, minTextSize);

			var lineStyles = new LineStyleMapper(doc);
			var geometryOptions = new Options
			{
				View = view,
				ComputeReferences = false,
				IncludeNonVisibleObjects = false
			};

			foreach (ImportInstance instance in targets)
			{
				var (texts, isOcr) = textsByInstance[instance.Id];
				foreach (DwgTextImporter.DwgTextEntry entry in texts)
				{
					if (!createdTextKeys.Add(TextKey(entry)))
					{
						continue;
					}

					if (textTypeCache.IsBelowMinimum(entry.HeightFeet))
					{
						stats.ClampedTexts++;
					}

					if (CreateTextNote(doc, view, entry, textTypeCache))
					{
						if (isOcr)
						{
							stats.OcrTexts++;
						}
						else
						{
							stats.ExactTexts++;
						}
					}
				}

				var curves = new List<(Curve Curve, ElementId StyleId)>();
				GeometryElement geometry = instance.get_Geometry(geometryOptions);
				if (geometry != null)
				{
					var seen = new HashSet<(long, long, long, long, long, long, long)>();
					CollectCurves(geometry, curves, seen, minLength, ref stats.Skipped);
				}

				DwgLinetypeIndex linetypes = null;
				DwgTextImporter.FileCache.Entry source = fileCache.ForInstance(instance.Id);
				if (source != null)
				{
					linetypes = DwgLinetypeIndex.Build(source.Cad, source.FeetPerUnit, instance.GetTransform(), view.Scale);
				}

				CreateDetailCurves(doc, view, curves, lineStyles, linetypes, stats);
				stats.Processed++;
			}

			stats.CreatedStyles = lineStyles.CreatedStyles;
			stats.CreatedPatterns = lineStyles.CreatedPatterns;
			if (tx.Commit() != TransactionStatus.Committed)
			{
				message = "No se pudo confirmar la transacción.";
				return Result.Failed;
			}
		}

		ShowSummary(stats);
		return Result.Succeeded;
	}

	private sealed class Stats
	{
		public int NotLinked;
		public int Unreadable;
		public int Processed;
		public int Lines;
		public int Skipped;
		public int ExactTexts;
		public int OcrTexts;
		public int CreatedStyles;
		public int CreatedPatterns;
		public int PatternedLines;
		public int OldScale;
		public int NewScale;
		public int ClampedTexts;
	}

	private static void ShowSummary(Stats s)
	{
		string notLinked = s.NotLinked > 0
			? $"\n{s.NotLinked} DWG estaban importados (no vinculados) o sin archivo indicado: para esos el texto se obtuvo reexportando la vista a DWG, o por OCR si eso tampoco dio resultado."
			: string.Empty;
		string unreadable = s.Unreadable > 0
			? $"\n{s.Unreadable} DWG no se pudieron releer desde su archivo original (movido/no encontrado, o formato no soportado)."
			: string.Empty;
		string styles = s.CreatedStyles > 0
			? $"\nLine Styles creados a partir de capas del DWG: {s.CreatedStyles} (prefijo \"DWG-\")."
			: string.Empty;
		if (s.CreatedPatterns > 0 || s.PatternedLines > 0)
		{
			styles += $"\nLíneas con tipo de línea leído del DWG original: {s.PatternedLines} (patrones de línea creados: {s.CreatedPatterns}).";
		}
		if (s.NewScale > 0)
		{
			styles += $"\nEscala de la vista cambiada de 1:{s.OldScale} a 1:{s.NewScale} para que los textos conserven el tamaño del DWG.";
		}

		if (s.ClampedTexts > 0)
		{
			styles += $"\n{s.ClampedTexts} texto(s) quedaron algo más grandes que en el DWG porque Revit no admite un tamaño menor a esta escala de vista.";
		}

		string ocr = s.OcrTexts > 0
			? "\n\nOjo: hubo textos creados por OCR (reconocimiento aproximado). Conviene verificarlos."
			: string.Empty;

		TaskDialog.Show("Explotar DWGs — resumen",
			$"DWGs procesados: {s.Processed}\n" +
			$"Detail Lines creadas: {s.Lines}\n" +
			$"Segmentos omitidos: {s.Skipped}\n" +
			$"TextNotes con texto exacto: {s.ExactTexts}\n" +
			$"TextNotes por OCR (aproximados): {s.OcrTexts}" +
			styles + notLinked + unreadable +
			"\n\nLos DWG originales no se modificaron ni se eliminaron." + ocr);
	}

	// ---------------------------------------------------------------- Textos

	private static bool CreateTextNote(Document doc, View view, DwgTextImporter.DwgTextEntry entry, TextNoteTypeCache types)
	{
		if (string.IsNullOrWhiteSpace(entry.Text) || entry.Position == null)
		{
			return false;
		}

		var options = new TextNoteOptions(types.Get(entry.HeightFeet, entry.WidthFactor, entry.FontName, entry.Bold))
		{
			HorizontalAlignment = entry.AnchorH switch
			{
				TextAnchorH.Center => HorizontalTextAlignment.Center,
				TextAnchorH.Right => HorizontalTextAlignment.Right,
				_ => HorizontalTextAlignment.Left
			},
			VerticalAlignment = entry.AnchorV switch
			{
				TextAnchorV.Top => VerticalTextAlignment.Top,
				TextAnchorV.Middle => VerticalTextAlignment.Middle,
				_ => VerticalTextAlignment.Bottom
			},
			Rotation = NormalizeAngle(entry.RotationRadians),
			KeepRotatedTextReadable = false
		};

		try
		{
			TextNote.Create(doc, view.Id, entry.Position, entry.Text, options);
			return true;
		}
		catch (Autodesk.Revit.Exceptions.ArgumentException)
		{
			return false;
		}
		catch (Autodesk.Revit.Exceptions.InvalidOperationException)
		{
			return false;
		}
	}

	private static double NormalizeAngle(double angle)
	{
		if (Math.Abs(angle) < 1E-09 || double.IsNaN(angle))
		{
			return 0.0;
		}

		const double twoPi = 2.0 * Math.PI;
		angle %= twoPi;
		return angle < 0.0 ? angle + twoPi : angle;
	}

	private static string TextKey(DwgTextImporter.DwgTextEntry e)
	{
		return e.Text + "|" + Math.Round(e.Position.X / 0.01) + "|" + Math.Round(e.Position.Y / 0.01);
	}

	/// <summary>Fracción de textos que caen dentro (con margen) de la caja del CAD en la vista.</summary>
	private static double FractionInside(List<DwgTextImporter.DwgTextEntry> texts, ImportInstance instance, View view)
	{
		BoundingBoxXYZ bbox = instance.get_BoundingBox(view);
		if (bbox == null || texts.Count == 0)
		{
			return 1.0;
		}

		double marginX = (bbox.Max.X - bbox.Min.X) * 0.1;
		double marginY = (bbox.Max.Y - bbox.Min.Y) * 0.1;
		int inside = texts.Count(t => t.Position != null
			&& t.Position.X >= bbox.Min.X - marginX && t.Position.X <= bbox.Max.X + marginX
			&& t.Position.Y >= bbox.Min.Y - marginY && t.Position.Y <= bbox.Max.Y + marginY);
		return (double)inside / texts.Count;
	}

	/// <summary>
	/// Si una parte importante de los textos del DWG quedaría por debajo del tamaño mínimo que admite
	/// Revit a la escala actual de la vista (DWG pequeño o insertado a escala reducida), propone cambiar
	/// la escala de la vista a una en la que todos conserven su tamaño relativo al dibujo.
	/// </summary>
	private static void AdjustViewScaleIfNeeded(View view, IEnumerable<DwgTextImporter.DwgTextEntry> allTexts, double minTextSize, Stats stats)
	{
		List<double> heights = allTexts.Where(t => t.HeightFeet > 1E-06).Select(t => t.HeightFeet * TextHeightFactor).OrderBy(h => h).ToList();
		if (heights.Count == 0)
		{
			return;
		}

		int scale = Math.Max(view.Scale, 1);
		int below = heights.Count(h => h / scale < minTextSize * 0.95);
		if (below < Math.Max(1, heights.Count / 10))
		{
			return;
		}

		// Escala más grande (1:N) con la que el 95 % de los textos queda por encima del mínimo.
		double reference = heights[Math.Min(heights.Count - 1, (int)Math.Floor(heights.Count * 0.05))];
		int suggested = Math.Max(1, (int)Math.Floor(reference / minTextSize));
		if (suggested >= scale)
		{
			return;
		}

		bool canChange;
		try
		{
			Parameter scaleParam = view.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC) ?? view.get_Parameter(BuiltInParameter.VIEW_SCALE);
			canChange = scaleParam == null || !scaleParam.IsReadOnly;
		}
		catch (Exception)
		{
			canChange = false;
		}

		if (!canChange)
		{
			return;
		}

		var dialog = new TaskDialog("Explotar DWGs")
		{
			MainInstruction = "Los textos del DWG son muy pequeños para la escala de esta vista",
			MainContent = $"{below} de {heights.Count} texto(s) del DWG necesitarían un tamaño menor al mínimo que admite Revit " +
				$"({minTextSize * 304.8:0.##} mm) a la escala actual 1:{scale}, así que saldrían más grandes que en el DWG " +
				"(por ejemplo, desbordando sus recuadros).\n\n" +
				$"Si cambias la escala de la vista a 1:{suggested}, los textos se crearán con el mismo tamaño relativo al dibujo " +
				"que en el DWG. Las líneas no cambian de tamaño; solo cambia el tamaño de las anotaciones de la vista.",
			CommonButtons = TaskDialogCommonButtons.None
		};
		dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Cambiar la escala de la vista a 1:{suggested} (recomendado)");
		dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, $"Mantener 1:{scale}");
		if (dialog.Show() != TaskDialogResult.CommandLink1)
		{
			return;
		}

		try
		{
			view.Scale = suggested;
			stats.OldScale = scale;
			stats.NewScale = suggested;
		}
		catch (Exception)
		{
		}
	}

	/// <summary>
	/// Busca o crea tipos de TextNote "DWG x mm" una sola vez por combinación de tamaño, anchura y fuente
	/// (el colector de tipos existentes se ejecuta una sola vez). Todos quedan con fondo transparente,
	/// sin borde y con desfase de directriz/borde 0, para que no tapen las líneas ni agranden el recuadro.
	/// </summary>
	private sealed class TextNoteTypeCache
	{
		private readonly Document _doc;

		private readonly int _viewScale;

		private readonly double _minTextSize;

		private readonly Dictionary<(int, int, string, bool), ElementId> _byKey = new Dictionary<(int, int, string, bool), ElementId>();

		private Dictionary<string, TextNoteType> _existingByName;

		public TextNoteTypeCache(Document doc, int viewScale, double minTextSize)
		{
			_doc = doc;
			_viewScale = Math.Max(viewScale, 1);
			_minTextSize = minTextSize > 0.0 ? minTextSize : FallbackMinTextSizeFeet;
		}

		public bool IsBelowMinimum(double modelHeightFeet) => modelHeightFeet * TextHeightFactor / _viewScale < _minTextSize * 0.95;

		/// <summary>
		/// Averigua el tamaño de texto más pequeño que Revit acepta en un tipo de texto, probando sobre un
		/// tipo temporal dentro de una subtransacción que luego se deshace.
		/// </summary>
		public static double ProbeMinimumTextSize(Document doc)
		{
			try
			{
				if (!(doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) is TextNoteType baseType))
				{
					baseType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
				}

				if (baseType == null)
				{
					return FallbackMinTextSizeFeet;
				}

				using var sub = new SubTransaction(doc);
				sub.Start();
				try
				{
					var probe = baseType.Duplicate("DWG probe " + Guid.NewGuid().ToString("N")) as TextNoteType;
					Parameter size = probe?.get_Parameter(BuiltInParameter.TEXT_SIZE);
					if (size == null)
					{
						return FallbackMinTextSizeFeet;
					}

					// 0.02 mm … 0.4 mm
					foreach (double mm in new[] { 0.02, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4 })
					{
						double feet = mm / 304.8;
						try
						{
							if (size.Set(feet) && Math.Abs(size.AsDouble() - feet) < feet * 0.01)
							{
								return feet;
							}
						}
						catch (Exception)
						{
						}
					}
				}
				finally
				{
					sub.RollBack();
				}
			}
			catch (Exception)
			{
			}

			return FallbackMinTextSizeFeet;
		}

		public ElementId Get(double modelHeightFeet, double widthFactor, string fontName, bool bold)
		{
			double paperHeight = modelHeightFeet * TextHeightFactor / _viewScale;
			paperHeight = Math.Max(_minTextSize, Math.Min(MaxTextSizeFeet, paperHeight));
			double mm = paperHeight * 304.8;
			widthFactor = Math.Round(Math.Max(0.01, Math.Min(10.0, widthFactor > 0.0 ? widthFactor : 1.0)), 2);
			string font = string.IsNullOrWhiteSpace(fontName) ? DefaultFont : fontName;

			// Resolución de 0.01 mm en el tamaño (antes 0.05 mm, que en textos pequeños era ~10 % de error).
			var key = ((int)Math.Round(mm * 100.0), (int)Math.Round(widthFactor * 100.0), font, bold);
			if (_byKey.TryGetValue(key, out ElementId id))
			{
				return id;
			}

			string typeName = $"DWG {mm:0.00} mm";
			if (Math.Abs(widthFactor - 1.0) > 0.005)
			{
				typeName += $" x{widthFactor:0.00}";
			}

			if (!font.Equals(DefaultFont, StringComparison.OrdinalIgnoreCase))
			{
				typeName += " " + font;
			}

			if (bold)
			{
				typeName += " Negrita";
			}

			id = FindOrCreate(typeName, paperHeight, widthFactor, font, bold);
			_byKey[key] = id;
			return id;
		}

		private ElementId FindOrCreate(string typeName, double paperHeight, double widthFactor, string font, bool bold)
		{
			_existingByName ??= new FilteredElementCollector(_doc)
				.OfClass(typeof(TextNoteType))
				.Cast<TextNoteType>()
				.GroupBy(t => t.Name)
				.ToDictionary(g => g.Key, g => g.First());

			if (_existingByName.TryGetValue(typeName, out TextNoteType existing))
			{
				// Tipo creado por una versión anterior del addin: se corrige su configuración.
				Configure(existing, paperHeight, widthFactor, font, bold);
				return existing.Id;
			}

			ElementId defaultTypeId = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
			if (!(_doc.GetElement(defaultTypeId) is TextNoteType defaultType))
			{
				defaultType = _existingByName.Values.FirstOrDefault();
				if (defaultType == null)
				{
					return defaultTypeId;
				}

				defaultTypeId = defaultType.Id;
			}

			TextNoteType created;
			try
			{
				created = defaultType.Duplicate(typeName) as TextNoteType;
			}
			catch (Autodesk.Revit.Exceptions.ArgumentException)
			{
				return defaultTypeId;
			}

			if (created == null)
			{
				return defaultTypeId;
			}

			Configure(created, paperHeight, widthFactor, font, bold);
			_existingByName[typeName] = created;
			return created.Id;
		}

		private static void Configure(TextNoteType type, double paperHeight, double widthFactor, string font, bool bold)
		{
			TrySet(type, BuiltInParameter.TEXT_STYLE_BOLD, p => p.Set(bold ? 1 : 0));
			TrySet(type, BuiltInParameter.TEXT_SIZE, p => p.Set(paperHeight));
			TrySet(type, BuiltInParameter.TEXT_WIDTH_SCALE, p => p.Set(widthFactor));
			TrySet(type, BuiltInParameter.TEXT_FONT, p => p.Set(font));
			// Fondo: 0 = Opaco, 1 = Transparente.
			TrySet(type, BuiltInParameter.TEXT_BACKGROUND, p => p.Set(1));
			// "Desfase de línea directriz/borde" = 0: el recuadro de la nota se ajusta al texto.
			TrySet(type, BuiltInParameter.LEADER_OFFSET_SHEET, p => p.Set(0.0));
			// "Mostrar borde" desactivado.
			TrySet(type, BuiltInParameter.TEXT_BOX_VISIBILITY, p => p.Set(0));
		}

		private static void TrySet(Element element, BuiltInParameter id, Action<Parameter> set)
		{
			try
			{
				Parameter parameter = element.get_Parameter(id);
				if (parameter != null && !parameter.IsReadOnly)
				{
					set(parameter);
				}
			}
			catch (Exception)
			{
			}
		}
	}

	// ---------------------------------------------------------------- Curvas

	private static void CreateDetailCurves(Document doc, View view, List<(Curve Curve, ElementId StyleId)> curves, LineStyleMapper lineStyles,
		DwgLinetypeIndex linetypes, Stats stats)
	{
		// Agrupar por Line Style resuelto: cada grupo se crea en lote con NewDetailCurveArray.
		var groups = new Dictionary<ElementId, (GraphicsStyle Style, List<Curve> Curves)>();
		foreach (var (curve, styleId) in curves)
		{
			// Si tenemos el DWG original, el tipo de línea real de la entidad (segmentada o continua)
			// manda sobre el de la capa.
			DwgLinePattern pattern = linetypes?.Find(curve);
			if (pattern != null)
			{
				stats.PatternedLines++;
			}

			GraphicsStyle style = pattern != null ? lineStyles.Resolve(styleId, pattern) : lineStyles.Resolve(styleId);
			ElementId key = style?.Id ?? ElementId.InvalidElementId;
			if (!groups.TryGetValue(key, out var group))
			{
				group = (style, new List<Curve>());
				groups[key] = group;
			}

			group.Curves.Add(curve);
		}

		foreach (var (style, groupCurves) in groups.Values)
		{
			for (int start = 0; start < groupCurves.Count; start += CurveBatchSize)
			{
				int count = Math.Min(CurveBatchSize, groupCurves.Count - start);
				List<Curve> batch = groupCurves.GetRange(start, count);
				List<DetailCurve> created = CreateBatch(doc, view, batch, ref stats.Skipped);
				stats.Lines += created.Count;
				if (style == null)
				{
					continue;
				}

				foreach (DetailCurve detailCurve in created)
				{
					try
					{
						detailCurve.LineStyle = style;
					}
					catch (Autodesk.Revit.Exceptions.ArgumentException)
					{
					}
				}
			}
		}
	}

	private static List<DetailCurve> CreateBatch(Document doc, View view, List<Curve> batch, ref int skipped)
	{
		var created = new List<DetailCurve>(batch.Count);
		try
		{
			var array = new CurveArray();
			foreach (Curve c in batch)
			{
				array.Append(c);
			}

			DetailCurveArray result = doc.Create.NewDetailCurveArray(view, array);
			foreach (DetailCurve dc in result)
			{
				created.Add(dc);
			}

			return created;
		}
		catch (Exception)
		{
			// Alguna curva del lote no es válida: se crean una a una para no perder las demás.
		}

		foreach (Curve curve in batch)
		{
			DetailCurve dc = TryCreateSingle(doc, view, curve);
			if (dc != null)
			{
				created.Add(dc);
			}
			else
			{
				skipped++;
			}
		}

		return created;
	}

	private static DetailCurve TryCreateSingle(Document doc, View view, Curve curve)
	{
		try
		{
			return doc.Create.NewDetailCurve(view, curve);
		}
		catch (Exception)
		{
		}

		// Segundo intento: proyectar la curva al plano de la vista (DWG con cota Z distinta de la vista).
		try
		{
			Curve projected = ProjectToViewPlane(view, curve);
			if (projected != null)
			{
				return doc.Create.NewDetailCurve(view, projected);
			}
		}
		catch (Exception)
		{
		}

		return null;
	}

	private static Curve ProjectToViewPlane(View view, Curve curve)
	{
		XYZ normal = view.ViewDirection;
		XYZ origin = view.SketchPlane?.GetPlane()?.Origin ?? view.Origin;
		if (normal == null || origin == null)
		{
			return null;
		}

		double d0 = (curve.GetEndPoint(0) - origin).DotProduct(normal);
		double d1 = (curve.GetEndPoint(1) - origin).DotProduct(normal);
		if (Math.Abs(d0 - d1) > 1E-06 || Math.Abs(d0) < 1E-09)
		{
			return null;
		}

		return curve.CreateTransformed(Transform.CreateTranslation(normal.Multiply(-d0)));
	}

	private static void CollectCurves(GeometryElement geometry, List<(Curve, ElementId)> output,
		HashSet<(long, long, long, long, long, long, long)> seen, double minLength, ref int skipped)
	{
		foreach (GeometryObject obj in geometry)
		{
			switch (obj)
			{
				case GeometryInstance instance:
					GeometryElement instanceGeometry = instance.GetInstanceGeometry();
					if (instanceGeometry != null)
					{
						CollectCurves(instanceGeometry, output, seen, minLength, ref skipped);
					}

					break;
				case Curve curve:
					AddCurve(curve, obj.GraphicsStyleId, output, seen, minLength, ref skipped);
					break;
				case PolyLine polyLine:
					IList<XYZ> points = polyLine.GetCoordinates();
					for (int i = 0; i < points.Count - 1; i++)
					{
						AddSegment(points[i], points[i + 1], obj.GraphicsStyleId, output, seen, minLength, ref skipped);
					}

					break;
				case Solid solid:
					foreach (Edge edge in solid.Edges)
					{
						AddCurve(edge.AsCurve(), obj.GraphicsStyleId, output, seen, minLength, ref skipped);
					}

					break;
				case Mesh mesh:
					for (int i = 0; i < mesh.NumTriangles; i++)
					{
						MeshTriangle triangle = mesh.get_Triangle(i);
						AddSegment(triangle.get_Vertex(0), triangle.get_Vertex(1), obj.GraphicsStyleId, output, seen, minLength, ref skipped);
						AddSegment(triangle.get_Vertex(1), triangle.get_Vertex(2), obj.GraphicsStyleId, output, seen, minLength, ref skipped);
						AddSegment(triangle.get_Vertex(2), triangle.get_Vertex(0), obj.GraphicsStyleId, output, seen, minLength, ref skipped);
					}

					break;
			}
		}
	}

	private static void AddSegment(XYZ a, XYZ b, ElementId styleId, List<(Curve, ElementId)> output,
		HashSet<(long, long, long, long, long, long, long)> seen, double minLength, ref int skipped)
	{
		if (a.DistanceTo(b) <= minLength)
		{
			skipped++;
			return;
		}

		if (!seen.Add(SegmentKey(a, b, styleId)))
		{
			return;
		}

		try
		{
			output.Add((Line.CreateBound(a, b), styleId));
		}
		catch (Autodesk.Revit.Exceptions.ArgumentException)
		{
			skipped++;
		}
	}

	private static void AddCurve(Curve curve, ElementId styleId, List<(Curve, ElementId)> output,
		HashSet<(long, long, long, long, long, long, long)> seen, double minLength, ref int skipped)
	{
		if (curve == null)
		{
			skipped++;
			return;
		}

		try
		{
			// Los círculos/elipses completos llegan como curvas no acotadas: se dividen en dos mitades.
			if (!curve.IsBound)
			{
				if (!curve.IsCyclic)
				{
					skipped++;
					return;
				}

				double period = curve.Period;
				Curve first = curve.Clone();
				first.MakeBound(0.0, period / 2.0);
				Curve second = curve.Clone();
				second.MakeBound(period / 2.0, period);
				AddCurve(first, styleId, output, seen, minLength, ref skipped);
				AddCurve(second, styleId, output, seen, minLength, ref skipped);
				return;
			}

			if (curve.Length <= minLength)
			{
				skipped++;
				return;
			}

			if (curve is Line && !seen.Add(SegmentKey(curve.GetEndPoint(0), curve.GetEndPoint(1), styleId)))
			{
				return;
			}

			output.Add((curve, styleId));
		}
		catch (Exception)
		{
			skipped++;
		}
	}

	/// <summary>Clave independiente del sentido del segmento, para no crear dos veces la misma línea.</summary>
	private static (long, long, long, long, long, long, long) SegmentKey(XYZ a, XYZ b, ElementId styleId)
	{
		long ax = Round(a.X), ay = Round(a.Y), az = Round(a.Z);
		long bx = Round(b.X), by = Round(b.Y), bz = Round(b.Z);
		bool swap = ax > bx || (ax == bx && (ay > by || (ay == by && az > bz)));
		long style = styleId?.Value ?? -1;
		return swap ? (bx, by, bz, ax, ay, az, style) : (ax, ay, az, bx, by, bz, style);
	}

	private static long Round(double v) => (long)Math.Round(v / DedupTolerance);

	// ---------------------------------------------------------------- Selección / diálogos

	private static Dictionary<ElementId, string> AskForManualDwgPaths(Document doc, List<ImportInstance> targets)
	{
		var result = new Dictionary<ElementId, string>();
		List<ImportInstance> unresolved = targets.Where(i => !HasResolvableLink(doc, i)).ToList();
		if (unresolved.Count == 0)
		{
			return result;
		}

		var dialog = new TaskDialog("Explotar DWGs")
		{
			MainInstruction = "Algunos DWG están importados (no vinculados)",
			MainContent = $"{unresolved.Count} de {targets.Count} instancia(s) de CAD están importadas (embebidas), no vinculadas. " +
				"Revit no conserva la ruta al archivo original para esos casos.\n\n" +
				"Si todavía tienes el/los archivo(s) .dwg originales, puedes localizarlos ahora para recuperar el texto exacto. " +
				"Si no los tienes (o prefieres saltarlo), el addin intentará primero reexportar la vista a DWG y, si no " +
				"obtiene texto, lo reconocerá por OCR sobre la geometría (aproximado).",
			CommonButtons = TaskDialogCommonButtons.None
		};
		dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Buscar los archivos .dwg originales (texto exacto)");
		dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Continuar sin buscarlos");
		if (dialog.Show() != TaskDialogResult.CommandLink1)
		{
			return result;
		}

		// Varias instancias del mismo tipo CAD comparten archivo: se pregunta una sola vez por tipo.
		var pathByType = new Dictionary<ElementId, string>();
		foreach (ImportInstance instance in unresolved)
		{
			ElementId typeId = instance.GetTypeId();
			if (!pathByType.TryGetValue(typeId, out string path))
			{
				string typeName = doc.GetElement(typeId)?.Name ?? string.Empty;
				using var fileDialog = new OpenFileDialog
				{
					Title = $"Selecciona el DWG original para \"{typeName}\" (Id {instance.Id.Value})",
					Filter = "Archivos DWG (*.dwg)|*.dwg|Todos los archivos (*.*)|*.*",
					CheckFileExists = true
				};
				path = fileDialog.ShowDialog() == DialogResult.OK ? fileDialog.FileName : null;
				pathByType[typeId] = path;
			}

			if (path != null)
			{
				result[instance.Id] = path;
			}
		}

		return result;
	}

	private static bool HasResolvableLink(Document doc, ImportInstance importInstance)
	{
		return importInstance.IsLinked && doc.GetElement(importInstance.GetTypeId())?.GetExternalFileReference() != null;
	}

	private static List<ImportInstance> GetSelectedImportInstances(UIDocument uiDoc)
	{
		Document doc = uiDoc.Document;
		return uiDoc.Selection.GetElementIds().Select(doc.GetElement).OfType<ImportInstance>().ToList();
	}

	private static List<ImportInstance> GetImportInstancesInView(Document doc, View view)
	{
		return new FilteredElementCollector(doc, view.Id)
			.OfClass(typeof(ImportInstance))
			.WhereElementIsNotElementType()
			.Cast<ImportInstance>()
			.ToList();
	}

	/// <summary>
	/// Elimina las advertencias (p.ej. "línea ligeramente fuera de eje") para que Revit no muestre
	/// un diálogo por cada una y la confirmación sea mucho más rápida.
	/// </summary>
	private sealed class WarningSwallower : IFailuresPreprocessor
	{
		public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
		{
			foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
			{
				if (failure.GetSeverity() == FailureSeverity.Warning)
				{
					failuresAccessor.DeleteWarning(failure);
				}
			}

			return FailureProcessingResult.Continue;
		}
	}
}

/// <summary>Habilita el botón solo cuando la vista activa admite Detail Lines.</summary>
public class ExplodeDwgAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
	{
		return SupportsDetailCurves(applicationData?.ActiveUIDocument?.ActiveView);
	}

	internal static bool SupportsDetailCurves(View view)
	{
		if (view == null || view.IsTemplate)
		{
			return false;
		}

		switch (view.ViewType)
		{
			case ViewType.FloorPlan:
			case ViewType.CeilingPlan:
			case ViewType.EngineeringPlan:
			case ViewType.AreaPlan:
			case ViewType.Elevation:
			case ViewType.Section:
			case ViewType.Detail:
			case ViewType.DraftingView:
			case ViewType.Legend:
			case ViewType.DrawingSheet:
				return true;
			default:
				return false;
		}
	}
}
