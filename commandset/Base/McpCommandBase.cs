global using RevitMCPCommandSet.Base;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.Exceptions;

namespace RevitMCPCommandSet.Base;

/// <summary>
///     Handler contract used by <see cref="McpCommandBase" />: the command resets the handler
///     before raising the event and reads back the error the handler captured.
/// </summary>
public interface IMcpEventHandler : IWaitableExternalEventHandler
{
    /// <summary>Exception captured during the last execution, or null on success.</summary>
    Exception LastError { get; }

    /// <summary>Clears the completion signal and the last error before a new request is raised.</summary>
    void PrepareForRaise();
}

/// <summary>
///     Base class for all commands of this command set.
///     Fixes the race in <see cref="ExternalEventCommandBase" />.RaiseAndWaitForCompletion
///     (the SDK resets the signal after raising, so a fast handler could be "lost"),
///     checks the result of <see cref="ExternalEvent.Raise" /> and refuses new requests
///     while a previous, timed-out request is still queued or running in Revit.
/// </summary>
public abstract class McpCommandBase : ExternalEventCommandBase
{
    public const int ErrorTimeout = -33001;
    public const int ErrorBusy = -33002;
    public const int ErrorExecution = -33003;

    private volatile bool _abandoned;

    protected McpCommandBase(IWaitableExternalEventHandler handler, UIApplication uiApp)
        : base(handler, uiApp)
    {
    }

    public sealed override object Execute(JObject parameters, string requestId)
    {
        EnsureHandlerIdle();
        return ExecuteCommand(parameters ?? new JObject(), requestId);
    }

    /// <summary>Command body. Parameters are never null.</summary>
    protected abstract object ExecuteCommand(JObject parameters, string requestId);

    /// <summary>
    ///     Raises the external event and waits for the handler.
    ///     Returns false on timeout; throws <see cref="CommandExecutionException" /> when Revit refuses
    ///     the request or the handler reported an error.
    /// </summary>
    protected new bool RaiseAndWaitForCompletion(int timeoutMs = 10000)
    {
        var mcpHandler = Handler as IMcpEventHandler;
        mcpHandler?.PrepareForRaise();

        var request = Event.Raise();
        if (request == ExternalEventRequest.Denied || request == ExternalEventRequest.TimedOut)
            throw new CommandExecutionException(
                $"Revit refused to schedule '{CommandName}' ({request}). Close open dialogs or edit modes in Revit and retry.",
                ErrorBusy);

        if (!Handler.WaitForCompletion(timeoutMs))
        {
            _abandoned = true;
            return false;
        }

        if (mcpHandler?.LastError is { } error)
            throw new CommandExecutionException($"{CommandName} failed: {error.Message}", ErrorExecution);

        return true;
    }

    private void EnsureHandlerIdle()
    {
        if (!_abandoned) return;

        // Handlers no longer reset the signal in WaitForCompletion, so a zero wait tells
        // whether the previously timed-out execution has finished.
        if (Handler.WaitForCompletion(0))
        {
            _abandoned = false;
            return;
        }

        throw new CommandExecutionException(
            $"Previous '{CommandName}' request is still running in Revit. Wait for it to finish and retry.",
            ErrorBusy);
    }
}
