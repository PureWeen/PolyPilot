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

		// One-time migration from non-sandboxed data to sandbox container.
		MigrateLegacyDataIfNeeded();

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

	/// <summary>
	/// One-time migration from non-sandboxed (~/.polypilot/, ~/.copilot/) to the sandbox
	/// container. When the App Store (sandboxed) build launches for the first time, HOME
	/// is remapped to ~/Library/Containers/&lt;bundle-id&gt;/Data/. If the user previously
	/// ran a sideloaded/dev build, their data lives at the real home. This copies it into
	/// the container so settings, sessions, and chat history are preserved.
	///
	/// Best-effort: if the sandbox blocks access to the real home directory, we skip
	/// gracefully. The marker file is only written after a fully successful migration,
	/// so blocked or partial migrations retry safely on subsequent launches.
	/// </summary>
	static void MigrateLegacyDataIfNeeded()
	{
		try
		{
			var containerHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			// Detect sandbox: the container path contains /Library/Containers/<id>/Data
			const string containerMarker = "/Library/Containers/";
			var containersIdx = containerHome.IndexOf(containerMarker, StringComparison.Ordinal);
			if (containersIdx < 0)
				return; // Not sandboxed — nothing to migrate

			var realHome = containerHome[..containersIdx];
			var containerPolypilot = Path.Combine(containerHome, ".polypilot");

			// Check if we already attempted migration
			var markerFile = Path.Combine(containerPolypilot, ".sandbox-migrated");
			if (File.Exists(markerFile))
				return;

			Directory.CreateDirectory(containerPolypilot);

			int copiedFiles = 0;
			bool hadErrors = false;

			// Migrate .polypilot/ (settings, organization, sessions, chat history)
			var legacyPolypilot = Path.Combine(realHome, ".polypilot");
			if (Directory.Exists(legacyPolypilot))
				CopyDirectoryRecursive(legacyPolypilot, containerPolypilot, ref copiedFiles, ref hadErrors);

			// Migrate .copilot/ (SDK session state — events.jsonl files)
			var legacyCopilot = Path.Combine(realHome, ".copilot");
			var containerCopilot = Path.Combine(containerHome, ".copilot");
			if (Directory.Exists(legacyCopilot))
			{
				Directory.CreateDirectory(containerCopilot);
				CopyDirectoryRecursive(legacyCopilot, containerCopilot, ref copiedFiles, ref hadErrors);
			}

			// Only write marker when files were actually copied and no errors occurred.
			// This keeps retries safe (don't-clobber semantics) and avoids permanently
			// sealing a failed or blocked migration.
			if (copiedFiles > 0 && !hadErrors)
				File.WriteAllText(markerFile, $"Migrated {copiedFiles} files at {DateTime.UtcNow:O}");
		}
		catch
		{
			// Best-effort: the sandbox may block access to the real home directory.
			// Never block app startup for migration.
		}
	}

	/// <summary>
	/// Recursively copies directory contents without overwriting existing files.
	/// Preserves the "don't clobber" invariant so container data always wins if
	/// the user has already created new data in the sandboxed location.
	/// Skips symlinks to avoid infinite recursion (StackOverflowException is uncatchable).
	/// </summary>
	static void CopyDirectoryRecursive(string source, string destination, ref int copiedFiles, ref bool hadErrors, int depth = 0)
	{
		const int maxDepth = 32;
		if (depth >= maxDepth)
			return;

		foreach (var file in Directory.GetFiles(source))
		{
			var destFile = Path.Combine(destination, Path.GetFileName(file));
			if (!File.Exists(destFile))
			{
				try
				{
					File.Copy(file, destFile);
					copiedFiles++;
				}
				catch { hadErrors = true; }
			}
		}

		foreach (var dir in Directory.GetDirectories(source))
		{
			// Skip symlinks to prevent infinite recursion from circular links
			var dirInfo = new DirectoryInfo(dir);
			if (dirInfo.LinkTarget != null)
				continue;

			var destDir = Path.Combine(destination, Path.GetFileName(dir));
			try
			{
				Directory.CreateDirectory(destDir);
				CopyDirectoryRecursive(dir, destDir, ref copiedFiles, ref hadErrors, depth + 1);
			}
			catch { hadErrors = true; }
		}
	}
}
