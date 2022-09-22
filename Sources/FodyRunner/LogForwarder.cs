
#nullable enable

namespace Malimbe.FodyRunner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Xml.Linq;

    internal sealed class LogForwarder : MarshalByRefObject, global::ILogger
    {
        private static readonly string[] _configurationElementSplitSeparators =
        {
            ",", " ", "\t", "\n", "\r", "\r\n"
        };

        private readonly ILogger _logger;
        private string? _currentWeaverName;
        private LogLevel _logLevel = LogLevel.None;

        public bool ErrorOccurred { get; private set; }

        public LogForwarder(ILogger logger) =>
            _logger = logger;

        public void SetLogLevelFromConfiguration(IEnumerable<XElement> elements) =>
            _logLevel = elements?.Elements(nameof(LogLevel))
                    .SelectMany(
                        element => element.Value.Split(
                            _configurationElementSplitSeparators,
                            StringSplitOptions.RemoveEmptyEntries))
                    .Select(value => value.Trim())
                    .Select(value => Enum.TryParse(value, true, out LogLevel level) ? level : LogLevel.None)
                    .Aggregate(LogLevel.None, (level1, level2) => level1 | level2)
                ?? LogLevel.None;

        public void ClearWeaverName() =>
            _currentWeaverName = string.Empty;

        public void LogDebug(string message) =>
            Log(LogLevel.Debug, message);

        public void LogError(
            string message,
            string? file,
            int lineNumber,
            int columnNumber,
            int endLineNumber,
            int endColumnNumber) =>
            Log(LogLevel.Error, message);

        public void LogError(string message) =>
            Log(LogLevel.Error, message);

        public void LogInfo(string message) =>
            Log(LogLevel.Info, message);

        public void LogMessage(string message, int level) =>
            Log(LogLevel.Info, message);

        public void LogWarning(string message, string? code = "") =>
            Log(LogLevel.Warning, message);

        public void LogWarning(
            string message,
            string? file,
            int lineNumber,
            int columnNumber,
            int endLineNumber,
            int endColumnNumber,
            string? code = "") =>
            Log(LogLevel.Warning, message);

        public void SetCurrentWeaverName(string weaverName) =>
            _currentWeaverName = weaverName;

        private void Log(LogLevel logLevel, string message)
        {
            if (_logLevel.HasFlag(logLevel))
            {
                _logger.Log(logLevel, _currentWeaverName == null ? message : $"{_currentWeaverName}: {message}");
            }
        }
    }
}
