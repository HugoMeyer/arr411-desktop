using Arr411.Core.Bridge;

namespace Arr411.Core.Tests;

public class PageBundleTests
{
    private const string Shim = "/* shim */ window.chrome = { runtime: {} };";
    private const string Hook = "/* hook */ window.__hooked = true;";
    private const string Content = "/* content */ console.log('{0} {{ $ }}');";

    [Fact]
    public void The_three_scripts_are_in_the_bundle_unchanged_and_in_order()
    {
        var bundle = PageBundle.Compose(Shim, Hook, Content);

        var shim = bundle.IndexOf(Shim, StringComparison.Ordinal);
        var hook = bundle.IndexOf(Hook, StringComparison.Ordinal);
        var content = bundle.IndexOf(Content, StringComparison.Ordinal);
        Assert.True(shim >= 0, "the shim is missing");
        Assert.True(hook > shim, "the hook must come after the shim");
        Assert.True(content > hook, "the content script must come after the hook");
    }

    [Fact]
    public void Nothing_runs_without_the_native_bridge_or_twice()
    {
        var bundle = PageBundle.Compose(Shim, Hook, Content);

        Assert.StartsWith("(function(){", bundle);
        Assert.Contains("if (!window.QbtPage || window." + PageBundle.LoadedFlag + ") return;", bundle);
        Assert.Contains("window." + PageBundle.LoadedFlag + " = true;", bundle);
        Assert.True(
            bundle.IndexOf("return;", StringComparison.Ordinal) < bundle.IndexOf(Shim, StringComparison.Ordinal),
            "the guard must come before any of the scripts");
        Assert.EndsWith("})();", bundle);
    }

    [Fact]
    public void The_content_script_waits_for_the_document()
    {
        var bundle = PageBundle.Compose(Shim, Hook, Content);

        Assert.Contains("var __qbtRun = function(){\n" + Content + "\n};", bundle);
        Assert.Contains("document.readyState === 'loading'", bundle);
        Assert.Contains("addEventListener('DOMContentLoaded', __qbtRun, { once: true })", bundle);
        Assert.True(
            bundle.IndexOf(Content, StringComparison.Ordinal) < bundle.IndexOf("__qbtRun, { once: true }", StringComparison.Ordinal),
            "the content script is defined before it is scheduled");
    }

    [Fact]
    public void The_scripts_are_not_interpreted_as_a_template()
    {
        var bundle = PageBundle.Compose("{0}", "{{1}}", "$&$1${x}");

        Assert.Contains("{0}", bundle);
        Assert.Contains("{{1}}", bundle);
        Assert.Contains("$&$1${x}", bundle);
    }

    [Fact]
    public void The_bundle_only_depends_on_its_inputs()
    {
        Assert.Equal(PageBundle.Compose(Shim, Hook, Content), PageBundle.Compose(Shim, Hook, Content));
        Assert.NotEqual(PageBundle.Compose(Shim, Hook, Content), PageBundle.Compose(Shim, Hook, Content + " "));
    }

    [Fact]
    public void The_wrapper_adds_no_unbalanced_bracket()
    {
        var bundle = PageBundle.Compose("", "", "");

        Assert.Equal(bundle.Count(c => c == '{'), bundle.Count(c => c == '}'));
        Assert.Equal(bundle.Count(c => c == '('), bundle.Count(c => c == ')'));
    }
}
