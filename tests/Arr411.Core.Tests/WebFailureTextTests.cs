using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class WebFailureTextTests
{
    [Fact]
    public void It_tells_the_kind_the_reason_and_the_exit_code()
    {
        var text = WebFailureText.Describe("RenderProcessExited", "Crashed", 1, null, null);

        Assert.Equal("RenderProcessExited (reason: Crashed, exit code 0x00000001)", text);
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000005), "0xC0000005: access violation")]
    [InlineData(unchecked((int)0xC0000409), "0xC0000409: fast fail")]
    [InlineData(unchecked((int)0xC0000017), "0xC0000017: not enough memory")]
    [InlineData(unchecked((int)0xC000012D), "0xC000012D: the system ran out of memory")]
    public void A_status_that_windows_defines_is_explained(int exitCode, string expected)
    {
        Assert.Contains(expected, WebFailureText.Describe("BrowserProcessExited", "Crashed", exitCode, null, null));
    }

    [Fact]
    public void An_exit_code_that_means_nothing_special_is_left_unexplained()
    {
        var text = WebFailureText.Describe("GpuProcessExited", "Terminated", unchecked((int)0xDEADBEEF), null, null);

        Assert.Contains("exit code 0xDEADBEEF)", text);
        Assert.DoesNotContain(":", text.Split("0xDEADBEEF")[1]);
    }

    [Fact]
    public void The_process_and_the_failing_module_are_added_when_known_and_the_module_is_only_a_file_name()
    {
        var text = WebFailureText.Describe(
            "UtilityProcessExited",
            "Crashed",
            -1073741819,
            " Audio Service ",
            @"C:\Users\someone\AppData\Local\Microsoft\EdgeWebView\Application\131.0.2903.112\msedge.dll");

        Assert.EndsWith(", process: Audio Service, failing module: msedge.dll", text);
        Assert.DoesNotContain("someone", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Nothing_is_added_for_what_the_browser_does_not_say(string? empty)
    {
        var text = WebFailureText.Describe("RenderProcessUnresponsive", "Unresponsive", 0, empty, empty);

        Assert.DoesNotContain("process:", text);
        Assert.DoesNotContain("module:", text);
    }
}
