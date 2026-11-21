using Microsoft.Extensions.Logging;

namespace HomeAssistantNetDaemon.apps
{
    /// <summary>
    /// Thin wrapper around <see cref="ILogger"/> that prepends a fixed
    /// prefix (e.g. "[Zyxel]") to every message so the call sites stay clean.
    /// </summary>
    internal sealed class PrefixedLog
    {
        private readonly ILogger _logger;
        private readonly string _prefix;

        public PrefixedLog(string prefix, ILogger logger)
        {
            _prefix = prefix;
            _logger = logger;
        }

        public void Info(string message) =>
            _logger.LogInformation("{Prefix} {Message}", _prefix, message);

        public void Warning(string message) =>
            _logger.LogWarning("{Prefix} {Message}", _prefix, message);

        public void Error(string message) =>
            _logger.LogError("{Prefix} {Message}", _prefix, message);
    }
}
