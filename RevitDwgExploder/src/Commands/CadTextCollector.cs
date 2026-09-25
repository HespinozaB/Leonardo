using System;
using System.Collections.Generic;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace RevitDwgExploder.Commands;

/// <summary>
/// Texto leído de un DWG, ya expresado en coordenadas del espacio modelo (unidades del DWG).
/// </summary>
internal sealed class RawText
{
	public string Text;

	public double X;

	public double Y;

	public double Z;

	public double Height;

	public double Rotation;

	public TextAnchorH AnchorH;

	public TextAnchorV AnchorV;
}

internal enum TextAnchorH
{
	Left,
	Center,
	Right
}

internal enum TextAnchorV
{
	Bottom,
	Middle,
	Top
}

/// <summary>
/// Recorre el espacio modelo de un <see cref="CadDocument"/> y devuelve todos los TEXT, MTEXT y
/// ATTRIB visibles, incluidos los que están dentro de bloques anidados (aplicando la transformación
/// completa de cada INSERT: punto base, escala, rotación y normal).
/// </summary>
internal static class CadTextCollector
{
	private const int MaxBlockDepth = 8;

	public static List<RawText> Collect(CadDocument cadDoc)
	{
		var output = new List<RawText>();
		if (cadDoc?.Entities == null)
		{
			return output;
		}

		CollectFrom(cadDoc.Entities, output, Matrix4.Identity, 0);
		return output;
	}

	private static void CollectFrom(IEnumerable<Entity> entities, List<RawText> output, Matrix4 transform, int depth)
	{
		foreach (Entity entity in entities)
		{
			if (entity == null || entity.IsInvisible)
			{
				continue;
			}

			switch (entity)
			{
				case AttributeDefinition:
					// Las definiciones de atributo solo son plantillas dentro del bloque; el valor real va en el ATTRIB del INSERT.
					break;
				case TextEntity text:
					AddText(output, text, transform);
					break;
				case MText mText:
					AddMText(output, mText, transform);
					break;
				case Insert insert:
					if (depth >= MaxBlockDepth || insert.Block == null)
					{
						break;
					}

					Matrix4 insertTransform;
					try
					{
						insertTransform = transform * insert.GetTransform().Matrix;
					}
					catch (Exception)
					{
						break;
					}

					CollectFrom(insert.Block.Entities, output, insertTransform, depth + 1);

					// Los ATTRIB ya están en coordenadas del contenedor del INSERT.
					foreach (AttributeEntity attribute in insert.Attributes)
					{
						if (attribute != null && !attribute.IsInvisible && (attribute.Flags & AttributeFlags.Hidden) == 0)
						{
							AddText(output, attribute, transform);
						}
					}

					break;
			}
		}
	}

	private static void AddText(List<RawText> output, TextEntity text, Matrix4 transform)
	{
		GetTextAnchors(text, out TextAnchorH anchorH, out TextAnchorV anchorV, out bool useAlignmentPoint);
		XYZ point = useAlignmentPoint && text.AlignmentPoint != null ? text.AlignmentPoint : text.InsertPoint;
		Add(output, text.Value, point, text.Height, text.Rotation, anchorH, anchorV, transform);
	}

	private static void AddMText(List<RawText> output, MText mText, Matrix4 transform)
	{
		GetMTextAnchors(mText.AttachmentPoint, out TextAnchorH anchorH, out TextAnchorV anchorV);
		Add(output, mText.PlainText, mText.InsertPoint, mText.Height, mText.Rotation, anchorH, anchorV, transform);
	}

	private static void Add(List<RawText> output, string value, XYZ localPoint, double localHeight, double localRotation,
		TextAnchorH anchorH, TextAnchorV anchorV, Matrix4 transform)
	{
		string clean = CleanText(value);
		if (clean.Length == 0)
		{
			return;
		}

		// Transformamos el punto, un vector en la dirección del texto y otro en la dirección de su altura,
		// así la rotación y la escala resultantes son correctas aunque el bloque esté rotado/escalado/anidado.
		XYZ origin = transform * localPoint;
		var direction = new XYZ(Math.Cos(localRotation), Math.Sin(localRotation), 0.0);
		var up = new XYZ(-Math.Sin(localRotation), Math.Cos(localRotation), 0.0);
		XYZ dirEnd = transform * (localPoint + direction);
		XYZ upEnd = transform * (localPoint + up * localHeight);

		double dx = dirEnd.X - origin.X;
		double dy = dirEnd.Y - origin.Y;
		double height = Math.Sqrt(Math.Pow(upEnd.X - origin.X, 2) + Math.Pow(upEnd.Y - origin.Y, 2));
		if (double.IsNaN(height) || height <= 0.0)
		{
			height = localHeight;
		}

		output.Add(new RawText
		{
			Text = clean,
			X = origin.X,
			Y = origin.Y,
			Z = origin.Z,
			Height = height,
			Rotation = Math.Abs(dx) + Math.Abs(dy) > 1E-12 ? Math.Atan2(dy, dx) : localRotation,
			AnchorH = anchorH,
			AnchorV = anchorV
		});
	}

	internal static string CleanText(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return string.Empty;
		}

		return raw.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
	}

	private static void GetTextAnchors(TextEntity text, out TextAnchorH anchorH, out TextAnchorV anchorV, out bool useAlignmentPoint)
	{
		anchorH = TextAnchorH.Left;
		anchorV = TextAnchorV.Bottom;
		useAlignmentPoint = false;

		switch (text.HorizontalAlignment)
		{
			case TextHorizontalAlignment.Center:
				anchorH = TextAnchorH.Center;
				useAlignmentPoint = true;
				break;
			case TextHorizontalAlignment.Middle:
				anchorH = TextAnchorH.Center;
				anchorV = TextAnchorV.Middle;
				useAlignmentPoint = true;
				break;
			case TextHorizontalAlignment.Right:
				anchorH = TextAnchorH.Right;
				useAlignmentPoint = true;
				break;
		}

		switch (text.VerticalAlignment)
		{
			case TextVerticalAlignmentType.Middle:
				anchorV = TextAnchorV.Middle;
				useAlignmentPoint = true;
				break;
			case TextVerticalAlignmentType.Top:
				anchorV = TextAnchorV.Top;
				useAlignmentPoint = true;
				break;
			case TextVerticalAlignmentType.Bottom:
				useAlignmentPoint = true;
				break;
		}
	}

	private static void GetMTextAnchors(AttachmentPointType attachment, out TextAnchorH anchorH, out TextAnchorV anchorV)
	{
		switch (attachment)
		{
			case AttachmentPointType.TopCenter:
				anchorH = TextAnchorH.Center;
				anchorV = TextAnchorV.Top;
				break;
			case AttachmentPointType.TopRight:
				anchorH = TextAnchorH.Right;
				anchorV = TextAnchorV.Top;
				break;
			case AttachmentPointType.MiddleLeft:
				anchorH = TextAnchorH.Left;
				anchorV = TextAnchorV.Middle;
				break;
			case AttachmentPointType.MiddleCenter:
				anchorH = TextAnchorH.Center;
				anchorV = TextAnchorV.Middle;
				break;
			case AttachmentPointType.MiddleRight:
				anchorH = TextAnchorH.Right;
				anchorV = TextAnchorV.Middle;
				break;
			case AttachmentPointType.BottomLeft:
				anchorH = TextAnchorH.Left;
				anchorV = TextAnchorV.Bottom;
				break;
			case AttachmentPointType.BottomCenter:
				anchorH = TextAnchorH.Center;
				anchorV = TextAnchorV.Bottom;
				break;
			case AttachmentPointType.BottomRight:
				anchorH = TextAnchorH.Right;
				anchorV = TextAnchorV.Bottom;
				break;
			default:
				anchorH = TextAnchorH.Left;
				anchorV = TextAnchorV.Top;
				break;
		}
	}
}
