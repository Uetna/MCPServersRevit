using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Keeps Revit failure dialogs from blocking MCP requests.
    /// While a request scope is active, warnings are dismissed and errors silently roll the
    /// transaction back; every message is collected and returned to the MCP client.
    /// Outside a scope (normal user work) Revit's default behaviour is untouched.
    /// </summary>
    public class RevitFailureCollector
    {
        private readonly Logger _logger;
        private volatile Scope _activeScope;

        public RevitFailureCollector(Logger logger)
        {
            _logger = logger;
        }

        public Scope BeginScope()
        {
            var scope = new Scope(this);
            _activeScope = scope;
            return scope;
        }

        public void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            var scope = _activeScope;
            if (scope == null) return;

            try
            {
                var accessor = e.GetFailuresAccessor();
                bool hasErrors = false;

                foreach (var message in accessor.GetFailureMessages())
                {
                    var severity = message.GetSeverity();
                    var text = message.GetDescriptionText();

                    if (severity == FailureSeverity.Warning)
                    {
                        accessor.DeleteWarning(message);
                        scope.Add($"Warning: {text}");
                    }
                    else
                    {
                        hasErrors = true;
                        scope.Add($"{severity}: {text}");
                    }
                }

                if (hasErrors)
                {
                    var options = accessor.GetFailureHandlingOptions();
                    options.SetClearAfterRollback(true);
                    accessor.SetFailureHandlingOptions(options);
                    e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
                    scope.Add($"Transaction '{accessor.GetTransactionName()}' was rolled back because of the errors above.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failure processing error: {0}", ex.Message);
            }
        }

        private void EndScope(Scope scope)
        {
            if (ReferenceEquals(_activeScope, scope))
                _activeScope = null;

            foreach (var message in scope.Messages)
                _logger.Warning("Revit: {0}", message);
        }

        public sealed class Scope : IDisposable
        {
            private readonly RevitFailureCollector _owner;
            private readonly List<string> _messages = new List<string>();

            internal Scope(RevitFailureCollector owner)
            {
                _owner = owner;
            }

            public IReadOnlyCollection<string> Messages
            {
                get { lock (_messages) return _messages.ToArray(); }
            }

            internal void Add(string message)
            {
                lock (_messages) _messages.Add(message);
            }

            public void Dispose() => _owner.EndScope(this);
        }
    }
}
