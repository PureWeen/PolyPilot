using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Tests that verify QR code images and the token value in Settings are blurred
/// by default, each controlled by an independent toggle (showQrCode,
/// showDirectQrCode, showToken). Since these are Blazor components, we verify
/// the source markup contracts.
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
    public void QrCodeImages_UseBlurredClassFromIndependentQrToggles()
    {
        var razorContent = File.ReadAllText(GetSettingsRazorPath());

        // Each QR code img tag must use its own independent toggle for the blurred class.
        // Use [^>]* between attributes so harmless additions/reordering don't break the test.
        var tunnelQrPattern = new Regex(@"<img\b[^>]*src=""@qrCodeDataUri""[^>]*class=""@\(showQrCode \? """" : ""blurred""\)""");
        var directQrPattern = new Regex(@"<img\b[^>]*src=""@directQrCodeDataUri""[^>]*class=""@\(showDirectQrCode \? """" : ""blurred""\)""");

        Assert.True(tunnelQrPattern.IsMatch(razorContent),
            "Tunnel QR code <img> must use showQrCode for the blurred class.");
        Assert.True(directQrPattern.IsMatch(razorContent),
            "Direct QR code <img> must use showDirectQrCode for the blurred class.");
    }

    [Fact]
    public void TokenValue_UsesBlurredClassFromShowTokenToggle()
    {
        var razorContent = File.ReadAllText(GetSettingsRazorPath());

        // Token code element must specifically use showToken toggle (independent from QR toggles)
        Assert.Matches(@"class=""token-value @\(showToken \? """" : ""blurred""\)""", razorContent);
    }

    [Fact]
    public void AllBlurToggles_DefaultFalse()
    {
        var razorContent = File.ReadAllText(GetSettingsRazorPath());

        // showToken should be declared as bool (defaults to false)
        Assert.Matches(@"private\s+bool\s+showToken\s*;", razorContent);
        Assert.DoesNotMatch(@"private\s+bool\s+showToken\s*=\s*true", razorContent);

        // QR code toggles must also default to false
        Assert.Matches(@"private\s+bool\s+showQrCode\s*;", razorContent);
        Assert.Matches(@"private\s+bool\s+showDirectQrCode\s*;", razorContent);
        Assert.DoesNotMatch(@"private\s+bool\s+showQrCode\s*=\s*true", razorContent);
        Assert.DoesNotMatch(@"private\s+bool\s+showDirectQrCode\s*=\s*true", razorContent);
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
