using System.Text.RegularExpressions;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// What makes it safe to draw an HTML file taken from a customer's machine. The page is untrusted: it may carry
/// scripts, links to other sites, images and style sheets that call out, forms and frames. Nothing in it may run,
/// reach the network, or take the viewer anywhere else. Three layers do that, so a gap in one is covered by the others:
/// a Content-Security-Policy placed first in the page (this class), settings that turn scripting off, and handlers that
/// cancel every navigation and every request (the view).
/// </summary>
public static partial class HtmlSandbox
{
    /// <summary>
    /// Nothing may be loaded or run. The page's own inline styles and images and fonts embedded in the page (data:) are
    /// allowed, because reports commonly embed them and they cannot reach out.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; "
        + "connect-src 'none'; frame-src 'none'; object-src 'none'; media-src 'none'; base-uri 'none'; "
        + "form-action 'none'; frame-ancestors 'none'";

    /// <summary>
    /// The same policy with the page's own inline scripts allowed, for pages that draw nothing without them. Everything
    /// else stays shut, so a script still cannot fetch, post, frame, open or navigate anywhere: there is nowhere for it
    /// to send anything. It can only change the page.
    /// </summary>
    public const string ScriptPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:; "
        + "connect-src 'none'; frame-src 'none'; object-src 'none'; media-src 'none'; base-uri 'none'; "
        + "form-action 'none'; frame-ancestors 'none'";

    // Pages are written for a white background. Put first so that a page's own styles still win.
    private const string BaseStyle = "<style>html{background-color:#ffffff;color:#000000;color-scheme:light}</style>";

    private static string PolicyElement(bool allowScripts) =>
        "<meta http-equiv=\"Content-Security-Policy\" content=\"" + (allowScripts ? ScriptPolicy : ContentSecurityPolicy) + "\">" + BaseStyle;

    /// <summary>
    /// The page with the policy as the first thing in it (after a doctype, which must stay first to keep the page's
    /// layout mode), then a plain light base style. A policy the page brings itself can only narrow this one, never
    /// widen it.
    /// </summary>
    public static string Prepare(string html, bool allowScripts = false)
    {
        var doctype = LeadingDoctype().Match(html);
        var at = doctype.Success ? doctype.Length : 0;
        return html.Insert(at, PolicyElement(allowScripts));
    }

    /// <summary>
    /// Whether the viewer may load <paramref name="uri"/> for the page. Only the page itself (handed over as text) and
    /// data: URIs inside it are; everything else, whatever it is and wherever it points, is refused.
    /// </summary>
    public static bool IsAllowedRequest(string uri) =>
        uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*<!doctype[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDoctype();
}
