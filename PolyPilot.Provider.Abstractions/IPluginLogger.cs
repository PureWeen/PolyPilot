namespace PolyPilot.Provider;

/// <summary>
/// Simple logger interface for session provider plugins.
/// Implementations write to ~/.polypilot/logs/plugins/{name}/plugin.log.
/// </summary>
public interface IPluginLogger
{
	void Log(PluginLogLevel level, string message, Exception? exception = null);
}

public enum PluginLogLevel
{
	Debug,
	Info,
	Warning,
	Error,
}

public static class PluginLoggerExtensions
{
	public static void Debug(this IPluginLogger logger, string message) =>
		logger.Log(PluginLogLevel.Debug, message);

	public static void Info(this IPluginLogger logger, string message) =>
		logger.Log(PluginLogLevel.Info, message);

	public static void Warning(this IPluginLogger logger, string message) =>
		logger.Log(PluginLogLevel.Warning, message);

	public static void Warning(this IPluginLogger logger, string message, Exception ex) =>
		logger.Log(PluginLogLevel.Warning, message, ex);

	public static void Error(this IPluginLogger logger, string message) =>
		logger.Log(PluginLogLevel.Error, message);

	public static void Error(this IPluginLogger logger, string message, Exception ex) =>
		logger.Log(PluginLogLevel.Error, message, ex);
}
