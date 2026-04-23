using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Driver;

namespace PolyPilot.IntegrationTests.Fixtures;

/// <summary>
/// Base class for all PolyPilot integration tests.
/// Provides AgentClient and helpers for UI interaction via DevFlow.
/// </summary>
public abstract class IntegrationTestBase
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    protected AppFixture App { get; }
    protected AgentClient Client => App.Client;
    protected HttpClient Http => App.Http;
    protected ITestOutputHelper Output { get; }

    protected IntegrationTestBase(AppFixture app, ITestOutputHelper output)
    {
        App = app;
        Output = output;
    }

    /// <summary>GET a JSON endpoint and parse the response.</summary>
    protected async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
    }

    /// <summary>Evaluate JavaScript in the Blazor WebView via CDP.</summary>
    protected async Task<string> CdpEvalAsync(string expression, bool awaitPromise = false)
    {
        // Use direct HTTP POST to /api/cdp — the Driver's SendCdpCommandAsync
        // uses a different route that may not be available on all agent versions.
        var paramsObj = new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        };
        if (awaitPromise)
            paramsObj["awaitPromise"] = true;

        var payload = new JsonObject
        {
            ["method"] = "Runtime.evaluate",
            ["params"] = paramsObj,
        };

        var response = await Http.PostAsync("/api/cdp",
            new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
            // CDP response: {"id":N,"result":{"result":{"type":"string","value":"..."}}}
            if (json.TryGetProperty("result", out var outer) &&
                outer.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var val))
                return val.ToString();
            // Flat: {"result":{"value":"..."}}
            if (json.TryGetProperty("result", out var flat) &&
                flat.TryGetProperty("value", out var flatVal))
                return flatVal.ToString();

            Output.WriteLine($"[CDP] Unexpected response: {body[..Math.Min(body.Length, 200)]}");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[CDP] Parse error: {ex.Message}, body: {body[..Math.Min(body.Length, 200)]}");
        }
        return "";
    }

    /// <summary>Wait for CDP to become ready (WebView may need time to initialize).</summary>
    protected async Task WaitForCdpReadyAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout.Value);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var status = await GetJsonAsync("/api/status");
                if (status.TryGetProperty("cdpReady", out var cdp) && cdp.GetBoolean())
                    return;
            }
            catch { }
            await Task.Delay(1000, cts.Token);
        }
        throw new TimeoutException("CDP did not become ready");
    }

    /// <summary>Click an element by CSS selector.</summary>
    protected async Task<string> ClickAsync(string selector)
    {
        var js = $"const el = document.querySelector(\"{EscapeJs(selector)}\"); el?.click(); el ? 'clicked' : 'not found'";
        return await CdpEvalAsync(js);
    }

    /// <summary>Set an input's value and fire Blazor change events.</summary>
    protected async Task<string> FillInputAsync(string selector, string value)
    {
        var js = $@"const el = document.querySelector(""{EscapeJs(selector)}""); 
            if (el) {{ el.value = '{EscapeJs(value)}'; 
            el.dispatchEvent(new Event('input', {{bubbles:true}})); 
            el.dispatchEvent(new Event('change', {{bubbles:true}})); 'set'; }} else {{ 'not found'; }}";
        return await CdpEvalAsync(js);
    }

    /// <summary>Select a dropdown value and fire change event.</summary>
    protected async Task<string> SelectAsync(string selector, string value)
    {
        var js = $@"const el = document.querySelector(""{EscapeJs(selector)}""); 
            if (el) {{ el.value = '{EscapeJs(value)}'; 
            el.dispatchEvent(new Event('change', {{bubbles:true}})); 'selected'; }} else {{ 'not found'; }}";
        return await CdpEvalAsync(js);
    }

    /// <summary>Check if an element exists in the DOM.</summary>
    protected async Task<bool> ExistsAsync(string selector)
    {
        var result = await CdpEvalAsync(
            $"document.querySelector(\"{EscapeJs(selector)}\") !== null ? 'true' : 'false'");
        return result == "true";
    }

    /// <summary>Get text content of an element.</summary>
    protected async Task<string> GetTextAsync(string selector)
    {
        return await CdpEvalAsync(
            $"document.querySelector(\"{EscapeJs(selector)}\")?.textContent?.trim() || ''");
    }

    /// <summary>Wait for an element to appear in the DOM.</summary>
    protected async Task<bool> WaitForAsync(string selector, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(15);
        using var cts = new CancellationTokenSource(timeout.Value);
        while (!cts.IsCancellationRequested)
        {
            if (await ExistsAsync(selector))
                return true;
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    /// <summary>Navigate by clicking a sidebar link.</summary>
    protected async Task<bool> NavigateToAsync(string linkText, string pageSelector, TimeSpan? timeout = null)
    {
        var js = $@"const link = [...document.querySelectorAll('a')].find(a => 
            a.textContent?.includes('{EscapeJs(linkText)}')); link?.click(); 
            link ? 'clicked' : 'not found'";
        var result = await CdpEvalAsync(js);
        Output.WriteLine($"Navigate to '{linkText}': {result}");

        if (result == "not found")
            return false;

        return await WaitForAsync(pageSelector, timeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>Take a screenshot for debugging.</summary>
    protected async Task ScreenshotAsync(string? label = null)
    {
        try
        {
            var response = await Http.GetAsync("/api/screenshot");
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Output.WriteLine($"📸 Screenshot '{label}': {bytes.Length} bytes");
            }
            else
            {
                Output.WriteLine($"📸 Screenshot '{label}': failed ({response.StatusCode})");
            }
        }
        catch (Exception ex)
        {
            Output.WriteLine($"📸 Screenshot '{label}': error ({ex.Message})");
        }
    }

    private static string EscapeJs(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "\\'");
}
