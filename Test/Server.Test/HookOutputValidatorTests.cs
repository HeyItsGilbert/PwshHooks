using PwshHooks;

namespace PwshHooks.ServerTest;

public class HookOutputValidatorTests
{
  [Fact]
  public void ValidPreToolUseOutput_ReturnsNull()
  {
    string output = """
    {
      "continue": true,
      "decision": "approve",
      "reason": "looks fine",
      "hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "allow",
        "permissionDecisionReason": "trusted command"
      }
    }
    """;

    Assert.Null(HookOutputValidator.Validate(output));
  }

  [Fact]
  public void HookSpecificOutput_MissingHookEventName_ReturnsError()
  {
    string output = """{"hookSpecificOutput":{"permissionDecision":"allow"}}""";

    string? error = HookOutputValidator.Validate(output);

    Assert.Equal(
      "hookSpecificOutput.hookEventName is required when hookSpecificOutput is present",
      error);
  }

  [Fact]
  public void MalformedJson_ReturnsParseError()
  {
    string? error = HookOutputValidator.Validate("{not json");

    Assert.NotNull(error);
    Assert.StartsWith("hook output is not valid JSON:", error);
  }

  [Fact]
  public void EmptyOutput_IsValid()
  {
    Assert.Null(HookOutputValidator.Validate(""));
    Assert.Null(HookOutputValidator.Validate("   \n\t"));
    Assert.Null(HookOutputValidator.Validate(null));
  }

  [Fact]
  public void NonObjectJson_ReturnsError()
  {
    string? error = HookOutputValidator.Validate("\"approved\"");
    Assert.NotNull(error);
    Assert.StartsWith("hook output must be a JSON object", error);
  }

  [Fact]
  public void InvalidDecisionValue_ReturnsError()
  {
    string? error = HookOutputValidator.Validate("""{"decision":"maybe"}""");

    Assert.Equal("field 'decision' must be \"approve\" or \"block\", got \"maybe\"", error);
  }

  [Fact]
  public void UnknownHookEventName_ReturnsError()
  {
    string output = """{"hookSpecificOutput":{"hookEventName":"BogusEvent"}}""";

    string? error = HookOutputValidator.Validate(output);

    Assert.Equal(
      "hookSpecificOutput.hookEventName \"BogusEvent\" is not a known hook event",
      error);
  }

  [Fact]
  public void PreToolUse_InvalidPermissionDecision_ReturnsError()
  {
    string output = """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"yolo"}}""";

    string? error = HookOutputValidator.Validate(output);

    Assert.Equal(
      "hookSpecificOutput.permissionDecision must be one of \"allow\", \"deny\", \"ask\", got \"yolo\"",
      error);
  }

  [Fact]
  public void ContinueWrongType_ReturnsError()
  {
    string? error = HookOutputValidator.Validate("""{"continue":"yes"}""");

    Assert.Equal("field 'continue' must be a boolean", error);
  }

  [Fact]
  public void UserPromptSubmit_AdditionalContextWrongType_ReturnsError()
  {
    string output = """{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":42}}""";

    string? error = HookOutputValidator.Validate(output);

    Assert.Equal("field 'hookSpecificOutput.additionalContext' must be a string", error);
  }
}
