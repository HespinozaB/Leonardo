using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CSharp;
using RevitMCP.Bridge;
using static RevitMCP.Tools.RevitHelpers;

namespace RevitMCP.Tools;

/// <summary>
/// Compila y ejecuta un fragmento de C# contra la API de Revit. Permite a Claude hacer cualquier
/// operación no cubierta por las herramientas específicas. Se desactiva con REVIT_MCP_ALLOW_CODE=0.
/// </summary>
internal static class CodeTool
{
	private const string Template = @"
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

public static class McpScript
{
	public static object Run(UIApplication uiapp, UIDocument uidoc, Document doc)
	{
#line 1
		/*CODE*/
#line default
		return null;
	}
}";

	public static object Execute(UIApplication app, Args a)
	{
		if (string.Equals(Environment.GetEnvironmentVariable("REVIT_MCP_ALLOW_CODE"), "0", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("La ejecución de código está desactivada (REVIT_MCP_ALLOW_CODE=0).");
		}
		string code = a.ReqStr("codigo");
		bool useTransaction = a.Bool("transaccion", true);
		UIDocument uidoc = ActiveUiDoc(app);
		Document doc = uidoc.Document;

		MethodInfo run = Compile(code);
		Func<object> invoke = () =>
		{
			try
			{
				return Simplify(run.Invoke(null, new object[] { app, uidoc, doc }));
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				throw new InvalidOperationException(ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
			}
		};
		return useTransaction ? RunTransaction(doc, "Código", invoke) : invoke();
	}

	private static MethodInfo Compile(string code)
	{
		using var provider = new CSharpCodeProvider();
		var parameters = new CompilerParameters
		{
			GenerateInMemory = true,
			GenerateExecutable = false,
			TreatWarningsAsErrors = false
		};
		parameters.ReferencedAssemblies.Add("System.dll");
		parameters.ReferencedAssemblies.Add("System.Core.dll");
		parameters.ReferencedAssemblies.Add(typeof(Document).Assembly.Location);
		parameters.ReferencedAssemblies.Add(typeof(UIApplication).Assembly.Location);

		CompilerResults results = provider.CompileAssemblyFromSource(parameters, Template.Replace("/*CODE*/", code));
		List<string> errors = results.Errors.Cast<CompilerError>()
			.Where(e => !e.IsWarning)
			.Select(e => "línea " + e.Line + ": " + e.ErrorText)
			.ToList();
		if (errors.Count > 0)
		{
			throw new ArgumentException("Error de compilación (C# 5):\n" + string.Join("\n", errors));
		}
		return results.CompiledAssembly.GetType("McpScript").GetMethod("Run");
	}

	/// <summary>Convierte el resultado a algo serializable a JSON.</summary>
	private static object Simplify(object value, int depth = 0)
	{
		switch (value)
		{
			case null:
				return null;
			case string or bool or int or long or double or float or decimal:
				return value;
			case ElementId id:
				return id.Value;
			case Element e:
				return Describe(e.Document, e);
			case XYZ p:
				return PointToMm(p);
			case IDictionary dict when depth < 4:
				var map = new Dictionary<string, object>();
				foreach (DictionaryEntry entry in dict)
				{
					map[Convert.ToString(entry.Key)] = Simplify(entry.Value, depth + 1);
				}
				return map;
			case IEnumerable list when depth < 4:
				return list.Cast<object>().Take(1000).Select(o => Simplify(o, depth + 1)).ToList();
			default:
				return value.ToString();
		}
	}
}
