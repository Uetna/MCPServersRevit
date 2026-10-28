using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.Exceptions;
using System;
using System.Diagnostics;
using System.Reflection;

namespace revit_mcp_plugin.Core
{
    public class CommandExecutor
    {
        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;

        public CommandExecutor(ICommandRegistry commandRegistry, ILogger logger)
        {
            _commandRegistry = commandRegistry;
            _logger = logger;
        }

        /// <summary>
        /// Executes a Revit command declared inside a JSON-RPC request.
        /// </summary>
        /// <param name="request">A JSON-RPC request.</param>
        /// <returns>Serialized JSON-RPC response.</returns>
        public string ExecuteCommand(JsonRPCRequest request)
        {
            // 查找命令
            // Find command
            if (!_commandRegistry.TryGetCommand(request.Method, out var command))
            {
                _logger.Warning("Command not found: {0}", request.Method);
                return CreateErrorResponse(request.Id,
                    JsonRPCErrorCodes.MethodNotFound,
                    $"Method not found: '{request.Method}'");
            }

            var stopwatch = Stopwatch.StartNew();
            _logger.Info("Executing command: {0} (id {1})", request.Method, request.Id);

            // 执行命令
            // Execute command
            try
            {
                object result = command.Execute(request.GetParamsObject(), request.Id);
                _logger.Info("Command {0} succeeded in {1} ms", request.Method, stopwatch.ElapsedMilliseconds);

                return CreateSuccessResponse(request.Id, result);
            }
            catch (Exception ex)
            {
                var root = Unwrap(ex);
                var commandError = ex as CommandExecutionException ?? root as CommandExecutionException;
                var message = BuildMessage(ex, root);

                _logger.Error("Command {0} failed in {1} ms: {2}\n{3}",
                    request.Method, stopwatch.ElapsedMilliseconds, message, root.StackTrace);

                return commandError != null
                    ? CreateErrorResponse(request.Id, commandError.ErrorCode, message, commandError.ErrorData)
                    : CreateErrorResponse(request.Id, JsonRPCErrorCodes.InternalError, message);
            }
        }

        /// <summary>Strips reflection/aggregate wrappers that hide the real error message.</summary>
        private static Exception Unwrap(Exception ex)
        {
            while ((ex is TargetInvocationException || ex is AggregateException) && ex.InnerException != null)
                ex = ex.InnerException;
            return ex;
        }

        /// <summary>
        /// Commands often re-wrap exceptions as "X failed: {inner.Message}"; keep that context
        /// but append the root cause when it is not already part of the message.
        /// </summary>
        private static string BuildMessage(Exception ex, Exception root)
        {
            if (ReferenceEquals(ex, root) || ex.Message.Contains(root.Message))
                return ex.Message;
            return $"{ex.Message} -> {root.GetType().Name}: {root.Message}";
        }

        private string CreateSuccessResponse(string id, object result)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = result == null ? JValue.CreateNull() : result is JToken jToken ? jToken : JToken.FromObject(result)
            };

            return response.ToJson();
        }

        private string CreateErrorResponse(string id, int code, string message, object data = null)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message,
                    Data = data != null ? JToken.FromObject(data) : null
                }
            };

            return response.ToJson();
        }
    }
}
