#!/usr/bin/env python3
"""
Extract LLM efficiency metrics from a Copilot CLI session.

Usage:
    python3 extract_session_metrics.py <session-id-or-path> [--log <process-log-path>]

Reads events.jsonl and the process log to produce a JSON summary of all LLM
usage metrics for efficiency analysis.

Output: JSON to stdout with the following structure:
{
  "session": { id, cwd, repository, branch, summary, copilotVersion, startTime, endTime },
  "llm_calls": [ { model, initiator, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost, duration_ms, timestamp, api_call_id } ],
  "turns": { total, user_initiated, agent_initiated },
  "user_messages": [ { content_preview, timestamp } ],
  "tool_calls": { total, by_name: { tool: count }, sequential_groups: N },
  "subagents": [ { name, started, completed, duration_ms } ],
  "compactions": [ { timestamp, pre_tokens, success } ],
  "errors": [ { type, message, timestamp } ],
  "summary": {
    total_llm_calls, total_input_tokens, total_output_tokens, total_cache_read,
    total_cache_write, cache_hit_rate, total_duration_ms, avg_duration_ms,
    longest_call: { duration_ms, model, timestamp },
    models_used: { model: count },
    estimated_cost_usd
  }
}
"""

import json
import os
import re
import sys
import glob as globmod
from pathlib import Path
from datetime import datetime, timezone


COPILOT_DIR = Path.home() / ".copilot"
SESSION_STATE_DIR = COPILOT_DIR / "session-state"
LOGS_DIR = COPILOT_DIR / "logs"

# Model pricing per 1M tokens (approximate, as of early 2026)
MODEL_PRICING = {
    "claude-opus-4.6": {"input": 15.00, "output": 75.00, "cached_input": 1.50},
    "claude-opus-4.5": {"input": 15.00, "output": 75.00, "cached_input": 1.50},
    "claude-sonnet-4.6": {"input": 3.00, "output": 15.00, "cached_input": 0.30},
    "claude-sonnet-4.5": {"input": 3.00, "output": 15.00, "cached_input": 0.30},
    "claude-sonnet-4": {"input": 3.00, "output": 15.00, "cached_input": 0.30},
    "claude-haiku-4.5": {"input": 0.80, "output": 4.00, "cached_input": 0.08},
    "gpt-5.3-codex": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gpt-5.2-codex": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gpt-5.2": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gpt-5.1-codex-max": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gpt-5.1-codex": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gpt-5.1": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gpt-5.1-codex-mini": {"input": 0.40, "output": 1.60, "cached_input": 0.10},
    "gpt-5-mini": {"input": 0.40, "output": 1.60, "cached_input": 0.10},
    "gpt-4.1": {"input": 2.00, "output": 8.00, "cached_input": 0.50},
    "gemini-3-pro-preview": {"input": 1.25, "output": 10.00, "cached_input": 0.31},
}

# Fallback pricing for unknown models
DEFAULT_PRICING = {"input": 3.00, "output": 15.00, "cached_input": 0.30}


def find_session_dir(session_id_or_path):
    """Resolve a session ID or path to the session state directory."""
    p = Path(session_id_or_path)
    if p.is_dir() and (p / "events.jsonl").exists():
        return p
    # Try as session ID
    candidate = SESSION_STATE_DIR / session_id_or_path
    if candidate.is_dir() and (candidate / "events.jsonl").exists():
        return candidate
    # Try partial match
    for d in SESSION_STATE_DIR.iterdir():
        if d.is_dir() and d.name.startswith(session_id_or_path):
            if (d / "events.jsonl").exists():
                return d
    return None


def find_process_log(session_id, explicit_log=None):
    """Find the process log file for a given session ID."""
    if explicit_log:
        p = Path(explicit_log)
        if p.exists():
            return p
        return None

    if not LOGS_DIR.exists():
        return None

    # Search log files for the session ID (check first 30 lines of each)
    log_files = sorted(LOGS_DIR.glob("process-*.log"), key=lambda f: f.stat().st_mtime, reverse=True)
    for log_file in log_files:
        try:
            with open(log_file, "r", errors="replace") as f:
                for i, line in enumerate(f):
                    if i > 30:
                        break
                    if session_id in line:
                        return log_file
        except (OSError, PermissionError):
            continue
    return None


def parse_events(events_path):
    """Parse events.jsonl and extract structured data."""
    events = {
        "session_info": {},
        "user_messages": [],
        "turns": [],
        "tool_starts": [],
        "tool_completes": [],
        "subagents": [],
        "compactions": [],
        "errors": [],
        "mode_changes": [],
        "plan_changes": 0,
        "task_completes": 0,
        "dev_loop": [],  # Build/test commands with results
    }

    current_turn = None
    tool_start_map = {}  # toolCallId -> start event

    with open(events_path, "r", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                evt = json.loads(line)
            except json.JSONDecodeError:
                continue

            etype = evt.get("type", "")
            data = evt.get("data", {})
            ts = evt.get("timestamp", "")

            if etype == "session.start":
                ctx = data.get("context", {})
                events["session_info"] = {
                    "id": data.get("sessionId", ""),
                    "copilotVersion": data.get("copilotVersion", ""),
                    "startTime": data.get("startTime", ts),
                    "cwd": ctx.get("cwd", ""),
                    "gitRoot": ctx.get("gitRoot", ""),
                    "branch": ctx.get("branch", ""),
                    "repository": ctx.get("repository", ""),
                }

            elif etype == "user.message":
                content = data.get("content", "")
                events["user_messages"].append({
                    "content_preview": content[:200],
                    "timestamp": ts,
                    "full_length": len(content),
                })

            elif etype == "assistant.turn_start":
                current_turn = {
                    "turnId": data.get("turnId", ""),
                    "startTime": ts,
                    "endTime": None,
                    "tool_calls": [],
                    "message_count": 0,
                }

            elif etype == "assistant.turn_end":
                if current_turn:
                    current_turn["endTime"] = ts
                    events["turns"].append(current_turn)
                    current_turn = None

            elif etype == "assistant.message":
                if current_turn:
                    current_turn["message_count"] += 1
                    tool_reqs = data.get("toolRequests", [])
                    for tr in tool_reqs:
                        if current_turn:
                            current_turn["tool_calls"].append(tr.get("name", ""))

            elif etype == "tool.execution_start":
                tool_name = data.get("toolName", "")
                tool_call_id = data.get("toolCallId", "")
                args = data.get("arguments", {})
                tool_start_map[tool_call_id] = {
                    "name": tool_name, "timestamp": ts, "arguments": args,
                }
                events["tool_starts"].append({"name": tool_name, "timestamp": ts, "toolCallId": tool_call_id})

                # Track build/test commands for dev loop analysis
                if tool_name == "bash":
                    cmd = args.get("command", "")
                    desc = args.get("description", "")
                    cmd_lower = cmd.lower()
                    is_build = any(kw in cmd_lower for kw in [
                        "dotnet build", "dotnet msbuild", "msbuild", "npm run build",
                        "cargo build", "go build", "make", "cmake", "gradle build",
                        "mvn compile", "mvn package", "tsc", "webpack",
                    ])
                    is_test = any(kw in cmd_lower for kw in [
                        "dotnet test", "npm test", "npm run test", "cargo test",
                        "go test", "pytest", "jest", "mocha", "xunit", "nunit",
                        "gradle test", "mvn test",
                    ])
                    if is_build or is_test:
                        tool_start_map[tool_call_id]["dev_loop"] = {
                            "type": "test" if is_test else "build",
                            "command": cmd[:200],
                            "description": desc,
                            "startTime": ts,
                        }

            elif etype == "tool.execution_complete":
                tool_call_id = data.get("toolCallId", "")
                success = data.get("success", False)
                start_info = tool_start_map.get(tool_call_id, {})
                events["tool_completes"].append({
                    "toolCallId": tool_call_id,
                    "name": start_info.get("name", ""),
                    "success": success,
                    "startTime": start_info.get("timestamp", ""),
                    "endTime": ts,
                })

                # Capture dev loop result
                if "dev_loop" in start_info:
                    dl = start_info["dev_loop"]
                    result_content = str(data.get("result", {}).get("content", ""))
                    # Detect build/test failure from result content
                    result_lower = result_content.lower()
                    build_failed = any(kw in result_lower for kw in [
                        "build failed", "error(s)", "compilation error",
                        "failed!", "error cs", "error ts", "error:", "exited with exit code 1",
                        "exited with exit code 2",
                    ])
                    # More nuanced: "0 Error(s)" is success, ">0 Error(s)" is failure
                    import re as _re
                    error_match = _re.search(r'(\d+)\s+Error\(s\)', result_content)
                    if error_match:
                        build_failed = int(error_match.group(1)) > 0

                    test_failed = False
                    test_passed_match = _re.search(r'Passed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+)', result_content)
                    if test_passed_match:
                        test_failed = int(test_passed_match.group(1)) > 0
                    elif any(kw in result_lower for kw in [
                        "test run failed", "tests failed",
                    ]):
                        test_failed = True

                    dl["endTime"] = ts
                    dl["duration_ms"] = compute_duration_ms(dl["startTime"], ts)
                    dl["success"] = success and not build_failed and not test_failed
                    dl["result_preview"] = result_content[:200]
                    if test_passed_match:
                        dl["test_failed"] = int(test_passed_match.group(1))
                        dl["test_passed"] = int(test_passed_match.group(2))
                    events["dev_loop"].append(dl)

            elif etype == "subagent.started":
                events["subagents"].append({
                    "toolCallId": data.get("toolCallId", ""),
                    "name": data.get("agentName", ""),
                    "displayName": data.get("agentDisplayName", ""),
                    "startTime": ts,
                    "endTime": None,
                })

            elif etype == "subagent.completed":
                tcid = data.get("toolCallId", "")
                for sa in events["subagents"]:
                    if sa["toolCallId"] == tcid and sa["endTime"] is None:
                        sa["endTime"] = ts
                        break

            elif etype == "session.compaction_start":
                events["compactions"].append({"startTime": ts, "endTime": None, "preTokens": None, "success": None})

            elif etype == "session.compaction_complete":
                if events["compactions"]:
                    last = events["compactions"][-1]
                    if last["endTime"] is None:
                        last["endTime"] = ts
                        last["preTokens"] = data.get("preCompactionTokens")
                        last["success"] = data.get("success", False)

            elif etype == "session.error":
                events["errors"].append({
                    "type": data.get("errorType", ""),
                    "message": (data.get("message", ""))[:300],
                    "timestamp": ts,
                })

            elif etype == "session.mode_changed":
                events["mode_changes"].append({"timestamp": ts, "data": data})

            elif etype == "session.plan_changed":
                events["plan_changes"] += 1

            elif etype == "session.task_complete":
                events["task_completes"] += 1

    return events


def parse_process_log(log_path, session_id):
    """Parse process log for assistant_usage telemetry events matching the session."""
    llm_calls = []
    if not log_path:
        return llm_calls

    # Read the file and find assistant_usage telemetry blocks
    try:
        with open(log_path, "r", errors="replace") as f:
            content = f.read()
    except (OSError, PermissionError):
        return llm_calls

    # Find all assistant_usage telemetry JSON blocks
    # Pattern: [Telemetry] cli.telemetry:\n{ ... "kind": "assistant_usage" ... }
    pattern = r'\[Telemetry\] cli\.telemetry:\s*\n(\{[^}]*"kind":\s*"assistant_usage"[^}]*\{[^}]*\}[^}]*\})'
    # More robust: find JSON blocks after telemetry markers
    blocks = []
    lines = content.split("\n")
    i = 0
    while i < len(lines):
        if "[Telemetry] cli.telemetry:" in lines[i]:
            # Collect the JSON block that follows
            json_lines = []
            i += 1
            brace_count = 0
            started = False
            while i < len(lines):
                line = lines[i]
                # Strip timestamp prefix if present
                stripped = re.sub(r'^\d{4}-\d{2}-\d{2}T[\d:.]+Z\s*', '', line)
                if not started and stripped.strip().startswith("{"):
                    started = True
                if started:
                    json_lines.append(stripped)
                    brace_count += stripped.count("{") - stripped.count("}")
                    if brace_count <= 0 and started:
                        break
                elif started:
                    break
                i += 1
            if json_lines:
                try:
                    block = json.loads("\n".join(json_lines))
                    if block.get("kind") == "assistant_usage":
                        blocks.append(block)
                except json.JSONDecodeError:
                    pass
        i += 1

    # Filter to our session and extract metrics
    for block in blocks:
        props = block.get("properties", {})
        metrics = block.get("metrics", {})
        block_session = block.get("session_id", "")

        if session_id and block_session != session_id:
            continue

        llm_calls.append({
            "model": props.get("model", "unknown"),
            "initiator": props.get("initiator", "unknown"),
            "api_call_id": props.get("api_call_id", ""),
            "input_tokens": metrics.get("input_tokens", 0),
            "output_tokens": metrics.get("output_tokens", 0),
            "cache_read_tokens": metrics.get("cache_read_tokens", 0),
            "cache_write_tokens": metrics.get("cache_write_tokens", 0),
            "cost": metrics.get("cost", 0),
            "duration_ms": metrics.get("duration", 0),
        })

    return llm_calls


def compute_cost(input_tokens, output_tokens, cache_read_tokens, model):
    """Estimate USD cost for a set of tokens at a given model's pricing."""
    pricing = MODEL_PRICING.get(model, DEFAULT_PRICING)
    # Non-cached input = total input - cache reads
    fresh_input = max(0, input_tokens - cache_read_tokens)
    cost = (
        (fresh_input * pricing["input"] / 1_000_000)
        + (cache_read_tokens * pricing["cached_input"] / 1_000_000)
        + (output_tokens * pricing["output"] / 1_000_000)
    )
    return round(cost, 6)


def ts_to_dt(ts_str):
    """Parse ISO timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        return datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
    except (ValueError, AttributeError):
        return None


def compute_duration_ms(start_ts, end_ts):
    """Compute duration in ms between two ISO timestamps."""
    start = ts_to_dt(start_ts)
    end = ts_to_dt(end_ts)
    if start and end:
        return int((end - start).total_seconds() * 1000)
    return None


def build_summary(events, llm_calls):
    """Build the final summary JSON."""
    session = events["session_info"]

    # Find session end time from last event
    end_time = None
    if events["turns"]:
        last_turn = events["turns"][-1]
        end_time = last_turn.get("endTime")
    if not end_time and events["user_messages"]:
        end_time = events["user_messages"][-1]["timestamp"]

    session["endTime"] = end_time

    # Workspace metadata (summary may already be set from workspace.yaml)

    # LLM call summary
    total_input = sum(c["input_tokens"] for c in llm_calls)
    total_output = sum(c["output_tokens"] for c in llm_calls)
    total_cache_read = sum(c["cache_read_tokens"] for c in llm_calls)
    total_cache_write = sum(c["cache_write_tokens"] for c in llm_calls)
    total_duration = sum(c["duration_ms"] for c in llm_calls)
    cache_hit_rate = (total_cache_read / total_input * 100) if total_input > 0 else 0

    # Longest call
    longest = max(llm_calls, key=lambda c: c["duration_ms"]) if llm_calls else None

    # Models used
    models_used = {}
    for c in llm_calls:
        m = c["model"]
        models_used[m] = models_used.get(m, 0) + 1

    # Cost estimation
    total_cost = 0
    for c in llm_calls:
        total_cost += compute_cost(c["input_tokens"], c["output_tokens"], c["cache_read_tokens"], c["model"])

    # Turn analysis
    user_turns = len(events["user_messages"])
    total_turns = len(events["turns"])
    agent_turns = total_turns - user_turns

    # Tool call analysis
    tool_counts = {}
    tool_durations = []
    for tc in events["tool_completes"]:
        name = tc["name"]
        tool_counts[name] = tool_counts.get(name, 0) + 1
        dur = compute_duration_ms(tc["startTime"], tc["endTime"])
        if dur is not None:
            tool_durations.append({"name": name, "duration_ms": dur})

    long_tools = [t for t in tool_durations if t["duration_ms"] > 60000]

    # Sub-agent analysis
    subagent_info = []
    for sa in events["subagents"]:
        dur = compute_duration_ms(sa["startTime"], sa["endTime"])
        subagent_info.append({
            "name": sa["name"],
            "displayName": sa["displayName"],
            "duration_ms": dur,
        })

    # Session duration
    session_duration_ms = compute_duration_ms(session.get("startTime"), end_time)

    # Dev loop analysis
    dev_loop = events["dev_loop"]
    builds = [d for d in dev_loop if d["type"] == "build"]
    tests = [d for d in dev_loop if d["type"] == "test"]
    build_failures = [d for d in builds if not d.get("success", True)]
    test_failures = [d for d in tests if not d.get("success", True)]

    # Detect fix cycles: consecutive build failures of similar targets
    fix_cycles = []
    if builds:
        current_cycle = []
        for b in builds:
            if not b.get("success", True):
                current_cycle.append(b)
            else:
                if len(current_cycle) >= 2:
                    fix_cycles.append({
                        "failures": len(current_cycle),
                        "first_failure": current_cycle[0]["startTime"],
                        "resolution": b["startTime"],
                        "command_preview": current_cycle[0]["command"][:100],
                    })
                current_cycle = []
        if len(current_cycle) >= 2:
            fix_cycles.append({
                "failures": len(current_cycle),
                "first_failure": current_cycle[0]["startTime"],
                "resolution": None,
                "command_preview": current_cycle[0]["command"][:100],
            })

    # Detect redundant builds: consecutive successful builds of same target with
    # no edit/create tool calls between them (from the full event timeline)
    redundant_builds = 0
    if builds and len(builds) >= 2:
        # Build a timeline of edit events for cross-referencing
        edit_timestamps = set()
        for tc in events["tool_completes"]:
            if tc["name"] in ("edit", "create"):
                edit_timestamps.add(tc["endTime"])

        prev_build = None
        for b in builds:
            if prev_build and b.get("success", True):
                prev_cmd_key = prev_build["command"][:80]
                curr_cmd_key = b["command"][:80]
                if prev_cmd_key == curr_cmd_key:
                    # Check if any edits happened between these two builds
                    prev_end = prev_build.get("endTime", prev_build["startTime"])
                    curr_start = b["startTime"]
                    edits_between = any(
                        prev_end < et < curr_start for et in edit_timestamps
                    )
                    if not edits_between:
                        redundant_builds += 1
            prev_build = b

    # Check for unvalidated edits: edit tool calls not followed by build/test
    edit_count = tool_counts.get("edit", 0) + tool_counts.get("create", 0)
    build_test_count = len(builds) + len(tests)
    edits_without_validation = max(0, edit_count - build_test_count) if edit_count > 0 else 0

    dev_loop_summary = {
        "total_builds": len(builds),
        "total_tests": len(tests),
        "build_failures": len(build_failures),
        "test_failures": len(test_failures),
        "build_success_rate_pct": round((len(builds) - len(build_failures)) / len(builds) * 100, 1) if builds else None,
        "test_success_rate_pct": round((len(tests) - len(test_failures)) / len(tests) * 100, 1) if tests else None,
        "fix_cycles": fix_cycles,
        "redundant_builds": redundant_builds,
        "total_build_time_ms": sum(b.get("duration_ms") or 0 for b in builds),
        "total_test_time_ms": sum(t.get("duration_ms") or 0 for t in tests),
        "edits_without_validation": edits_without_validation,
        "test_results": {
            "total_passed": sum(t.get("test_passed", 0) for t in tests),
            "total_failed": sum(t.get("test_failed", 0) for t in tests),
        },
    }

    return {
        "session": session,
        "llm_calls": llm_calls,
        "turns": {
            "total": total_turns,
            "user_initiated": user_turns,
            "agent_initiated": agent_turns,
        },
        "user_messages": events["user_messages"],
        "tool_calls": {
            "total": len(events["tool_completes"]),
            "by_name": tool_counts,
            "long_running": long_tools,
        },
        "subagents": subagent_info,
        "compactions": events["compactions"],
        "errors": events["errors"],
        "dev_loop": dev_loop,
        "dev_loop_summary": dev_loop_summary,
        "plan_changes": events["plan_changes"],
        "task_completes": events["task_completes"],
        "summary": {
            "total_llm_calls": len(llm_calls),
            "total_input_tokens": total_input,
            "total_output_tokens": total_output,
            "total_cache_read_tokens": total_cache_read,
            "total_cache_write_tokens": total_cache_write,
            "cache_hit_rate_pct": round(cache_hit_rate, 1),
            "total_llm_duration_ms": total_duration,
            "avg_llm_duration_ms": round(total_duration / len(llm_calls)) if llm_calls else 0,
            "longest_call": {
                "duration_ms": longest["duration_ms"],
                "model": longest["model"],
                "initiator": longest["initiator"],
            } if longest else None,
            "models_used": models_used,
            "estimated_cost_usd": round(total_cost, 4),
            "session_duration_ms": session_duration_ms,
            "total_turns": total_turns,
            "user_turns": user_turns,
            "agent_turns": agent_turns,
            "total_tool_calls": len(events["tool_completes"]),
            "total_subagents": len(events["subagents"]),
            "total_compactions": len(events["compactions"]),
            "total_errors": len(events["errors"]),
            "total_builds": len(builds),
            "total_tests": len(tests),
            "build_failures": len(build_failures),
            "test_failures": len(test_failures),
            "fix_cycles": len(fix_cycles),
        },
    }


def main():
    import argparse
    parser = argparse.ArgumentParser(description="Extract LLM efficiency metrics from a Copilot CLI session")
    parser.add_argument("session", help="Session ID, partial ID, or path to session directory")
    parser.add_argument("--log", help="Explicit path to process log file", default=None)
    parser.add_argument("--pretty", action="store_true", help="Pretty-print JSON output")
    args = parser.parse_args()

    # Find session directory
    session_dir = find_session_dir(args.session)
    if not session_dir:
        print(f"Error: Could not find session directory for '{args.session}'", file=sys.stderr)
        print(f"Looked in: {SESSION_STATE_DIR}", file=sys.stderr)
        sys.exit(1)

    events_path = session_dir / "events.jsonl"
    if not events_path.exists():
        print(f"Error: No events.jsonl found in {session_dir}", file=sys.stderr)
        sys.exit(1)

    # Parse events
    events = parse_events(events_path)
    session_id = events["session_info"].get("id", session_dir.name)

    # Read workspace.yaml for summary
    workspace_yaml = session_dir / "workspace.yaml"
    if workspace_yaml.exists():
        try:
            with open(workspace_yaml) as f:
                for line in f:
                    if line.startswith("summary:"):
                        events["session_info"]["summary"] = line.split(":", 1)[1].strip()
                        break
        except (OSError, PermissionError):
            pass

    # Find and parse process log
    log_path = find_process_log(session_id, args.log)
    llm_calls = parse_process_log(log_path, session_id)

    # Build summary
    result = build_summary(events, llm_calls)
    result["_meta"] = {
        "session_dir": str(session_dir),
        "process_log": str(log_path) if log_path else None,
        "events_count": sum(1 for _ in open(events_path, errors="replace")),
        "llm_calls_found": len(llm_calls),
    }

    # Output
    indent = 2 if args.pretty else None
    print(json.dumps(result, indent=indent, default=str))


if __name__ == "__main__":
    main()
