using System.Globalization;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class GetCurrentViewElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, IMcpEventHandler
    {
        // 默认模型类别列表
        private readonly List<string> _defaultModelCategories = new List<string>
        {
            "OST_Walls",
            "OST_Doors",
            "OST_Windows",
            "OST_Furniture",
            "OST_Columns",
            "OST_Floors",
            "OST_Roofs",
            "OST_Stairs",
            "OST_StructuralFraming",
            "OST_Ceilings",
            "OST_MEPSpaces",
            "OST_Rooms"
        };
        // 默认注释类别列表
        private readonly List<string> _defaultAnnotationCategories = new List<string>
        {
            "OST_Dimensions",
            "OST_TextNotes",
            "OST_GenericAnnotation",
            "OST_WallTags",
            "OST_DoorTags",
            "OST_WindowTags",
            "OST_RoomTags",
            "OST_AreaTags",
            "OST_SpaceTags",
            "OST_ViewportLabels",
            "OST_TitleBlocks"
        };

        // 查询参数
        private List<string> _modelCategoryList;
        private List<string> _annotationCategoryList;
        private bool _includeHidden;
        private int _limit;

        // 执行结果
        public ViewElementsResult ResultInfo { get; private set; }

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public Exception LastError { get; private set; }

        public void PrepareForRaise()
        {
            LastError = null;
            _resetEvent.Reset();
        }

        // 设置查询参数
        public void SetQueryParameters(List<string> modelCategoryList, List<string> annotationCategoryList, bool includeHidden, int limit)
        {
            _modelCategoryList = modelCategoryList;
            _annotationCategoryList = annotationCategoryList;
            _includeHidden = includeHidden;
            _limit = limit;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // 实现IWaitableExternalEventHandler接口
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument
                            ?? throw new InvalidOperationException("No active document in Revit.");
                var doc = uiDoc.Document;
                var activeView = doc.ActiveView;

                // 合并所有类别. Empty lists mean "use the default categories"; the MCP server
                // always sends lists, so checking for null alone never applied the defaults.
                List<string> allCategories = new List<string>();
                allCategories.AddRange(_modelCategoryList ?? new List<string>());
                allCategories.AddRange(_annotationCategoryList ?? new List<string>());
                if (allCategories.Count == 0)
                {
                    allCategories.AddRange(_defaultModelCategories);
                    allCategories.AddRange(_defaultAnnotationCategories);
                }

                var builtInCategories = new List<BuiltInCategory>();
                var unknownCategories = new List<string>();
                foreach (string categoryName in allCategories)
                {
                    if (Enum.TryParse(categoryName, out BuiltInCategory category))
                        builtInCategories.Add(category);
                    else
                        unknownCategories.Add(categoryName);
                }

                if (builtInCategories.Count == 0)
                    throw new ArgumentException($"Unknown categories: {string.Join(", ", unknownCategories)}. Use BuiltInCategory names such as OST_Walls.");

                // One collector pass with the category filter evaluated inside Revit;
                // hidden-element check and limit are applied lazily while enumerating.
                IEnumerable<Element> elements = new FilteredElementCollector(doc, activeView.Id)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(builtInCategories));

                if (!_includeHidden)
                    elements = elements.Where(e => !e.IsHidden(activeView));

                if (_limit > 0)
                    elements = elements.Take(_limit);

                var elementInfos = elements.Select(e => new ElementInfo
                {
                    Id = e.Id.GetValue(),
                    UniqueId = e.UniqueId,
                    Name = e.Name,
                    Category = e.Category?.Name ?? "unknown",
                    Properties = GetElementProperties(doc, e)
                }).ToList();

                ResultInfo = new ViewElementsResult
                {
                    ViewId = activeView.Id.GetValue(),
                    ViewName = activeView.Name,
                    TotalElementsInView = new FilteredElementCollector(doc, activeView.Id).GetElementCount(),
                    FilteredElementCount = elementInfos.Count,
                    Elements = elementInfos
                };
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private const double FeetToMm = 304.8;

        private static string Mm(double feet) => (feet * FeetToMm).ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>
        /// Basic element data. Coordinates and lengths are in millimetres. Parameters are read by
        /// BuiltInParameter, so the result does not depend on the Revit UI language.
        /// </summary>
        private static Dictionary<string, string> GetElementProperties(Document doc, Element element)
        {
            var properties = new Dictionary<string, string>
            {
                ["ElementId"] = element.Id.GetValue().ToString()
            };

            if (element.Location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                properties["LocationX"] = Mm(point.X);
                properties["LocationY"] = Mm(point.Y);
                properties["LocationZ"] = Mm(point.Z);
            }
            else if (element.Location is LocationCurve locationCurve)
            {
                var curve = locationCurve.Curve;
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);
                properties["Start"] = $"{Mm(start.X)}, {Mm(start.Y)}, {Mm(start.Z)}";
                properties["End"] = $"{Mm(end.X)}, {Mm(end.Y)}, {Mm(end.Z)}";
                properties["Length"] = Mm(curve.Length);
            }

            AddString(properties, "Comments", element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            AddString(properties, "Mark", element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK));

            if (element.LevelId != ElementId.InvalidElementId && doc.GetElement(element.LevelId) is Level level)
                properties["Level"] = level.Name;

            if (doc.GetElement(element.GetTypeId()) is ElementType elementType)
            {
                if (!string.IsNullOrEmpty(elementType.FamilyName))
                    properties["Family"] = elementType.FamilyName;
                properties["Type"] = elementType.Name;
            }

            return properties;
        }

        private static void AddString(Dictionary<string, string> properties, string key, Parameter parameter)
        {
            var value = parameter?.AsString();
            if (!string.IsNullOrEmpty(value))
                properties[key] = value;
        }

        public string GetName()
        {
            return "获取当前视图元素";
        }
    }
}
