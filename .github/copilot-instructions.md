# AutoPilot.App - Copilot Instructions

## Building and Launching

**IMPORTANT**: After making ANY code changes to this project, you MUST rebuild and relaunch the app using the relaunch script:

```bash
/Users/shneuvil/Projects/AutoPilot.App/relaunch.sh
```

**NEVER** use `dotnet build` + `open` separately. The relaunch script:
1. Builds the app with `dotnet build -f net10.0-maccatalyst`
2. Copies the built app to a staging directory
3. Launches a NEW instance with `open -n` (so the old one stays alive)

This is critical because:
- The script captures old PIDs, launches new instance, waits for it to start, then kills old ones
- This ensures seamless handoff — new app is running before old one dies
- The 3-second grace period lets the new UI fully initialize
- Safe even when called from a Copilot session inside the app

## Project Structure

- **Framework**: .NET MAUI Blazor Hybrid (Mac Catalyst)
- **SDK**: `GitHub.Copilot.SDK` 0.1.22 - talks to Copilot CLI via ACP (Agent Control Protocol)
- **Linker**: The csproj has `<TrimmerRootAssembly Include="GitHub.Copilot.SDK" />` — do NOT remove this or SDK event types get stripped
- **Sandbox**: Disabled in `Platforms/MacCatalyst/Entitlements.plist` — required for spawning copilot CLI

## Key Files

- `Services/CopilotService.cs` — Core SDK wrapper, session management, event handling
- `Components/Pages/Home.razor` — Chat UI
- `Components/Pages/Dashboard.razor` — Multi-session orchestrator view
- `Components/Layout/SessionSidebar.razor` — Session list, create/resume
- `Models/AgentSessionInfo.cs` — Session info model
- `Models/ChatMessage.cs` — Chat message record

## SDK Event Handling

The SDK sends `AssistantMessageEvent` (not deltas) for responses. Key event types:
- `AssistantMessageEvent` — full response content
- `AssistantMessageDeltaEvent` — streaming deltas (may not always be used)
- `SessionIdleEvent` — turn is complete
- `ToolExecutionStartEvent` — tool call started
- `ToolExecutionCompleteEvent` — tool call finished
- `AssistantIntentEvent` — intent/activity update
- `SessionStartEvent` — has SessionId assignment

## Performance Notes

- Avoid `@bind:event="oninput"` on text inputs — causes round-trip lag on every keystroke
- Use plain HTML inputs with JS event listeners for fast typing
- Read input values via `JS.InvokeAsync<string>("eval", "document.getElementById('id')?.value")` on submit
- Materialize `IEnumerable` from disk reads to `List` to avoid re-reading on every render
