using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.Exceptions;

namespace RevitMCPCommandSet.Commands.Parameters;

/// <summary>Returns the units configured in the active project (Manage &gt; Project Units).</summary>
public class GetProjectUnitsCommand : McpCommandBase
{
    public GetProjectUnitsCommand(UIApplication uiApp)
        : base(new DelegateEventHandler("Get project units"), uiApp)
    {
    }

    public override string CommandName => "get_project_units";

    protected override object ExecuteCommand(JObject parameters, string requestId)
    {
        var handler = (DelegateEventHandler)Handler;
        handler.Action = app =>
        {
            var doc = app.ActiveUIDocument.Document;
            ProjectUnits.Use(doc);
            return new { document = doc.Title, units = ProjectUnits.Describe() };
        };

        if (!RaiseAndWaitForCompletion(10000))
            throw new CommandExecutionException("Timed out reading project units", ErrorTimeout);
        return handler.Result;
    }
}

/// <summary>
///     Reads parameters of elements: built-in, shared, project and family parameters,
///     instance and type. Values of measurable parameters are in project units.
/// </summary>
public class GetElementParametersCommand : McpCommandBase
{
    private const int MaxElements = 200;

    public GetElementParametersCommand(UIApplication uiApp)
        : base(new DelegateEventHandler("Get element parameters"), uiApp)
    {
    }

    public override string CommandName => "get_element_parameters";

    protected override object ExecuteCommand(JObject parameters, string requestId)
    {
        var ids = parameters["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
        if (ids.Count == 0) throw new ArgumentException("elementIds is required");
        if (ids.Count > MaxElements) throw new ArgumentException($"At most {MaxElements} elements per call");

        var keys = parameters["parameterNames"]?.ToObject<List<string>>() ?? new List<string>();
        var includeType = parameters["includeTypeParameters"]?.Value<bool>() ?? true;
        var kinds = parameters["kinds"]?.ToObject<List<string>>();

        var handler = (DelegateEventHandler)Handler;
        handler.Action = app =>
        {
            var doc = app.ActiveUIDocument.Document;
            var projectParameterIds = ElementParameters.GetProjectParameterIds(doc);
            var elements = new JArray();

            foreach (var id in ids)
            {
                var element = doc.GetElement(id.ToElementId());
                if (element == null)
                {
                    elements.Add(new JObject { ["id"] = id, ["error"] = "Element not found" });
                    continue;
                }

                var list = new JArray();
                var missing = new JArray();
                var type = includeType ? doc.GetElement(element.GetTypeId()) : null;

                if (keys.Count > 0)
                {
                    foreach (var key in keys)
                    {
                        var parameter = ElementParameters.Find(element, key, includeType: false);
                        var isType = false;
                        if (parameter == null && type != null)
                        {
                            parameter = ElementParameters.Find(type, key, includeType: false);
                            isType = parameter != null;
                        }

                        if (parameter == null) missing.Add(key);
                        else list.Add(WithKey(ElementParameters.Describe(parameter, projectParameterIds, isType), key));
                    }
                }
                else
                {
                    AddAll(list, element, projectParameterIds, false, kinds);
                    if (type != null) AddAll(list, type, projectParameterIds, true, kinds);
                }

                var item = new JObject
                {
                    ["id"] = id,
                    ["name"] = element.Name,
                    ["category"] = element.Category?.Name,
                    ["parameters"] = list
                };
                if (missing.Count > 0) item["notFound"] = missing;
                elements.Add(item);
            }

            return new JObject { ["units"] = JToken.FromObject(ProjectUnits.Describe()), ["elements"] = elements };
        };

        if (!RaiseAndWaitForCompletion(30000))
            throw new CommandExecutionException("Timed out reading parameters", ErrorTimeout);
        return handler.Result;
    }

    private static JObject WithKey(JObject description, string key)
    {
        description["key"] = key;
        return description;
    }

    private static void AddAll(JArray list, Element element, ISet<long> projectParameterIds, bool isType,
        ICollection<string> kinds)
    {
        foreach (Parameter parameter in element.GetOrderedParameters())
        {
            if (kinds != null && kinds.Count > 0
                && !kinds.Contains(ElementParameters.GetKind(parameter, projectParameterIds)))
                continue;
            list.Add(ElementParameters.Describe(parameter, projectParameterIds, isType));
        }
    }
}

/// <summary>
///     Sets parameter values in one transaction. Each change addresses a parameter by
///     built-in name, shared parameter GUID or name; numbers are in project units.
/// </summary>
public class SetElementParametersCommand : McpCommandBase
{
    public SetElementParametersCommand(UIApplication uiApp)
        : base(new DelegateEventHandler("Set element parameters"), uiApp)
    {
    }

    public override string CommandName => "set_element_parameters";

    protected override object ExecuteCommand(JObject parameters, string requestId)
    {
        var changes = parameters["changes"] as JArray;
        if (changes == null || changes.Count == 0)
            throw new ArgumentException("changes is required: [{ elementId, parameter, value }]");
        var allowType = parameters["allowTypeParameters"]?.Value<bool>() ?? false;

        var handler = (DelegateEventHandler)Handler;
        handler.Action = app =>
        {
            var doc = app.ActiveUIDocument.Document;
            var results = new JArray();
            var failed = 0;

            using (var transaction = new Transaction(doc, "MCP: set parameters"))
            {
                transaction.Start();
                foreach (var change in changes.OfType<JObject>())
                {
                    var id = change["elementId"]?.Value<long>() ?? 0;
                    var key = change["parameter"]?.ToString();
                    var result = new JObject { ["elementId"] = id, ["parameter"] = key };
                    try
                    {
                        var element = doc.GetElement(id.ToElementId())
                                      ?? throw new InvalidOperationException("Element not found");
                        var parameter = ElementParameters.Find(element, key, includeType: false);
                        if (parameter == null && allowType)
                            parameter = ElementParameters.Find(doc.GetElement(element.GetTypeId()), key, includeType: false);
                        if (parameter == null)
                            throw new InvalidOperationException(allowType
                                ? "Parameter not found"
                                : "Parameter not found on the instance (set allowTypeParameters to change type parameters)");

                        ElementParameters.SetValue(parameter, change["value"] ?? JValue.CreateNull());
                        result["ok"] = true;
                        result["newValue"] = ElementParameters.Describe(parameter, null)["value"];
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
                                                  || ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        failed++;
                        result["ok"] = false;
                        result["error"] = ex.Message;
                    }

                    results.Add(result);
                }

                if (failed == changes.Count)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit rolled back the transaction; see warnings.");
                }
            }

            return new JObject { ["updated"] = changes.Count - failed, ["failed"] = failed, ["results"] = results };
        };

        if (!RaiseAndWaitForCompletion(60000))
            throw new CommandExecutionException("Timed out setting parameters", ErrorTimeout);
        return handler.Result;
    }
}
