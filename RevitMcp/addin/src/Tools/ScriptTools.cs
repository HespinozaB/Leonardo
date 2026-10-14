using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace RevitMcp.Tools;

/// <summary>Variables disponibles dentro del script.</summary>
public sealed class ScriptGlobals
{
	public UIApplication uiapp;
	public UIDocument uidoc;
	public Document doc;
	internal readonly StringBuilder Output = new();

	/// <summary>Escribe una línea en la salida que recibe Claude.</summary>
	public void Print(object value) => Output.AppendLine(value?.ToString());

	public double Mm(double mm) => Units.ToInternal(mm);
	public double ToMm(double feet) => Units.ToMm(feet);
}

/// <summary>
/// Ejecuta C# arbitrario en el hilo de Revit: cubre lo que las demás herramientas no hacen.
/// Roslyn se carga solo la primera vez que se usa.
/// </summary>
internal static class ScriptTools
{
	public static object RunCSharp(UIApplication app, JsonObject args) => Run(app, args);

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static object Run(UIApplication app, JsonObject args)
	{
		string code = Args.RequiredStr(args, "code");
		bool useTransaction = Args.Bool(args, "transaction", true);
		var globals = new ScriptGlobals { uiapp = app, uidoc = app.ActiveUIDocument, doc = app.ActiveUIDocument?.Document };

		ScriptOptions options = ScriptOptions.Default
			.WithReferences(typeof(Element).Assembly, typeof(UIApplication).Assembly, typeof(Enumerable).Assembly,
				typeof(ScriptGlobals).Assembly, typeof(List<>).Assembly)
			.WithImports("System", "System.Linq", "System.Collections.Generic", "System.Text",
				"Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI")
			.WithEmitDebugInformation(false);

		Script<object> script = CSharpScript.Create<object>(code, options, typeof(ScriptGlobals));
		var diagnostics = script.Compile().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
		if (diagnostics.Count > 0)
		{
			throw new ArgumentException("Error de compilación:\n" + string.Join("\n", diagnostics.Select(d => d.ToString())));
		}

		ScriptRunner<object> runner = script.CreateDelegate();
		object value;
		List<string> warnings = new();
		if (useTransaction && globals.doc != null)
		{
			value = RevitUtil.InTransaction(globals.doc, Args.Str(args, "transactionName", "Script"),
				() => runner(globals).GetAwaiter().GetResult(), out warnings);
		}
		else
		{
			value = runner(globals).GetAwaiter().GetResult();
		}

		return new
		{
			output = globals.Output.ToString(),
			returnValue = Format(value),
			warnings
		};
	}

	private static object Format(object value) => value switch
	{
		null => null,
		string s => s,
		ElementId id => id.Value,
		Element e => Describe.Element(e),
		IEnumerable<Element> list => list.Take(500).Select(Describe.Element).ToList(),
		IEnumerable<ElementId> ids => ids.Select(i => i.Value).ToList(),
		XYZ p => Units.Mm(p),
		_ when value.GetType().IsPrimitive || value is decimal => value,
		_ => TrySerialize(value)
	};

	private static object TrySerialize(object value)
	{
		try
		{
			return Json.ToNode(value);
		}
		catch (Exception)
		{
			return value.ToString();
		}
	}
}
