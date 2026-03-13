using ObjCRuntime;
using UIKit;

namespace PolyPilot;

public class Program
{
	private static FileStream? _instanceLock;

	// This is the main entry point of the application.
	static void Main(string[] args)
	{
		// Single-instance guard: if another PolyPilot is already running, activate it and exit.
		// This prevents a second instance from launching when the user taps a notification
		// and macOS Launch Services resolves a different .app bundle (e.g. build output vs staging).
		if (!TryAcquireInstanceLock())
		{
			ActivateExistingInstance(args);
			return;
		}

		UIApplication.Main(args, null, typeof(AppDelegate));
	}

	static bool TryAcquireInstanceLock()
	{
		try
		{
			var lockDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".polypilot");
			Directory.CreateDirectory(lockDir);
			var lockPath = Path.Combine(lockDir, "instance.lock");

			_instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			// Write PID so we can identify the owning process
			_instanceLock.SetLength(0);
			using var writer = new StreamWriter(_instanceLock, leaveOpen: true);
			writer.Write(Environment.ProcessId);
			writer.Flush();
			return true;
		}
		catch (IOException)
		{
			// Lock held by another instance
			return false;
		}
		catch
		{
			// If the lock mechanism fails for an unexpected reason (not a contention IOException),
			// fail-closed to prevent a duplicate instance rather than silently allowing it.
			return false;
		}
	}

	static void ActivateExistingInstance(string[] args)
	{
		try
		{
			// If the launch was triggered by a notification tap that included a sessionId,
			// write it to a sidecar file so the running instance can pick it up and navigate.
			// The running instance also writes this file in SendNotificationAsync; this path
			// handles cases where the OS re-launches a different bundle for the same notification.
			var sessionId = ExtractSessionId(args);
			if (sessionId != null)
			{
				try
				{
					var navDir = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
						".polypilot");
					Directory.CreateDirectory(navDir);
					var navPath = Path.Combine(navDir, "pending-navigation.json");
					// Include writtenAt so the 30s TTL in CheckPendingNavigation applies if the
					// AppleScript activation fails and the sidecar is left on disk.
					File.WriteAllText(navPath, System.Text.Json.JsonSerializer.Serialize(new { sessionId, writtenAt = DateTime.UtcNow }));
				}
				catch
				{
					// Best effort — don't block window activation if sidecar write fails
				}
			}

			// Bring the existing PolyPilot window to the foreground via AppleScript
			var psi = new System.Diagnostics.ProcessStartInfo
			{
				FileName = "/usr/bin/osascript",
				UseShellExecute = false,
				CreateNoWindow = true
			};
			psi.ArgumentList.Add("-e");
			psi.ArgumentList.Add("tell application \"System Events\" to tell process \"PolyPilot\" to set frontmost to true");
			System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
		}
		catch
		{
			// Best effort — if activation fails, just exit silently
		}
	}

	// Extract a session ID from launch arguments if present (e.g. --session-id=<id>).
	static string? ExtractSessionId(string[] args)
	{
		foreach (var arg in args)
		{
			const string prefix = "--session-id=";
			if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return arg[prefix.Length..];
		}
		return null;
	}
}