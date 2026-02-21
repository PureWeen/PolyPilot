using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PolyPilot.Services;

/// <summary>
/// Provides the show_image AIFunction for Copilot sessions.
/// When invoked by the model, persists the image to ~/.polypilot/images/ and returns the path.
/// </summary>
public static class ShowImageTool
{
    public const string ToolName = "show_image";

    private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff", ".svg" };

    private static string? _imagesDir;
    private static string ImagesDir => _imagesDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".polypilot", "images");

    /// <summary>Creates the show_image AIFunction to register on SessionConfig.Tools.</summary>
    public static AIFunction CreateFunction()
    {
        return AIFunctionFactory.Create(
            ShowImageAsync,
            nameof(ShowImageAsync),
            "Display an image to the user in the chat. Use this when you want to show the user a screenshot, diagram, or any image as part of the conversation. Accepts either a file path or base64-encoded image data.");
    }

    [Description("Display an image to the user in the chat UI")]
    private static async Task<string> ShowImageAsync(
        [Description("Absolute path to an image file on disk")] string? path = null,
        [Description("Base64-encoded image data (alternative to path)")] string? base64_data = null,
        [Description("MIME type when using base64_data (e.g. image/png)")] string? mime_type = null,
        [Description("Optional caption to display below the image")] string? caption = null)
    {
        try
        {
            // Validate input — need either path or base64
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(base64_data))
                return JsonSerializer.Serialize(new { error = "Either 'path' or 'base64_data' must be provided" });

            Directory.CreateDirectory(ImagesDir);
            string persistentPath;

            if (!string.IsNullOrEmpty(path))
            {
                // File path mode
                if (!File.Exists(path))
                    return JsonSerializer.Serialize(new { error = $"File not found: {path}" });

                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext))
                    return JsonSerializer.Serialize(new { error = $"Unsupported image format: {ext}" });

                // Copy to persistent storage
                persistentPath = Path.Combine(ImagesDir, $"{Guid.NewGuid()}{ext}");
                await Task.Run(() => File.Copy(path, persistentPath));
            }
            else
            {
                // Base64 mode
                var mimeType = mime_type ?? "image/png";
                var ext = MimeToExtension(mimeType);
                persistentPath = Path.Combine(ImagesDir, $"{Guid.NewGuid()}{ext}");
                var bytes = Convert.FromBase64String(base64_data!);
                await File.WriteAllBytesAsync(persistentPath, bytes);
            }

            return JsonSerializer.Serialize(new
            {
                displayed = true,
                persistent_path = persistentPath,
                caption = caption ?? ""
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Parses the show_image tool result to extract persistent path and caption.</summary>
    public static (string? path, string? caption) ParseResult(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            var path = root.TryGetProperty("persistent_path", out var p) ? p.GetString() : null;
            var caption = root.TryGetProperty("caption", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(caption)) caption = null;
            return (path, caption);
        }
        catch { return (null, null); }
    }

    private static string MimeToExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/svg+xml" => ".svg",
        _ => ".png"
    };
}
