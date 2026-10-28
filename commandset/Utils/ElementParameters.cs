using System.Globalization;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Utils;

/// <summary>
///     Finds, reads and writes element parameters of every kind:
///     built-in (system) parameters, shared parameters (identified by GUID), project parameters
///     (bound through Manage &gt; Project Parameters) and family parameters.
///     A parameter key can be:
///     <list type="bullet">
///         <item>a BuiltInParameter name, e.g. "ALL_MODEL_MARK" (language independent);</item>
///         <item>a shared parameter GUID, e.g. "9b2f1c9e-...";</item>
///         <item>a parameter name as shown in Revit, e.g. "ADSK_Марка" or "Комментарии".</item>
///     </list>
///     Instance parameters are searched first, then the element type.
///     Numeric values are exchanged in the project display units of the parameter.
/// </summary>
public static class ElementParameters
{
    public const string KindBuiltIn = "builtIn";
    public const string KindShared = "shared";
    public const string KindProject = "project";
    public const string KindFamily = "family";

    /// <summary>Finds a parameter on the element or, when <paramref name="includeType" />, on its type.</summary>
    public static Parameter Find(Element element, string key, bool includeType = true)
    {
        if (element == null || string.IsNullOrWhiteSpace(key)) return null;

        var parameter = FindOn(element, key.Trim());
        if (parameter != null || !includeType) return parameter;

        var type = element.Document.GetElement(element.GetTypeId());
        return type == null ? null : FindOn(type, key.Trim());
    }

    private static Parameter FindOn(Element element, string key)
    {
        if (Guid.TryParse(key, out var guid))
            return element.get_Parameter(guid);

        if (key.IndexOf('_') > 0 && Enum.TryParse(key, false, out BuiltInParameter builtIn)
                                 && Enum.IsDefined(typeof(BuiltInParameter), builtIn))
        {
            var byBuiltIn = element.get_Parameter(builtIn);
            if (byBuiltIn != null) return byBuiltIn;
        }

        // LookupParameter returns the first match; prefer a parameter that has a value when
        // several parameters share the name (e.g. a shared and a project parameter).
        Parameter first = null;
        foreach (Parameter parameter in element.GetParameters(key))
        {
            if (parameter.HasValue) return parameter;
            first ??= parameter;
        }

        return first;
    }

    /// <summary>Returns "builtIn", "shared", "project" or "family".</summary>
    public static string GetKind(Parameter parameter, ISet<long> projectParameterIds)
    {
        if (parameter.Definition is InternalDefinition internalDefinition
            && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
            return KindBuiltIn;
        if (parameter.IsShared) return KindShared;
        if (projectParameterIds != null && projectParameterIds.Contains(parameter.Id.GetValue())) return KindProject;
        return KindFamily;
    }

    /// <summary>Ids of the parameter elements bound as project parameters (shared ones included).</summary>
    public static HashSet<long> GetProjectParameterIds(Document doc)
    {
        var ids = new HashSet<long>();
        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is InternalDefinition definition)
                ids.Add(definition.Id.GetValue());
        }

        return ids;
    }

    /// <summary>Describes a parameter and its value in project units.</summary>
    public static JObject Describe(Parameter parameter, ISet<long> projectParameterIds, bool isTypeParameter = false)
    {
        var result = new JObject
        {
            ["name"] = parameter.Definition?.Name,
            ["kind"] = GetKind(parameter, projectParameterIds),
            ["storageType"] = parameter.StorageType.ToString(),
            ["isReadOnly"] = parameter.IsReadOnly,
            ["isType"] = isTypeParameter,
            ["hasValue"] = parameter.HasValue
        };

        if (parameter.Definition is InternalDefinition internalDefinition
            && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
            result["builtInParameter"] = internalDefinition.BuiltInParameter.ToString();
        if (parameter.IsShared)
            result["guid"] = parameter.GUID.ToString();

        switch (parameter.StorageType)
        {
            case StorageType.Double:
                var unit = GetUnit(parameter, out var toProjectUnits);
                result["value"] = Math.Round(toProjectUnits(parameter.AsDouble()), 6);
                if (unit != null) result["unit"] = unit.Unit;
                break;
            case StorageType.Integer:
                result["value"] = parameter.AsInteger();
                break;
            case StorageType.String:
                result["value"] = parameter.AsString();
                break;
            case StorageType.ElementId:
                var id = parameter.AsElementId();
                result["value"] = id.GetValue();
                if (id != ElementId.InvalidElementId && parameter.Element?.Document.GetElement(id) is { } referenced)
                    result["elementName"] = referenced.Name;
                break;
        }

        var display = parameter.AsValueString();
        if (!string.IsNullOrEmpty(display)) result["displayValue"] = display;
        return result;
    }

    /// <summary>
    ///     Sets a parameter. Numbers for Double parameters are taken in the project display units of
    ///     the parameter; a string for a non-text parameter is parsed like user input in Revit
    ///     (SetValueString), so "1200" or "1,2 m" both work.
    /// </summary>
    public static void SetValue(Parameter parameter, JToken value)
    {
        if (parameter.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{parameter.Definition?.Name}' is read-only.");

        bool ok;
        switch (parameter.StorageType)
        {
            case StorageType.String:
                ok = parameter.Set(value.Type == JTokenType.Null ? string.Empty : value.ToString());
                break;
            case StorageType.Double when IsNumber(value):
                GetUnit(parameter, out var toProjectUnits, out var toInternal);
                ok = parameter.Set(toInternal(value.Value<double>()));
                break;
            case StorageType.Integer when IsNumber(value) || value.Type == JTokenType.Boolean:
                ok = parameter.Set(value.Type == JTokenType.Boolean ? (value.Value<bool>() ? 1 : 0) : value.Value<int>());
                break;
            case StorageType.ElementId when IsNumber(value):
                ok = parameter.Set(value.Value<long>().ToElementId());
                break;
            default:
                ok = parameter.SetValueString(value.ToString());
                break;
        }

        if (!ok)
            throw new InvalidOperationException(
                $"Revit rejected value '{value}' for parameter '{parameter.Definition?.Name}' ({parameter.StorageType}).");
    }

    private static bool IsNumber(JToken value) =>
        value.Type == JTokenType.Integer || value.Type == JTokenType.Float;

    private static ProjectUnits.UnitInfo GetUnit(Parameter parameter, out Func<double, double> fromInternal) =>
        GetUnit(parameter, out fromInternal, out _);

    /// <summary>Unit of a Double parameter in the project display settings, or null for unitless numbers.</summary>
    private static ProjectUnits.UnitInfo GetUnit(Parameter parameter, out Func<double, double> fromInternal,
        out Func<double, double> toInternal)
    {
        fromInternal = v => v;
        toInternal = v => v;
        try
        {
#if REVIT2021_OR_GREATER
            var unitTypeId = parameter.GetUnitTypeId();
            if (unitTypeId == null || string.IsNullOrEmpty(unitTypeId.TypeId)) return null;
            fromInternal = v => UnitUtils.ConvertFromInternalUnits(v, unitTypeId);
            toInternal = v => UnitUtils.ConvertToInternalUnits(v, unitTypeId);
            return ProjectUnits.DescribeUnit(unitTypeId);
#else
            var displayUnits = parameter.DisplayUnitType;
            if (displayUnits == DisplayUnitType.DUT_UNDEFINED || displayUnits == DisplayUnitType.DUT_CUSTOM) return null;
            fromInternal = v => UnitUtils.ConvertFromInternalUnits(v, displayUnits);
            toInternal = v => UnitUtils.ConvertToInternalUnits(v, displayUnits);
            return ProjectUnits.DescribeUnit(displayUnits);
#endif
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Unitless numeric parameter (e.g. a plain number): the internal value is the value.
            return null;
        }
    }

    /// <summary>Formats a parameter for compact element listings (string properties).</summary>
    public static string ToDisplayString(Parameter parameter)
    {
        if (parameter == null || !parameter.HasValue) return null;
        if (parameter.StorageType == StorageType.String) return parameter.AsString();
        if (parameter.StorageType == StorageType.Double)
        {
            GetUnit(parameter, out var fromInternal);
            return Math.Round(fromInternal(parameter.AsDouble()), 6).ToString(CultureInfo.InvariantCulture);
        }

        return parameter.AsValueString() ?? (parameter.StorageType == StorageType.Integer
            ? parameter.AsInteger().ToString(CultureInfo.InvariantCulture)
            : parameter.AsElementId().GetValue().ToString(CultureInfo.InvariantCulture));
    }
}
