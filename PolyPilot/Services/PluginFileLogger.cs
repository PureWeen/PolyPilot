using PolyPilot.Provider;

namespace PolyPilot.Services;

/// <summary>
/// File-based plugin logger that writes to ~/.polypilot/logs/plugins/{name}/plugin.log.
/// Rotates at 5 MB to prevent unbounded growth. Thread-safe.
/// </summary>
public class PluginFileLogger : IPluginLogger
{
	private readonly string _logPath;
	private readonly string _pluginName;
	private readonly object _lock = new();
	private const long MaxLogSize = 5 * 1024 * 1024; // 5 MB

	public PluginFileLogger(string pluginName)
	{
		_pluginName = pluginName;
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var dir = Path.Combine(home, ".polypilot", "logs", "plugins", pluginName);
		Directory.CreateDirectory(dir);
		_logPath = Path.Combine(dir, "plugin.log");
	}

	public void Log(PluginLogLevel level, string message, Exception? exception = null)
	{
		var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
		var levelStr = level switch
		{
			PluginLogLevel.Debug => "DBG",
			PluginLogLevel.Info => "INF",
			PluginLogLevel.Warning => "WRN",
			PluginLogLevel.Error => "ERR",
			_ => "???"
		};

		var line = $"{timestamp} [{levelStr}] [{_pluginName}] {message}";
		if (exception != null)
			line += $"\n  Exception: {exception}";
		line += Environment.NewLine;

		lock (_lock)
		{
			try
			{
				// Rotate if too large
				var fi = new FileInfo(_logPath);
				if (fi.Exists && fi.Length > MaxLogSize)
				{
					var backupPath = _logPath + ".1";
					try { File.Delete(backupPath); } catch { }
					try { File.Move(_logPath, backupPath); } catch { }
				}

				File.AppendAllText(_logPath, line);
			}
			catch
			{
				// Logging should never crash the host
			}
		}
	}
}
