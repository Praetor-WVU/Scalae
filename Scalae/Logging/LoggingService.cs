using Microsoft.Extensions.Logging;

namespace Scalae.Logging
{
    /// <summary>
    /// Implementation of ILoggingService that wraps ILoggerFactory.
    /// </summary>
    public class LoggingService : ILoggingService
    {
        private readonly ILoggerFactory _loggerFactory;

        public LoggingService(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        public ILogger<T> CreateLogger<T>()
        {
            return _loggerFactory.CreateLogger<T>();
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggerFactory.CreateLogger(categoryName);
        }
    }
}
