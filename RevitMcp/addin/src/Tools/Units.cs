using System;
using Autodesk.Revit.DB;

namespace RevitMcp.Tools;

/// <summary>Revit trabaja internamente en pies; la interfaz MCP usa milímetros y grados.</summary>
internal static class Units
{
	public static double ToInternal(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

	public static double ToMm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 3);

	public static XYZ FromMm(double x, double y, double z) => new(ToInternal(x), ToInternal(y), ToInternal(z));

	public static object Mm(XYZ p) => p == null ? null : new { x = ToMm(p.X), y = ToMm(p.Y), z = ToMm(p.Z) };

	public static double DegToRad(double deg) => deg * Math.PI / 180.0;
}
