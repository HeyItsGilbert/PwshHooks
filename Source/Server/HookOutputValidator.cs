using System.Text.Json;
using System.Text.Json.Nodes;

namespace PwshHooks;

public static class HookOutputValidator
{
  private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
  {
    "PreToolUse",
    "PostToolUse",
    "UserPromptSubmit",
    "SessionStart",
    "SessionEnd",
    "Stop",
    "SubagentStop",
    "PreCompact",
    "Notification",
  };

  private static readonly HashSet<string> PermissionDecisions = new(StringComparer.Ordinal)
  {
    "allow", "deny", "ask",
  };

  /// <summary>
  /// Validate a single Claude Hooks output payload. Returns null when valid, otherwise a
  /// human-readable error description suitable for surfacing on the E: stream.
  /// Empty/whitespace input is treated as a no-op and considered valid.
  /// </summary>
  public static string? Validate(string? output)
  {
    if (string.IsNullOrWhiteSpace(output))
    {
      return null;
    }

    JsonNode? root;
    try
    {
      root = JsonNode.Parse(output);
    }
    catch (JsonException ex)
    {
      return $"hook output is not valid JSON: {ex.Message}";
    }

    if (root is not JsonObject obj)
    {
      string kind = root is null ? "null" : root.GetValueKind().ToString();
      return $"hook output must be a JSON object, got {kind}";
    }

    if (CheckOptionalBool(obj, "continue") is string e1) return e1;
    if (CheckOptionalString(obj, "stopReason") is string e2) return e2;
    if (CheckOptionalBool(obj, "suppressOutput") is string e3) return e3;
    if (CheckOptionalString(obj, "systemMessage") is string e4) return e4;
    if (CheckOptionalString(obj, "reason") is string e5) return e5;

    if (obj.TryGetPropertyValue("decision", out JsonNode? decision) && decision is not null)
    {
      if (decision.GetValueKind() != JsonValueKind.String)
      {
        return "field 'decision' must be a string (\"approve\" | \"block\") or null";
      }
      string v = decision.GetValue<string>();
      if (v != "approve" && v != "block")
      {
        return $"field 'decision' must be \"approve\" or \"block\", got \"{v}\"";
      }
    }

    if (obj.TryGetPropertyValue("hookSpecificOutput", out JsonNode? hso) && hso is not null)
    {
      if (hso is not JsonObject hsoObj)
      {
        return "field 'hookSpecificOutput' must be a JSON object";
      }

      if (!hsoObj.TryGetPropertyValue("hookEventName", out JsonNode? hen) || hen is null)
      {
        return "hookSpecificOutput.hookEventName is required when hookSpecificOutput is present";
      }
      if (hen.GetValueKind() != JsonValueKind.String)
      {
        return "hookSpecificOutput.hookEventName must be a string";
      }
      string evt = hen.GetValue<string>();
      if (!KnownEvents.Contains(evt))
      {
        return $"hookSpecificOutput.hookEventName \"{evt}\" is not a known hook event";
      }

      if (CheckPerEventFields(hsoObj, evt) is string ePerEvent) return ePerEvent;
    }

    return null;
  }

  private static string? CheckPerEventFields(JsonObject hso, string evt)
  {
    switch (evt)
    {
      case "PreToolUse":
        if (hso.TryGetPropertyValue("permissionDecision", out JsonNode? pd) && pd is not null)
        {
          if (pd.GetValueKind() != JsonValueKind.String)
          {
            return "hookSpecificOutput.permissionDecision must be a string";
          }
          string pdv = pd.GetValue<string>();
          if (!PermissionDecisions.Contains(pdv))
          {
            return $"hookSpecificOutput.permissionDecision must be one of \"allow\", \"deny\", \"ask\", got \"{pdv}\"";
          }
        }
        if (CheckOptionalString(hso, "permissionDecisionReason", "hookSpecificOutput.") is string e1) return e1;
        break;
      case "UserPromptSubmit":
      case "SessionStart":
        if (CheckOptionalString(hso, "additionalContext", "hookSpecificOutput.") is string e2) return e2;
        break;
    }
    return null;
  }

  private static string? CheckOptionalBool(JsonObject obj, string field, string prefix = "")
  {
    if (!obj.TryGetPropertyValue(field, out JsonNode? n) || n is null) return null;
    JsonValueKind k = n.GetValueKind();
    if (k != JsonValueKind.True && k != JsonValueKind.False)
    {
      return $"field '{prefix}{field}' must be a boolean";
    }
    return null;
  }

  private static string? CheckOptionalString(JsonObject obj, string field, string prefix = "")
  {
    if (!obj.TryGetPropertyValue(field, out JsonNode? n) || n is null) return null;
    if (n.GetValueKind() != JsonValueKind.String)
    {
      return $"field '{prefix}{field}' must be a string";
    }
    return null;
  }
}
