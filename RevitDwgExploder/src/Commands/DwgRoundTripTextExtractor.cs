using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using Autodesk.Revit.DB;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Obtiene el texto de un CAD importado reexportando la vista activa a DWG y leyendo el resultado.
/// La exportación (lo caro) se hace una sola vez por vista y se reutiliza para todas las instancias.
/// </summary>
internal sealed class DwgRoundTripTextExtractor : IDisposable
{
	private static readonly double[] CandidateScales =
	{
		1.0,
		1.0 / 12.0,
		0.0032808398950131233,
		25.0 / 762.0,
		3.280839895013123
	};

	private readonly Document _doc;

	private readonly View _view;

	private readonly string _tempDir;

	private bool _loaded;

	private List<RawText> _texts = new List<RawText>();

	private CadDocument _cadDoc;

	private SpatialPointIndex _existingTextNotes;

	private readonly List<ElementId> _targets;

	/// <summary>True si la exportación falló (no si simplemente el CAD no tiene textos).</summary>
	public bool ExportFailed { get; private set; }

	public DwgRoundTripTextExtractor(Document doc, View view, IEnumerable<ElementId> targets)
	{
		_doc = doc;
		_view = view;
		_targets = targets?.ToList() ?? new List<ElementId>();
		_tempDir = Path.Combine(Path.GetTempPath(), "RevitDwgExploder_" + Guid.NewGuid().ToString("N"));
	}

	public List<DwgTextImporter.DwgTextEntry> Extract(ImportInstance importInstance)
	{
		var result = new List<DwgTextImporter.DwgTextEntry>();
		try
		{
			BoundingBoxXYZ bbox = importInstance.get_BoundingBox(_view);
			if (bbox == null)
			{
				return result;
			}

			EnsureLoaded();
			if (_texts.Count == 0)
			{
				return result;
			}

			double scale = ResolveScale(bbox);
			if (scale <= 0.0)
			{
				return result;
			}

			double z = (bbox.Min.Z + bbox.Max.Z) / 2.0;
			foreach (RawText raw in _texts)
			{
				var position = new XYZ(raw.X * scale, raw.Y * scale, z);
				double height = raw.Height * scale;
				if (height < 0.0001 || !IsInside(position, bbox) || _existingTextNotes.Contains(position, 0.05))
				{
					continue;
				}

				result.Add(new DwgTextImporter.DwgTextEntry
				{
					Text = raw.Text,
					Position = position,
					HeightFeet = height,
					RotationRadians = raw.Rotation,
					AnchorH = raw.AnchorH,
					AnchorV = raw.AnchorV,
					WidthFactor = raw.WidthFactor,
					FontName = raw.FontName,
					Bold = raw.Bold
				});
			}
		}
		catch (Exception)
		{
		}

		return result;
	}

	/// <summary>
	/// Hatch del CAD leídos del DWG reexportado (para CAD importados sin archivo original). Solo se devuelven
	/// los que están en una capa del propio CAD y dentro de su caja, para no traer rellenos de otros elementos.
	/// </summary>
	public List<HatchRegion> ExtractHatches(ImportInstance importInstance)
	{
		var result = new List<HatchRegion>();
		try
		{
			BoundingBoxXYZ bbox = importInstance.get_BoundingBox(_view);
			if (bbox == null)
			{
				return result;
			}

			EnsureLoaded();
			if (_cadDoc == null)
			{
				return result;
			}

			// Se exporta en pies: la escala es 1 salvo que los textos indiquen otra cosa.
			double scale = _texts.Count > 0 ? ResolveScale(bbox) : 1.0;
			if (scale <= 0.0)
			{
				scale = 1.0;
			}

			var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				foreach (Category sub in importInstance.Category.SubCategories)
				{
					layers.Add(sub.Name);
				}
			}
			catch (Exception)
			{
			}

			_hatches ??= DwgHatchReader.Read(_cadDoc, 1.0, Transform.Identity);

			// Si Revit exportó las capas con otro nombre, se aceptan por posición, salvo que la vista ya tenga
			// Filled Regions propios (que también se exportan como hatch y se duplicarían).
			bool filterByLayer = layers.Count > 0 && (_hatches.Any(h => layers.Contains(h.Layer)) || ViewHasFilledRegions());
			foreach (HatchRegion hatch in _hatches)
			{
				if (filterByLayer && !layers.Contains(hatch.Layer))
				{
					continue;
				}

				HatchRegion scaled = Scale(hatch, scale, (bbox.Min.Z + bbox.Max.Z) / 2.0);
				List<XYZ> all = scaled.Loops.SelectMany(l => l).ToList();
				if (all.Count == 0)
				{
					continue;
				}

				var center = new XYZ(all.Average(p => p.X), all.Average(p => p.Y), 0.0);
				if (IsInside(center, bbox))
				{
					result.Add(scaled);
				}
			}
		}
		catch (Exception)
		{
		}

		return result;
	}

	private List<HatchRegion> _hatches;

	private bool ViewHasFilledRegions()
	{
		try
		{
			return new FilteredElementCollector(_doc, _view.Id).OfClass(typeof(FilledRegion)).GetElementCount() > 0;
		}
		catch (Exception)
		{
			return true;
		}
	}

	private static HatchRegion Scale(HatchRegion h, double scale, double z)
	{
		return new HatchRegion
		{
			Layer = h.Layer,
			IsSolid = h.IsSolid,
			PatternName = h.PatternName,
			R = h.R,
			G = h.G,
			B = h.B,
			Loops = h.Loops.Select(l => l.Select(p => new XYZ(p.X * scale, p.Y * scale, z)).ToList()).ToList(),
			Grids = h.Grids.Select(g => new HatchGrid
			{
				Angle = g.Angle,
				OriginX = g.OriginX * scale,
				OriginY = g.OriginY * scale,
				Offset = g.Offset * scale,
				Shift = g.Shift * scale,
				Segments = g.Segments.Select(v => v * scale).ToList()
			}).ToList()
		};
	}

	private void EnsureLoaded()
	{
		if (_loaded)
		{
			return;
		}

		_loaded = true;
		_existingTextNotes = new SpatialPointIndex(GetExistingTextNotePositions(), 0.05);
		try
		{
			Directory.CreateDirectory(_tempDir);
			// Una sola exportación: si el CAD no tiene textos, exportar de nuevo con otro modo no los crea.
			_cadDoc = ExportAndRead(TextTreatment.Approximate);
			ExportFailed = _cadDoc == null;
			_texts = CadTextCollector.Collect(_cadDoc);
		}
		catch (Exception)
		{
			ExportFailed = true;
			_texts = new List<RawText>();
		}
	}

	private CadDocument ExportAndRead(TextTreatment textTreatment)
	{
		string folder = Path.Combine(_tempDir, textTreatment.ToString());
		Directory.CreateDirectory(folder);
		var options = new DWGExportOptions
		{
			MergedViews = true,
			TargetUnit = ExportUnit.Foot,
			SharedCoords = false,
			TextTreatment = textTreatment,
			FileVersion = ACADVersion.R2013
		};
		// Solo los CAD a explotar, recortados a su área: la exportación es mucho más pequeña y rápida, y se
		// evita el aviso de Revit "los contornos de la vista son demasiado grandes para su exportación".
		// Todo ocurre en un TransactionGroup que se deshace: la vista queda intacta.
		using (var group = new TransactionGroup(_doc, "EMASY: exportación temporal"))
		{
			group.Start();
			try
			{
				using (var tx = new Transaction(_doc, "EMASY: aislar CAD"))
				{
					tx.Start();
					IsolateTargets();
					tx.Commit();
				}

				_doc.Export(folder, "roundtrip", new List<ElementId> { _view.Id }, options);
			}
			finally
			{
				group.RollBack();
			}
		}

		string file = Directory.GetFiles(folder, "*.dwg").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
		if (file == null)
		{
			return null;
		}

		try
		{
			return DwgTextImporter.ReadDwg(file);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Oculta todo lo que no son los CAD a explotar y, si la vista lo permite, la recorta a su área.</summary>
	private void IsolateTargets()
	{
		if (_targets.Count == 0)
		{
			return;
		}

		var keep = new HashSet<ElementId>(_targets);
		try
		{
			List<ElementId> others = new FilteredElementCollector(_doc, _view.Id)
				.WhereElementIsNotElementType()
				.Where(e => !keep.Contains(e.Id) && e.CanBeHidden(_view))
				.Select(e => e.Id)
				.ToList();
			if (others.Count > 0)
			{
				_view.HideElements(others);
			}
		}
		catch (Exception)
		{
		}

		try
		{
			BoundingBoxXYZ crop = _view.CropBox;
			if (crop == null)
			{
				return;
			}

			Transform toCrop = (crop.Transform ?? Transform.Identity).Inverse;
			var points = new List<XYZ>();
			foreach (ElementId id in _targets)
			{
				BoundingBoxXYZ box = _doc.GetElement(id)?.get_BoundingBox(_view);
				if (box == null)
				{
					continue;
				}

				points.Add(toCrop.OfPoint(box.Min));
				points.Add(toCrop.OfPoint(box.Max));
				points.Add(toCrop.OfPoint(new XYZ(box.Min.X, box.Max.Y, box.Min.Z)));
				points.Add(toCrop.OfPoint(new XYZ(box.Max.X, box.Min.Y, box.Min.Z)));
			}

			if (points.Count == 0)
			{
				return;
			}

			double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
			double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
			double mx = (maxX - minX) * 0.05 + 0.1, my = (maxY - minY) * 0.05 + 0.1;
			_view.CropBoxActive = true;
			_view.CropBoxVisible = false;
			_view.CropBox = new BoundingBoxXYZ
			{
				Transform = crop.Transform,
				Min = new XYZ(minX - mx, minY - my, crop.Min.Z),
				Max = new XYZ(maxX + mx, maxY + my, crop.Max.Z)
			};
		}
		catch (Exception)
		{
			// Leyendas y vistas de dibujo no admiten recorte: basta con haber ocultado lo demás.
		}
	}

	private double ResolveScale(BoundingBoxXYZ bbox)
	{
		var candidates = new List<double>(CandidateScales);
		if (_cadDoc?.Header != null)
		{
			double declared = DwgTextImporter.GetFeetPerDwgUnit(_cadDoc.Header.InsUnits);
			if (declared > 0.0 && !candidates.Any(c => Math.Abs(c - declared) < 1E-09))
			{
				candidates.Insert(0, declared);
			}
		}

		double best = 0.0;
		int bestCount = 0;
		foreach (double candidate in candidates)
		{
			int count = 0;
			foreach (RawText t in _texts)
			{
				if (IsInside(t.X * candidate, t.Y * candidate, bbox))
				{
					count++;
				}
			}

			if (count > bestCount)
			{
				bestCount = count;
				best = candidate;
			}
		}

		return bestCount > 0 ? best : 0.0;
	}

	private static bool IsInside(XYZ point, BoundingBoxXYZ bbox) => IsInside(point.X, point.Y, bbox);

	private static bool IsInside(double x, double y, BoundingBoxXYZ bbox)
	{
		double minX = Math.Min(bbox.Min.X, bbox.Max.X);
		double maxX = Math.Max(bbox.Min.X, bbox.Max.X);
		double minY = Math.Min(bbox.Min.Y, bbox.Max.Y);
		double maxY = Math.Max(bbox.Min.Y, bbox.Max.Y);
		double marginX = (maxX - minX) * 0.1;
		double marginY = (maxY - minY) * 0.1;
		return x >= minX - marginX && x <= maxX + marginX && y >= minY - marginY && y <= maxY + marginY;
	}

	private List<XYZ> GetExistingTextNotePositions()
	{
		try
		{
			return new FilteredElementCollector(_doc, _view.Id)
				.OfClass(typeof(TextNote))
				.Cast<TextNote>()
				.Select(t => t.Coord)
				.Where(c => c != null)
				.ToList();
		}
		catch (Exception)
		{
			return new List<XYZ>();
		}
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}
		catch (Exception)
		{
		}
	}
}
