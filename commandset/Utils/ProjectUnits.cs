using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Utils;

/// <summary>
///     Conversion between Revit internal units (feet, square feet, cubic feet, radians) and the
///     display units configured in the active project (Manage &gt; Project Units).
///     All numbers exchanged with the MCP server are in project units, so a metric project in
///     millimetres keeps the previous behaviour and a project in metres or feet works without
///     guessing. The settings are read once per request: <see cref="McpCommandBase" /> calls
///     <see cref="Reset" /> before raising the external event, and the first conversion inside the
///     handler reads them on the Revit API thread.
/// </summary>
public static class ProjectUnits
{
    private static UIApplication _uiApp;
    private static Snapshot _current;

    /// <summary>Remembers the application so the active document can be resolved lazily.</summary>
    public static void Attach(UIApplication uiApp)
    {
        if (uiApp != null) _uiApp = uiApp;
    }

    /// <summary>Forgets the cached settings; the next conversion re-reads them.</summary>
    public static void Reset() => _current = null;

    /// <summary>Uses the units of a specific document until the next <see cref="Reset" />.</summary>
    public static void Use(Document doc) => _current = Snapshot.Read(doc);

    private static Snapshot Current
    {
        get
        {
            if (_current != null) return _current;
            Document doc = null;
            try
            {
                doc = _uiApp?.ActiveUIDocument?.Document;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Called outside the API context; fall back to millimetres.
            }

            _current = Snapshot.Read(doc);
            return _current;
        }
    }

    public static double ToInternalLength(double value) => value * Current.LengthToInternal;
    public static double FromInternalLength(double feet) => feet / Current.LengthToInternal;
    public static double ToInternalArea(double value) => value * Current.AreaToInternal;
    public static double FromInternalArea(double squareFeet) => squareFeet / Current.AreaToInternal;
    public static double ToInternalVolume(double value) => value * Current.VolumeToInternal;
    public static double FromInternalVolume(double cubicFeet) => cubicFeet / Current.VolumeToInternal;
    public static double ToInternalAngle(double value) => value * Current.AngleToInternal;
    public static double FromInternalAngle(double radians) => radians / Current.AngleToInternal;

    public static XYZ ToInternal(double x, double y, double z) =>
        new XYZ(ToInternalLength(x), ToInternalLength(y), ToInternalLength(z));

    /// <summary>Length unit name, e.g. "millimeters", "meters", "feetFractionalInches".</summary>
    public static string LengthUnit => Current.Length.Unit;

    /// <summary>Description of the active project units for tool responses.</summary>
    public static object Describe()
    {
        var current = Current;
        return new
        {
            length = current.Length,
            area = current.Area,
            volume = current.Volume,
            angle = current.Angle,
            note = "All lengths, coordinates, areas, volumes and angles sent to or returned by the MCP tools use these project units."
        };
    }

    public sealed class UnitInfo
    {
        public string Unit { get; set; }
        public string Label { get; set; }
        public string TypeId { get; set; }
    }

    private sealed class Snapshot
    {
        public double LengthToInternal = 1 / 304.8;
        public double AreaToInternal = 1 / (304.8 * 304.8);
        public double VolumeToInternal = 1 / (304.8 * 304.8 * 304.8);
        public double AngleToInternal = Math.PI / 180;
        public UnitInfo Length = new UnitInfo { Unit = "millimeters", Label = "mm", TypeId = "" };
        public UnitInfo Area = new UnitInfo { Unit = "squareMillimeters", Label = "mm²", TypeId = "" };
        public UnitInfo Volume = new UnitInfo { Unit = "cubicMillimeters", Label = "mm³", TypeId = "" };
        public UnitInfo Angle = new UnitInfo { Unit = "degrees", Label = "°", TypeId = "" };

        public static Snapshot Read(Document doc)
        {
            var snapshot = new Snapshot();
            if (doc == null) return snapshot;

            var units = doc.GetUnits();
#if REVIT2021_OR_GREATER
            Read(units, SpecTypeId.Length, ref snapshot.LengthToInternal, ref snapshot.Length);
            Read(units, SpecTypeId.Area, ref snapshot.AreaToInternal, ref snapshot.Area);
            Read(units, SpecTypeId.Volume, ref snapshot.VolumeToInternal, ref snapshot.Volume);
            Read(units, SpecTypeId.Angle, ref snapshot.AngleToInternal, ref snapshot.Angle);
#else
            Read(units, UnitType.UT_Length, ref snapshot.LengthToInternal, ref snapshot.Length);
            Read(units, UnitType.UT_Area, ref snapshot.AreaToInternal, ref snapshot.Area);
            Read(units, UnitType.UT_Volume, ref snapshot.VolumeToInternal, ref snapshot.Volume);
            Read(units, UnitType.UT_Angle, ref snapshot.AngleToInternal, ref snapshot.Angle);
#endif
            return snapshot;
        }

#if REVIT2021_OR_GREATER
        private static void Read(Units units, ForgeTypeId spec, ref double toInternal, ref UnitInfo info)
        {
            var unitTypeId = units.GetFormatOptions(spec).GetUnitTypeId();
            toInternal = UnitUtils.ConvertToInternalUnits(1.0, unitTypeId);
            info = Describe(unitTypeId);
        }

        public static UnitInfo Describe(ForgeTypeId unitTypeId)
        {
            return new UnitInfo
            {
                Unit = ShortName(unitTypeId.TypeId),
                Label = LabelUtils.GetLabelForUnit(unitTypeId),
                TypeId = unitTypeId.TypeId
            };
        }

        // "autodesk.unit.unit:millimeters-1.0.1" -> "millimeters"
        private static string ShortName(string typeId)
        {
            var name = typeId;
            var colon = name.LastIndexOf(':');
            if (colon >= 0) name = name.Substring(colon + 1);
            var dash = name.IndexOf('-');
            return dash > 0 ? name.Substring(0, dash) : name;
        }
#else
        private static void Read(Units units, UnitType unitType, ref double toInternal, ref UnitInfo info)
        {
            var displayUnits = units.GetFormatOptions(unitType).DisplayUnits;
            toInternal = UnitUtils.ConvertToInternalUnits(1.0, displayUnits);
            info = Describe(displayUnits);
        }

        public static UnitInfo Describe(DisplayUnitType displayUnits)
        {
            return new UnitInfo
            {
                Unit = displayUnits.ToString(),
                Label = LabelUtils.GetLabelFor(displayUnits),
                TypeId = displayUnits.ToString()
            };
        }
#endif
    }

#if REVIT2021_OR_GREATER
    /// <summary>Unit description for a specific unit (used for parameter values).</summary>
    public static UnitInfo DescribeUnit(ForgeTypeId unitTypeId) => Snapshot.Describe(unitTypeId);
#else
    public static UnitInfo DescribeUnit(DisplayUnitType displayUnits) => Snapshot.Describe(displayUnits);
#endif
}
