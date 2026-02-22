using Microsoft.Extensions.Logging;

namespace Scalae.Logging
{
    /// <summary>
    /// Small abstraction over ILoggerFactory so application classes can depend on a single
    /// interface instead of the concrete logging factory. Keeps call sites easy to mock/test.
    /// </summary>
    public interface ILoggingService
    {
        ILogger<T> CreateLogger<T>();
        ILogger CreateLogger(string categoryName);
    }
}