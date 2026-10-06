using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the in-ticket Summary helpers: the template rides along in the system
/// prompt, a stray code fence around the filled template is unwrapped, and the
/// template's own formatting (bold heading lines + '- ' bullets) renders to the
/// safe HTML subset the draft editor receives.
public sealed class ClaudeSummaryTests
{
    [Fact]
    public void System_prompt_appends_the_template_after_the_instructions()
    {
        var prompt = ClaudeAssistService.BuildSummarySystemPrompt("Fill it in.  \n", "**A:**\n\n- ");

        Assert.StartsWith("Fill it in.\n\n", prompt);
        Assert.Contains("<template>\n**A:**\n\n- \n</template>", prompt);
        Assert.EndsWith("</template>", prompt);
    }

    [Fact]
    public void Code_fence_around_the_output_is_unwrapped()
    {
        Assert.Equal("**A:**\n\n- one", ClaudeAssistService.StripCodeFence("```markdown\n**A:**\n\n- one\n```"));
    }

    [Fact]
    public void Unfenced_output_is_left_alone()
    {
        Assert.Equal("**A:**\n\n- one", ClaudeAssistService.StripCodeFence("  **A:**\n\n- one  "));
    }

    [Fact]
    public void Filled_template_renders_bold_headings_and_bullet_lists()
    {
        var filled = "**Summary**\n\n**Problem:**\n\n- Printer offline.\n\n**Solution:**\n\n- Driver reinstalled.\n- Test page printed.";

        Assert.Equal(
            "<p><strong>Summary</strong></p>"
            + "<p><strong>Problem:</strong></p><ul><li>Printer offline.</li></ul>"
            + "<p><strong>Solution:</strong></p><ul><li>Driver reinstalled.</li><li>Test page printed.</li></ul>",
            ClaudeAssistService.MarkdownToHtml(filled));
    }

    [Fact]
    public void Summary_settings_have_default_registrations()
    {
        Assert.Contains(SettingDefaults.All, d => d.Key == SettingKeys.Claude.SummaryTemplate && d.Value.Contains("- "));
        Assert.Contains(SettingDefaults.All, d => d.Key == SettingKeys.Claude.SummarySystemPrompt && d.Value.Length > 0);
    }
}
