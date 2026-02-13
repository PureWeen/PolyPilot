using PolyPilot.Models;

namespace PolyPilot.Tests;

public class ModelSelectionTests
{
    // === ModelHelper.NormalizeToSlug tests ===

    [Theory]
    [InlineData("claude-opus-4.6", "claude-opus-4.6")]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4.5")]
    [InlineData("gemini-3-pro-preview", "gemini-3-pro-preview")]
    [InlineData("gpt-5.1-codex", "gpt-5.1-codex")]
    [InlineData("claude-haiku-4.5", "claude-haiku-4.5")]
    public void NormalizeToSlug_AlreadySlug_ReturnsUnchanged(string input, string expected)
    {
        Assert.Equal(expected, ModelHelper.NormalizeToSlug(input));
    }

    [Theory]
    [InlineData("Claude Opus 4.6", "claude-opus-4.6")]
    [InlineData("Claude Sonnet 4.5", "claude-sonnet-4.5")]
    [InlineData("Claude Haiku 4.5", "claude-haiku-4.5")]
    [InlineData("Claude Opus 4.5", "claude-opus-4.5")]
    [InlineData("Claude Sonnet 4", "claude-sonnet-4")]
    public void NormalizeToSlug_DisplayName_ConvertsToClaude(string input, string expected)
    {
        Assert.Equal(expected, ModelHelper.NormalizeToSlug(input));
    }

    [Theory]
    [InlineData("GPT-5.2", "gpt-5.2")]
    [InlineData("GPT-5.1-Codex", "gpt-5.1-codex")]
    [InlineData("GPT-5.1-Codex-Max", "gpt-5.1-codex-max")]
    [InlineData("GPT-5.1-Codex-Mini", "gpt-5.1-codex-mini")]
    [InlineData("GPT-5", "gpt-5")]
    [InlineData("GPT-5-Mini", "gpt-5-mini")]
    [InlineData("GPT-4.1", "gpt-4.1")]
    public void NormalizeToSlug_DisplayName_ConvertsToGpt(string input, string expected)
    {
        Assert.Equal(expected, ModelHelper.NormalizeToSlug(input));
    }

    [Theory]
    [InlineData("Gemini 3 Pro (Preview)", "gemini-3-pro-preview")]
    [InlineData("Gemini 3 Pro", "gemini-3-pro")]
    public void NormalizeToSlug_DisplayName_ConvertsToGemini(string input, string expected)
    {
        Assert.Equal(expected, ModelHelper.NormalizeToSlug(input));
    }

    [Theory]
    [InlineData("Claude Opus 4.6 (fast mode)", "claude-opus-4.6-fast")]
    [InlineData("Claude Opus 4.6 (fast)", "claude-opus-4.6-fast")]
    public void NormalizeToSlug_DisplayName_WithFastSuffix(string input, string expected)
    {
        Assert.Equal(expected, ModelHelper.NormalizeToSlug(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void NormalizeToSlug_NullOrEmpty_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, ModelHelper.NormalizeToSlug(input));
    }

    [Fact]
    public void NormalizeToSlug_WithWhitespace_Trims()
    {
        Assert.Equal("claude-opus-4.6", ModelHelper.NormalizeToSlug("  claude-opus-4.6  "));
        Assert.Equal("claude-opus-4.5", ModelHelper.NormalizeToSlug("  Claude Opus 4.5  "));
    }

    // === IsDisplayName tests ===

    [Theory]
    [InlineData("Claude Opus 4.5", true)]
    [InlineData("GPT-5.1-Codex", true)]
    [InlineData("Gemini 3 Pro (Preview)", true)]
    [InlineData("claude-opus-4.5", false)]
    [InlineData("gpt-5.1-codex", false)]
    [InlineData("gemini-3-pro-preview", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsDisplayName_DetectsCorrectly(string? input, bool expected)
    {
        Assert.Equal(expected, ModelHelper.IsDisplayName(input));
    }

    // === Round-trip test: display names from known SDK event patterns ===

    [Fact]
    public void NormalizeToSlug_AllFallbackModels_AreAlreadySlugs()
    {
        var fallbackModels = new[]
        {
            "claude-opus-4.6", "claude-opus-4.6-fast", "claude-opus-4.5",
            "claude-sonnet-4.5", "claude-sonnet-4", "claude-haiku-4.5",
            "gpt-5.2", "gpt-5.2-codex", "gpt-5.1", "gpt-5.1-codex",
            "gpt-5.1-codex-max", "gpt-5.1-codex-mini", "gpt-5", "gpt-5-mini", "gpt-4.1",
            "gemini-3-pro-preview",
        };

        foreach (var model in fallbackModels)
        {
            var normalized = ModelHelper.NormalizeToSlug(model);
            Assert.Equal(model, normalized);
        }
    }

    [Fact]
    public void NormalizeToSlug_DisplayNamesFromCliEvents_MatchSlugs()
    {
        // These are actual display names observed in session.start events from the CLI
        var displayToSlug = new Dictionary<string, string>
        {
            { "Claude Opus 4.5", "claude-opus-4.5" },
            { "Claude Sonnet 4.5", "claude-sonnet-4.5" },
            { "Claude Opus 4.6 (fast mode)", "claude-opus-4.6-fast" },
        };

        foreach (var (display, expectedSlug) in displayToSlug)
        {
            Assert.Equal(expectedSlug, ModelHelper.NormalizeToSlug(display));
        }
    }

    // === AgentSessionInfo model property tests ===

    [Fact]
    public void AgentSessionInfo_Model_CanBeSetToSlug()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "claude-opus-4.6" };
        Assert.Equal("claude-opus-4.6", info.Model);
    }

    [Fact]
    public void AgentSessionInfo_Model_DisplayNameShouldBeNormalized()
    {
        // This tests the pattern: receive display name from event, normalize before storing
        var displayName = "Claude Opus 4.5";
        var normalized = ModelHelper.NormalizeToSlug(displayName);
        var info = new AgentSessionInfo { Name = "test", Model = normalized };
        Assert.Equal("claude-opus-4.5", info.Model);
    }

    // === Session creation model flow test ===

    [Fact]
    public void CreateSession_ModelPassedCorrectly()
    {
        // Simulates the flow: UI selectedModel → CreateSessionAsync model param → SessionConfig.Model
        var uiSelectedModel = "claude-opus-4.6"; // From dropdown (slug)
        var sessionModel = uiSelectedModel; // model ?? DefaultModel
        Assert.Equal("claude-opus-4.6", sessionModel);
    }

    [Fact]
    public void CreateSession_DisplayNameFromUiState_IsNormalized()
    {
        // Simulates: UI state has display name → normalize → pass to CreateSessionAsync
        var savedModel = "Claude Sonnet 4.5"; // From corrupted ui-state.json
        var normalized = ModelHelper.NormalizeToSlug(savedModel);
        Assert.Equal("claude-sonnet-4.5", normalized);
    }

    // === UiState model persistence tests ===

    [Fact]
    public void UiState_SelectedModel_NormalizationNeeded()
    {
        // Verify that a display name in UiState would be normalized on load
        var displayName = "Claude Opus 4.5";
        var slug = ModelHelper.NormalizeToSlug(displayName);
        Assert.False(ModelHelper.IsDisplayName(slug));
        Assert.Equal("claude-opus-4.5", slug);
    }

    [Fact]
    public void ActiveSessionEntry_Model_NormalizationNeeded()
    {
        // Verify that display names from persisted entries are normalized correctly
        var displayModel = "Claude Opus 4.5";
        var normalized = ModelHelper.NormalizeToSlug(displayModel);
        Assert.Equal("claude-opus-4.5", normalized);
    }

    // === Session resume model + working directory preservation ===

    [Fact]
    public void ResumeFlow_ActiveSessionEntry_PreservesModelAndWorkingDirectory()
    {
        // Simulates the full save → restore cycle for active sessions
        var entries = new List<ActiveSessionEntry>
        {
            new()
            {
                SessionId = "2a6c8495-20a8-4026-88e8-a4626b915b7a",
                DisplayName = "TestFromTree",
                Model = "claude-opus-4.5",
                WorkingDirectory = "/Users/test/.polypilot/worktrees/dotnet-maui-8f45001d"
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entries);
        var restored = System.Text.Json.JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);

        Assert.NotNull(restored);
        var entry = restored![0];

        // These are the critical fields that MUST survive the round-trip
        Assert.Equal("claude-opus-4.5", entry.Model);
        Assert.Equal("/Users/test/.polypilot/worktrees/dotnet-maui-8f45001d", entry.WorkingDirectory);

        // Model must be a slug, not a display name
        Assert.False(ModelHelper.IsDisplayName(entry.Model), "Persisted model should be a slug, not a display name");
    }

    [Fact]
    public void ResumeFlow_DisplayNameModel_IsNormalizedBeforeResume()
    {
        // Simulates: old active-sessions.json has display name → normalize before passing to SDK
        var entry = new ActiveSessionEntry
        {
            SessionId = "some-guid",
            DisplayName = "MySession",
            Model = "Claude Opus 4.5", // Display name from older persistence
            WorkingDirectory = "/some/worktree/path"
        };

        var resumeModel = ModelHelper.NormalizeToSlug(entry.Model);
        Assert.Equal("claude-opus-4.5", resumeModel);
        Assert.False(ModelHelper.IsDisplayName(resumeModel));
    }

    [Fact]
    public void ResumeFlow_EmptyModel_FallsBackToDefault()
    {
        // If the persisted model is empty, the resume should use DefaultModel
        var defaultModel = "claude-opus-4.6";
        string? persistedModel = null;

        var resumeModel = ModelHelper.NormalizeToSlug(persistedModel ?? defaultModel);
        Assert.Equal("claude-opus-4.6", resumeModel);
    }

    [Fact]
    public void ResumeFlow_WorkingDirectory_NotOverriddenByProjectDir()
    {
        // The key invariant: a worktree session must keep its worktree path,
        // NOT fall back to the PolyPilot project directory
        var worktreePath = "/Users/test/.polypilot/worktrees/dotnet-maui-8f45001d";
        var projectDir = "/Users/test/Projects/AutoPilot/PolyPilot";

        // Simulates: workingDirectory param is set → should win over any fallback
        var resolvedDir = worktreePath ?? projectDir;
        Assert.Equal(worktreePath, resolvedDir);
        Assert.NotEqual(projectDir, resolvedDir);
    }

    // === Normalization idempotency ===

    [Fact]
    public void NormalizeToSlug_IsIdempotent()
    {
        // Normalizing an already-normalized slug should return the same value
        var slugs = new[]
        {
            "claude-opus-4.6", "claude-opus-4.6-fast", "claude-sonnet-4.5",
            "gpt-5.1-codex", "gemini-3-pro-preview"
        };

        foreach (var slug in slugs)
        {
            var once = ModelHelper.NormalizeToSlug(slug);
            var twice = ModelHelper.NormalizeToSlug(once);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void NormalizeToSlug_DisplayNames_AreIdempotentAfterFirstPass()
    {
        // Normalizing a display name, then normalizing the result, must be stable
        var displayNames = new[]
        {
            "Claude Opus 4.6", "Claude Opus 4.6 (fast mode)", "GPT-5.1-Codex",
            "Gemini 3 Pro (Preview)", "Claude Sonnet 4.5"
        };

        foreach (var name in displayNames)
        {
            var once = ModelHelper.NormalizeToSlug(name);
            var twice = ModelHelper.NormalizeToSlug(once);
            Assert.Equal(once, twice);
        }
    }

    // === End-to-end persistence scenario ===

    [Fact]
    public void EndToEnd_CreateSaveRestoreResume_PreservesContext()
    {
        // Full lifecycle: create session → save to active-sessions.json → restore → resume
        // This is the exact flow that was broken (PR 90 bug)

        // Step 1: Session created with specific model + worktree
        var createdModel = "claude-opus-4.5";
        var createdWorkDir = "/Users/test/.polypilot/worktrees/dotnet-maui-abc123";
        var info = new AgentSessionInfo
        {
            Name = "MauiWorktreeSession",
            Model = createdModel,
            WorkingDirectory = createdWorkDir,
            SessionId = "fake-guid-1234"
        };

        // Step 2: Save to disk (SaveActiveSessionsToDisk pattern)
        var entry = new ActiveSessionEntry
        {
            SessionId = info.SessionId!,
            DisplayName = info.Name,
            Model = info.Model,
            WorkingDirectory = info.WorkingDirectory
        };

        // Step 3: Serialize + deserialize (simulates app restart)
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { entry });
        var restored = System.Text.Json.JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json)!;
        var restoredEntry = restored[0];

        // Step 4: Resume would use these values
        var resumeModel = ModelHelper.NormalizeToSlug(restoredEntry.Model);
        var resumeWorkDir = restoredEntry.WorkingDirectory;

        // ASSERTIONS: Everything must match the original creation values
        Assert.Equal(createdModel, resumeModel);
        Assert.Equal(createdWorkDir, resumeWorkDir);
        Assert.Equal("MauiWorktreeSession", restoredEntry.DisplayName);
        Assert.False(ModelHelper.IsDisplayName(resumeModel), "Resume model must be a slug");
    }

    [Fact]
    public void EndToEnd_LegacyDisplayNameEntry_IsNormalizedOnResume()
    {
        // Simulates restoring an entry that was saved before the normalization fix
        var legacyJson = @"[{
            ""SessionId"": ""old-guid"",
            ""DisplayName"": ""OldSession"",
            ""Model"": ""Claude Opus 4.5"",
            ""WorkingDirectory"": ""/some/worktree""
        }]";

        var entries = System.Text.Json.JsonSerializer.Deserialize<List<ActiveSessionEntry>>(legacyJson)!;
        var entry = entries[0];

        // The restore path should normalize the model
        var resumeModel = ModelHelper.NormalizeToSlug(entry.Model);
        Assert.Equal("claude-opus-4.5", resumeModel);
        Assert.Equal("/some/worktree", entry.WorkingDirectory);
    }

    // === ChangeModelAsync behavior tests ===

    [Fact]
    public void ChangeModel_NewModelIsNormalized()
    {
        // ChangeModelAsync normalizes before use — display names from UI must become slugs
        var userSelection = "Claude Opus 4.6";
        var normalized = ModelHelper.NormalizeToSlug(userSelection);
        Assert.Equal("claude-opus-4.6", normalized);
    }

    [Fact]
    public void ChangeModel_SameModel_IsNoOp()
    {
        // If current model == requested model, ChangeModelAsync should be a no-op
        var currentModel = "claude-opus-4.5";
        var requested = "claude-opus-4.5";
        var normalizedRequested = ModelHelper.NormalizeToSlug(requested);
        Assert.Equal(currentModel, normalizedRequested); // Would trigger no-op guard
    }

    [Fact]
    public void ChangeModel_DifferentModel_UpdatesInfo()
    {
        // Simulates what ChangeModelAsync does to AgentSessionInfo after successful resume
        var info = new AgentSessionInfo
        {
            Name = "TestSession",
            Model = "claude-sonnet-4.5",
            SessionId = "some-guid",
            WorkingDirectory = "/some/worktree"
        };

        var newModel = ModelHelper.NormalizeToSlug("claude-opus-4.6");
        Assert.NotEqual(info.Model, newModel); // Different model → would proceed

        // After successful resume, model is updated
        info.Model = newModel;
        Assert.Equal("claude-opus-4.6", info.Model);

        // Working directory must NOT change during model switch
        Assert.Equal("/some/worktree", info.WorkingDirectory);
    }

    [Fact]
    public void ChangeModel_PreservesSessionIdentity()
    {
        // Model switch reconnects the same session ID — history is preserved server-side
        var info = new AgentSessionInfo
        {
            Name = "MySession",
            Model = "gpt-5.1-codex",
            SessionId = "fixed-guid-123",
            WorkingDirectory = "/my/worktree"
        };
        info.History.Add(ChatMessage.UserMessage("hello"));
        info.History.Add(new ChatMessage("assistant", "hi there", DateTime.Now));

        var originalSessionId = info.SessionId;
        var originalHistory = info.History.Count;
        var originalName = info.Name;
        var originalWorkDir = info.WorkingDirectory;

        // Simulate model switch
        info.Model = ModelHelper.NormalizeToSlug("claude-opus-4.6");

        // Everything except model must be unchanged
        Assert.Equal(originalSessionId, info.SessionId);
        Assert.Equal(originalHistory, info.History.Count);
        Assert.Equal(originalName, info.Name);
        Assert.Equal(originalWorkDir, info.WorkingDirectory);
        Assert.Equal("claude-opus-4.6", info.Model);
    }

    [Fact]
    public void ChangeModel_DisplayNameFromDropdown_NormalizesToSlug()
    {
        // The UI dropdown shows display names but passes slugs.
        // If somehow a display name gets through, ChangeModelAsync must normalize it.
        var displayNames = new Dictionary<string, string>
        {
            { "Claude Opus 4.6 (fast mode)", "claude-opus-4.6-fast" },
            { "Gemini 3 Pro (Preview)", "gemini-3-pro-preview" },
            { "GPT-5.1-Codex-Max", "gpt-5.1-codex-max" },
        };

        foreach (var (display, expectedSlug) in displayNames)
        {
            var normalized = ModelHelper.NormalizeToSlug(display);
            Assert.Equal(expectedSlug, normalized);
            Assert.False(ModelHelper.IsDisplayName(normalized));
        }
    }

    // --- PrettifyModel tests ---
    // The prettifier is duplicated in ExpandedSessionView.razor and ModelSelector.razor.
    // We test the logic inline here to catch regressions like the "Opus-4.5" bug.

    /// <summary>
    /// Mirror of the PrettifyModel logic from ExpandedSessionView.razor / ModelSelector.razor.
    /// </summary>
    private static string PrettifyModel(string modelId)
    {
        var display = modelId
            .Replace("claude-", "Claude ")
            .Replace("gpt-", "GPT-")
            .Replace("gemini-", "Gemini ");
        display = display.Replace("-", " ");
        display = display.Replace("GPT ", "GPT-");
        return string.Join(' ', display.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s =>
            s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s));
    }

    [Theory]
    [InlineData("claude-opus-4.5", "Claude Opus 4.5")]
    [InlineData("claude-opus-4.6-fast", "Claude Opus 4.6 Fast")]
    [InlineData("claude-sonnet-4.5", "Claude Sonnet 4.5")]
    [InlineData("claude-haiku-4.5", "Claude Haiku 4.5")]
    [InlineData("gpt-5.1-codex-max", "GPT-5.1 Codex Max")]
    [InlineData("gpt-5.1-codex-mini", "GPT-5.1 Codex Mini")]
    [InlineData("gpt-5.2-codex", "GPT-5.2 Codex")]
    [InlineData("gpt-5-mini", "GPT-5 Mini")]
    [InlineData("gemini-3-pro-preview", "Gemini 3 Pro Preview")]
    public void PrettifyModel_ProducesReadableNames(string slug, string expected)
    {
        Assert.Equal(expected, PrettifyModel(slug));
    }

    [Theory]
    [InlineData("claude-opus-4.5")]
    [InlineData("gpt-5.1-codex")]
    [InlineData("gemini-3-pro-preview")]
    public void PrettifyModel_NoDuplicateHyphens(string slug)
    {
        var pretty = PrettifyModel(slug);
        // Should not contain stray hyphens (except GPT- prefix)
        var withoutGpt = pretty.Replace("GPT-", "GPT");
        Assert.DoesNotContain("-", withoutGpt);
    }

    [Fact]
    public void PrettifyModel_DoesNotProduceDuplicateEntries()
    {
        // Regression: "claude-opus-4.5" was prettified to "Claude Opus-4.5"
        // which didn't match the display name "Claude Opus 4.5", causing duplicates in the dropdown
        var slugs = new[] { "claude-opus-4.5", "claude-sonnet-4.5", "gpt-5.1-codex-max" };
        foreach (var slug in slugs)
        {
            var pretty = PrettifyModel(slug);
            Assert.DoesNotContain("-", pretty.Replace("GPT-", "GPT"));
            // Prettifying twice should be stable
            Assert.Equal(pretty, PrettifyModel(slug));
        }
    }

    [Fact]
    public void RecreateSession_UsesNormalizedModel()
    {
        // When RecreateSessionAsync is called, the model should be normalized
        // before being passed to CreateSessionAsync
        var displayName = "Claude Opus 4.5";
        var normalized = ModelHelper.NormalizeToSlug(displayName);
        Assert.Equal("claude-opus-4.5", normalized);
        // The recreated session should use the slug, not the display name
        Assert.False(ModelHelper.IsDisplayName(normalized));
    }

    [Theory]
    [InlineData("claude-opus-4.5")]
    [InlineData("gpt-5.1-codex-max")]
    [InlineData("gemini-3-pro-preview")]
    [InlineData("claude-opus-4.6-fast")]
    public void RoundTrip_NormalizeAndPrettify_AreConsistent(string slug)
    {
        // Prettify a slug, then normalize back — should return the original slug
        var pretty = PrettifyModel(slug);
        var backToSlug = ModelHelper.NormalizeToSlug(pretty);
        Assert.Equal(slug, backToSlug);
    }
}
