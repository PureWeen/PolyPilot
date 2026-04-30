using PolyPilot.IntegrationTests.Fixtures;

namespace PolyPilot.IntegrationTests;

/// <summary>
/// Integration tests verifying that the polypilot-interop.js module is loaded
/// and all named functions are available on window, replacing eval-based interop.
/// </summary>
[Collection("PolyPilot")]
[Trait("Category", "JsInterop")]
public class JsInteropModuleTests : IntegrationTestBase
{
    public JsInteropModuleTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task InteropModule_AllCoreFunctionsRegistered()
    {
        await WaitForCdpReadyAsync();

        // Verify that polypilot-interop.js loaded and core functions are on window
        var functions = new[]
        {
            "setDataTheme", "setDataPlatform", "setAppFontSize",
            "startSidebarResize", "blurActiveElement",
            "clearSettingsSearchInput", "scrollToSettingsCategory",
            "wireSettingsSearch", "setupCategoryIntersectionObserver",
            "removeSettingsContentActiveClass", "clearSettingsRef",
            "ensureDashboardKeyHandlers", "clearDashRef",
            "ensureTextareaAutoResize", "setDashboardScrollTop",
            "getDashboardScrollTop", "ensureLoadMoreObserver",
            "captureDrafts", "scrollMessagesToBottom",
            "focusAndSelect", "saveDraftsAndCursor",
            "setInputValueAndCursor", "showPopup",
            "showAgentsPopup", "showPromptsPopup",
            "wireSessionNameInputEnter", "clearSidebarRef",
            "invokeDashboardCollapseToGrid", "scrollAndFocusCommentBox",
            "clearPromptRef",
            "__setNavRef", "__setDashRef", "__setSettingsRef",
            "__setSidebarRef", "__ppSetRef",
        };

        var checkExpr = "JSON.stringify([" +
            string.Join(",", functions.Select(f => $"typeof window.{f} === 'function'")) +
            "])";

        var result = await CdpEvalAsync(checkExpr);
        Output.WriteLine($"Function check result: {result}");

        // Parse the JSON array of booleans
        var results = System.Text.Json.JsonSerializer.Deserialize<bool[]>(result) ?? Array.Empty<bool>();
        Assert.Equal(functions.Length, results.Length);

        for (int i = 0; i < functions.Length; i++)
        {
            Assert.True(results[i], $"window.{functions[i]} should be a function");
        }
    }

    [Fact]
    public async Task InteropModule_NoEvalInRazorComponents()
    {
        // Structural scan: verify no Razor components still use eval-based JS interop.
        // This catches regressions where new code introduces InvokeVoidAsync("eval", ...).
        var componentsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "PolyPilot", "Components");
        if (!Directory.Exists(componentsDir))
        {
            // Fallback for CI where source layout may differ
            componentsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "PolyPilot", "Components");
        }

        if (Directory.Exists(componentsDir))
        {
            var razorFiles = Directory.GetFiles(componentsDir, "*.razor", SearchOption.AllDirectories);
            Assert.NotEmpty(razorFiles); // Sanity check: we found Razor files

            var violations = new List<string>();
            foreach (var file in razorFiles)
            {
                var content = await File.ReadAllTextAsync(file);
                if (content.Contains("InvokeVoidAsync(\"eval\"") || content.Contains("InvokeAsync<string>(\"eval\""))
                {
                    violations.Add(Path.GetFileName(file));
                }
            }
            Assert.True(violations.Count == 0,
                $"Razor files still using eval-based JS interop: {string.Join(", ", violations)}. " +
                "Use named functions from polypilot-interop.js instead.");
        }
        else
        {
            // If source not available, fall back to runtime check
            await WaitForCdpReadyAsync();
            var result = await CdpEvalAsync("typeof window.focusAndSelect === 'function' ? 'ok' : 'missing'");
            Assert.Equal("ok", result);
        }
    }

    [Fact]
    public async Task InteropModule_SetDataTheme_Works()
    {
        await WaitForCdpReadyAsync();

        // Call setDataTheme and verify it sets the data-theme attribute
        await CdpEvalAsync("window.setDataTheme('dark')");
        var theme = await CdpEvalAsync("document.documentElement.getAttribute('data-theme')");
        Assert.Equal("dark", theme);

        // Change it to light
        await CdpEvalAsync("window.setDataTheme('light')");
        theme = await CdpEvalAsync("document.documentElement.getAttribute('data-theme')");
        Assert.Equal("light", theme);
    }

    [Fact]
    public async Task InteropModule_SetAppFontSize_Works()
    {
        await WaitForCdpReadyAsync();

        // Call setAppFontSize and verify CSS variable is set
        await CdpEvalAsync("window.setAppFontSize(16)");
        var size = await CdpEvalAsync(
            "getComputedStyle(document.documentElement).getPropertyValue('--app-font-size').trim()");
        Assert.Equal("16px", size);
    }

    [Fact]
    public async Task InteropModule_FocusAndSelect_NoThrow()
    {
        await WaitForCdpReadyAsync();

        // focusAndSelect with non-existent element should not throw
        var result = await CdpEvalAsync(
            "(function() { try { window.focusAndSelect('nonexistent-id'); return 'ok'; } catch(e) { return 'error:' + e.message; } })()");
        Assert.Equal("ok", result);
    }
}
