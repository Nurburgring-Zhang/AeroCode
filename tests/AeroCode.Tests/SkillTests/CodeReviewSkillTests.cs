// Copyright (c) AeroCode V3.0
// CodeReviewSkill unit tests — 8-dimension code review.
using AeroCode.Skills.Bundled.Engineering;
using AeroCode.Skills.Registry;
using Xunit;

namespace AeroCode.Tests.SkillTests;

public class CodeReviewSkillTests
{
    private static async Task<SkillResult> RunAsync(string code)
    {
        var skill = new CodeReviewSkill();
        var input = new SkillInput
        {
            Args = new Dictionary<string, object?> { ["code"] = code },
        };
        return await skill.ExecuteAsync(input, new SkillContext());
    }

    [Fact]
    public async Task CleanCode_NoMajorIssues()
    {
        var clean = """
            /// <summary>Greets a user.</summary>
            public class Greeter
            {
                /// <summary>Returns a greeting.</summary>
                public string Greet(string name)
                {
                    return $"Hello, {name}!";
                }
            }
            """;
        var result = await RunAsync(clean);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task EmptyMethod_FlaggedInFunctionality()
    {
        var code = """
            public class Foo
            {
                public void DoStuff() { }
                public int Compute(int x) { return x; }
            }
            """;
        var result = await RunAsync(code);
        Assert.True(result.Success);
        Assert.Contains("empty method", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HighComplexity_Flagged()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("public class Complex");
        sb.AppendLine("{");
        sb.AppendLine("  public int F(int x) {");
        sb.AppendLine("    if (x > 0) {");
        sb.AppendLine("      for (int i = 0; i < x; i++) {");
        sb.AppendLine("        while (i > 0) {");
        sb.AppendLine("          if (i % 2 == 0) {");
        sb.AppendLine("            switch (i) {");
        sb.AppendLine("              case 1: break;");
        sb.AppendLine("              case 2: break;");
        sb.AppendLine("            }");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    return x;");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        var result = await RunAsync(sb.ToString());
        Assert.Contains("complexity", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TodoMarker_FlaggedInComments()
    {
        var code = """
            public class Foo
            {
                // TODO: implement this
                public void Bar() { }
            }
            """;
        var result = await RunAsync(code);
        Assert.Contains("TODO", result.Text);
    }

    [Fact]
    public async Task MissingCode_Fails()
    {
        var skill = new CodeReviewSkill();
        var input = new SkillInput { Args = new Dictionary<string, object?>() };
        var result = await skill.ExecuteAsync(input, new SkillContext());
        Assert.False(result.Success);
    }
}
