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
			ActivateExistingInstance();
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
			// If the lock mechanism fails (permissions, etc.), allow this instance to start
			return true;
		}
	}

	static void ActivateExistingInstance()
	{
		try
		{
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
}