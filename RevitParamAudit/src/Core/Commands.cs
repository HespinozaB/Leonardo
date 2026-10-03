using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitParamAudit.Core;

namespace RevitParamAudit.Commands;

public abstract class DepuradorCommand : IExternalCommand
{
	internal abstract ToolKind Kind { get; }

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		try
		{
			return DepuradorLauncher.Show(commandData.Application, Kind);
		}
		catch (Exception ex)
		{
			message = ex.Message;
			return Result.Failed;
		}
	}
}

/// <summary>1. Depurador de planos.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class SheetsCommand : DepuradorCommand
{
	internal override ToolKind Kind => ToolKind.Sheets;
}

/// <summary>2. Depurador de vistas sin plano.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ViewsCommand : DepuradorCommand
{
	internal override ToolKind Kind => ToolKind.Views;
}

/// <summary>3. Depurador de filtros.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class FiltersCommand : DepuradorCommand
{
	internal override ToolKind Kind => ToolKind.Filters;
}

/// <summary>4. Depurador de parámetros.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ParametersCommand : DepuradorCommand
{
	internal override ToolKind Kind => ToolKind.Parameters;
}

/// <summary>Los depuradores están disponibles siempre que haya un modelo abierto.</summary>
public class DepuradorAvailability : IExternalCommandAvailability
{
	public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
		applicationData?.ActiveUIDocument != null;
}
