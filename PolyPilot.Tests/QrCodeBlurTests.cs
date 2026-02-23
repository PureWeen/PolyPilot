using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Tests that verify the QR code images in Settings are blurred by default
/// (when showToken is false), matching the token's blur behavior.
/// Since these are Blazor components, we verify the source markup contracts.
/// </summary>
public class QrCodeBlurTests
{
    private static string GetSettingsRazorPath()
    {
        // Navigate from test project to main project
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "PolyPilot", "Components", "Pages", "Settings.razor");
    }

    private static string GetSettingsCssPath()
    {
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "PolyPilot", "Components", "Pages", "Settings.razor.css");
    }

    [Fact]
    public void QrCodeImages_UseBlurredClassFromShowTokenToggle()
    {
        var razorContent = File.ReadAllText(GetSettingsRazorPath());

        // Both QR code img tags should use the showToken toggle for blurred class
        var qrImgPattern = new Regex(@"<img\s+src=""@(qrCodeDataUri|directQrCodeDataUri)""\s+alt=""QR Code""\s+class=""@\(showToken \? """" : ""blurred""\)""");
        var matches = qrImgPattern.Matches(razorContent);

        Assert.True(matches.Count >= 2,
            $"Expected at least 2 QR code <img> tags with showToken-based blur class, found {matches.Count}. " +
            "Both tunnel and direct QR codes must be blurred when token is hidden.");
    }

    [Fact]
    public void TokenValue_UsesBlurredClassFromShowTokenToggle()
    {
        var razorContent = File.ReadAllText(GetSettingsRazorPath());

        // Token code element should also use showToken toggle
        Assert.Contains(@"@(showToken ? """" : ""blurred"")", razorContent);
    }

    [Fact]
    public void ShowToken_DefaultsFalse()
    {
        var razorContent = File.ReadAllText(GetSettingsRazorPath());

        // showToken should be declared as bool (defaults to false)
        Assert.Matches(@"private\s+bool\s+showToken\s*;", razorContent);

        // It should NOT be initialized to true
        Assert.DoesNotMatch(@"private\s+bool\s+showToken\s*=\s*true", razorContent);
    }

    [Fact]
    public void Css_HasBlurredStyleForQrCodeImages()
    {
        var cssContent = File.ReadAllText(GetSettingsCssPath());

        // CSS must define blur styles for QR code images
        Assert.Contains(".qr-code img.blurred", cssContent);
        Assert.Contains("filter: blur(", cssContent);
    }

    [Fact]
    public void Css_QrCodeImageHasTransition()
    {
        var cssContent = File.ReadAllText(GetSettingsCssPath());

        // QR code img should have a transition for smooth blur toggle
        Assert.Contains("transition: filter", cssContent);
    }

    [Fact]
    public void Css_BlurredQrCodeHasHoverReveal()
    {
        var cssContent = File.ReadAllText(GetSettingsCssPath());

        // Blurred QR should partially reveal on hover (like the token does)
        Assert.Contains(".qr-code img.blurred:hover", cssContent);
    }
}
