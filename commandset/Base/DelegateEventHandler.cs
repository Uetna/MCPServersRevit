using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Base;

/// <summary>
///     External event handler that runs a delegate on the Revit API thread and keeps its result.
///     Used by small commands that do not need a dedicated handler class.
/// </summary>
public sealed class DelegateEventHandler : IExternalEventHandler, IMcpEventHandler
{
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
    private readonly string _name;

    public DelegateEventHandler(string name)
    {
        _name = name;
    }

    /// <summary>Work to run on the next execution.</summary>
    public Func<UIApplication, object> Action { get; set; }

    public object Result { get; private set; }

    public Exception LastError { get; private set; }

    public void PrepareForRaise()
    {
        LastError = null;
        Result = null;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            if (app.ActiveUIDocument == null)
                throw new InvalidOperationException("No active document in Revit.");
            Result = Action?.Invoke(app);
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => _name;
}
