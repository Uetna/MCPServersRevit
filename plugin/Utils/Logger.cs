using RevitMCPSDK.API.Interfaces;
using System;
using System.IO;

namespace revit_mcp_plugin.Utils
{
    public class Logger : ILogger
    {
        private static readonly object FileLock = new object();
        private readonly string _logsDirectory;
        private LogLevel _currentLogLevel = LogLevel.Info;

        public Logger()
        {
            _logsDirectory = PathManager.GetLogsDirectoryPath();
        }

        /// <summary>Sets the minimum level from a configuration value ("Debug", "Info", ...). Unknown values are ignored.</summary>
        public void SetLevel(string level)
        {
            if (!string.IsNullOrWhiteSpace(level) && Enum.TryParse(level, true, out LogLevel parsed))
                _currentLogLevel = parsed;
        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            if (level < _currentLogLevel)
                return;

            string formattedMessage;
            try
            {
                formattedMessage = args != null && args.Length > 0 ? string.Format(message, args) : message;
            }
            catch (FormatException)
            {
                // A malformed format string must never break the caller.
                formattedMessage = message + " | " + string.Join(", ", args);
            }

            var now = DateTime.Now;
            string logEntry = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {formattedMessage}";

            // 输出到 Debug 窗口
            // Output to debug window.
            System.Diagnostics.Debug.WriteLine(logEntry);

            // 写入日志文件 (one file per day, writes from several threads are serialised)
            // Write to the logfile.
            try
            {
                var logFilePath = Path.Combine(_logsDirectory, $"mcp_{now:yyyyMMdd}.log");
                lock (FileLock)
                {
                    File.AppendAllText(logFilePath, logEntry + Environment.NewLine);
                }
            }
            catch
            {
                // 如果写入日志文件失败，不抛出异常
                // If writing to the logfile fails, do not throw an exception.
            }
        }

        public void Debug(string message, params object[] args)
        {
            Log(LogLevel.Debug, message, args);
        }

        public void Info(string message, params object[] args)
        {
            Log(LogLevel.Info, message, args);
        }

        public void Warning(string message, params object[] args)
        {
            Log(LogLevel.Warning, message, args);
        }

        public void Error(string message, params object[] args)
        {
            Log(LogLevel.Error, message, args);
        }
    }
}
