namespace AutoPilot.App.Services;

public static class FolderPickerService
{
    public static Task<string?> PickFolderAsync()
    {
        // Folder picking not supported on Android yet
        return Task.FromResult<string?>(null);
    }
}
