using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class SayHelloEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, IMcpEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public Exception LastError { get; private set; }

        public void PrepareForRaise()
        {
            LastError = null;
            _resetEvent.Reset();
        }

        public string Message { get; set; } = "Hello MCP!";

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
        return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                TaskDialog.Show("Revit MCP", Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Say Hello";
        }
    }
}
