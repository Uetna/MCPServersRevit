using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// TCP JSON-RPC endpoint for the MCP server.
    /// Protocol: one JSON object per line (NDJSON, UTF-8). A request without a trailing
    /// newline is still accepted once it forms a complete JSON object (legacy clients).
    /// Requests are executed one at a time, because Revit runs them on its single UI thread.
    /// </summary>
    public class SocketService
    {
        public const int DefaultPort = 8964;
        private const int MaxMessageChars = 64 * 1024 * 1024;

        private static SocketService _instance;
        private readonly object _clientsLock = new object();
        private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        private readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);
        private TcpListener _listener;
        private Thread _listenerThread;
        private volatile bool _isRunning;
        private bool _isInitialized;
        private int _port = DefaultPort;
        private UIApplication _uiApp;
        private readonly ICommandRegistry _commandRegistry;
        private readonly Logger _logger;
        private CommandExecutor _commandExecutor;
        private RevitFailureCollector _failureCollector;

        public static SocketService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SocketService();
                return _instance;
            }
        }

        private SocketService()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        // 初始化
        // Initialization. Commands are loaded once per Revit session; toggling the
        // service off and on again must not re-register commands or external events.
        public void Initialize(UIApplication uiApp)
        {
            if (_isInitialized) return;

            _uiApp = uiApp;

            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            _logger.Info("Current Revit version: {0}", versionAdapter.GetRevitVersion());

            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            // 加载配置并注册命令
            // Load configuration and register commands.
            var configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();

            var settings = configManager.Config?.Settings;
            _port = settings != null && settings.Port > 0 ? settings.Port : DefaultPort;
            if (settings != null)
                _logger.SetLevel(settings.LogLevel);

            var commandManager = new CommandManager(_commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            // Warnings raised by MCP transactions must never open modal dialogs.
            _failureCollector = new RevitFailureCollector(_logger);
            _uiApp.Application.FailuresProcessing += _failureCollector.OnFailuresProcessing;

            _isInitialized = true;
            _logger.Info("Socket service initialized on 127.0.0.1:{0}", _port);
        }

        /// <summary>
        /// Starts listening. Throws when the port is unavailable so the caller can report it.
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            try
            {
                // Loopback only: send_code_to_revit executes arbitrary C#, so the endpoint
                // must not be reachable from the network.
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                _listener = null;
                _logger.Error("Cannot listen on 127.0.0.1:{0}: {1}", _port, ex.Message);
                throw new InvalidOperationException(
                    $"Port {_port} is not available ({ex.SocketErrorCode}). Another application may be using it; change \"settings.port\" in commandRegistry.json.",
                    ex);
            }

            _isRunning = true;
            _listenerThread = new Thread(ListenForClients)
            {
                IsBackground = true,
                Name = "RevitMCP listener"
            };
            _listenerThread.Start();
            _logger.Info("Socket service started on 127.0.0.1:{0}", _port);
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;

            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                _logger.Warning("Error stopping listener: {0}", ex.Message);
            }
            _listener = null;

            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    try { client.Close(); } catch { /* already closed */ }
                }
                _clients.Clear();
            }

            if (_listenerThread != null && _listenerThread.IsAlive)
                _listenerThread.Join(1000);

            _logger.Info("Socket service stopped");
        }

        private void ListenForClients()
        {
            while (_isRunning)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    // Listener stopped.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("Accept failed: {0}", ex.Message);
                    continue;
                }

                client.NoDelay = true;
                lock (_clientsLock) _clients.Add(client);

                var clientThread = new Thread(HandleClientCommunication)
                {
                    IsBackground = true,
                    Name = "RevitMCP client"
                };
                clientThread.Start(client);
            }
        }

        private void HandleClientCommunication(object clientObj)
        {
            var tcpClient = (TcpClient)clientObj;

            try
            {
                using (var stream = tcpClient.GetStream())
                {
                    var decoder = new UTF8Encoding(false).GetDecoder();
                    var bytes = new byte[64 * 1024];
                    var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                    var pending = new StringBuilder();

                    while (_isRunning)
                    {
                        int bytesRead;
                        try
                        {
                            bytesRead = stream.Read(bytes, 0, bytes.Length);
                        }
                        catch (IOException)
                        {
                            break;
                        }

                        if (bytesRead == 0) break;

                        int charCount = decoder.GetChars(bytes, 0, bytesRead, chars, 0);
                        pending.Append(chars, 0, charCount);

                        if (pending.Length > MaxMessageChars)
                        {
                            _logger.Error("Message exceeds {0} chars, closing connection", MaxMessageChars);
                            WriteResponse(stream, CreateErrorResponse(null, JsonRPCErrorCodes.InvalidRequest, "Message too large"));
                            break;
                        }

                        foreach (var message in ExtractMessages(pending))
                        {
                            WriteResponse(stream, ProcessJsonRPCRequest(message));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Client connection closed with error: {0}", ex.Message);
            }
            finally
            {
                lock (_clientsLock) _clients.Remove(tcpClient);
                tcpClient.Close();
            }
        }

        /// <summary>
        /// Takes complete messages out of the buffer: every newline-terminated line, plus a
        /// trailing unterminated chunk if it already is a complete JSON object (legacy clients).
        /// </summary>
        internal static List<string> ExtractMessages(StringBuilder pending)
        {
            var messages = new List<string>();
            var text = pending.ToString();
            int start = 0;
            int newline;

            while ((newline = text.IndexOf('\n', start)) >= 0)
            {
                var line = text.Substring(start, newline - start).Trim();
                if (line.Length > 0) messages.Add(line);
                start = newline + 1;
            }

            var rest = text.Substring(start).Trim();
            if (rest.Length > 0 && rest.EndsWith("}") && IsCompleteJson(rest))
            {
                messages.Add(rest);
                start = text.Length;
            }

            pending.Remove(0, start);
            return messages;
        }

        private static bool IsCompleteJson(string text)
        {
            try
            {
                JToken.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static void WriteResponse(Stream stream, string response)
        {
            var data = Encoding.UTF8.GetBytes(response + "\n");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private string ProcessJsonRPCRequest(string requestJson)
        {
            JsonRPCRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);
            }
            catch (JsonException)
            {
                return CreateErrorResponse(null, JsonRPCErrorCodes.ParseError, "Invalid JSON");
            }

            if (request == null || !request.IsValid())
                return CreateErrorResponse(null, JsonRPCErrorCodes.InvalidRequest, "Invalid JSON-RPC request");

            // Revit executes external events one at a time on its UI thread; serialising here
            // keeps command handlers (which hold per-request state) from being overwritten.
            _executionGate.Wait();
            try
            {
                using (var scope = _failureCollector?.BeginScope())
                {
                    var response = _commandExecutor.ExecuteCommand(request);
                    return AttachWarnings(response, scope?.Messages);
                }
            }
            finally
            {
                _executionGate.Release();
            }
        }

        /// <summary>Adds Revit warnings collected during the request as an extra "warnings" member.</summary>
        private static string AttachWarnings(string response, IReadOnlyCollection<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return response;

            var json = JObject.Parse(response);
            json["warnings"] = JArray.FromObject(warnings);
            return json.ToString(Formatting.None);
        }

        private static string CreateErrorResponse(string id, int code, string message)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message
                }
            };

            return response.ToJson();
        }
    }
}
