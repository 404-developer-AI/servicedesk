using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Servicedesk.Domain.Signatures;

namespace Servicedesk.Infrastructure.Signatures;

/// Renders a signature block-tree to email-safe HTML: nested
/// <c>&lt;table role="presentation"&gt;</c> layout with inline CSS only
/// (Outlook has no usable CSS box-model), `{{agent.*}}` tokens substituted for
/// the sending agent, and every image referenced as an inline <c>cid:</c> so
/// it renders without an external fetch. The bytes to attach are returned
/// alongside the HTML in <see cref="RenderedSignature.Assets"/>.
public sealed class SignatureRenderer : ISignatureRenderer
{
    private static readonly Regex BrSplit = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagStrip = new("<[^>]+>", RegexOptions.Compiled);

    private const string DefaultFontFamily = "Arial, 'Helvetica Neue', Helvetica, sans-serif";

    private readonly ISignatureHtmlSanitizer _sanitizer;

    public SignatureRenderer(ISignatureHtmlSanitizer sanitizer)
    {
        _sanitizer = sanitizer;
    }

    public RenderedSignature Render(
        SignatureDesign design,
        SignatureVariables vars,
        IReadOnlyList<SignatureAsset> assets)
    {
        var ctx = new RenderContext(vars, assets.ToDictionary(a => a.Id));
        var sb = new StringBuilder();

        var fontFamily = string.IsNullOrWhiteSpace(design.FontFamily) ? DefaultFontFamily : design.FontFamily!;
        var widthStyle = design.MaxWidthPx is > 0 ? $"max-width:{design.MaxWidthPx}px;" : string.Empty;
        var bgStyle = !string.IsNullOrWhiteSpace(design.Background) ? $"background-color:{CssValue(design.Background!)};" : string.Empty;

        // The marker class lets us identify our own signature later (e.g. to
        // avoid stacking duplicates) without affecting rendering.
        sb.Append("<div class=\"sd-signature\">");
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;")
          .Append(bgStyle)
          .Append(widthStyle)
          .Append("font-family:").Append(CssValue(fontFamily)).Append(';')
          .Append("color:#222222;font-size:13px;line-height:1.4;\">");

        foreach (var row in design.Rows ?? Array.Empty<SignatureRow>())
        {
            var rowHtml = RenderRow(row, ctx);
            if (rowHtml.Length == 0) continue;
            sb.Append("<tr><td style=\"padding:0;\">").Append(rowHtml).Append("</td></tr>");
        }

        sb.Append("</table></div>");

        return new RenderedSignature(sb.ToString(), ctx.CidAssets);
    }

    private string RenderRow(SignatureRow row, RenderContext ctx)
    {
        var columns = row.Columns ?? Array.Empty<SignatureColumn>();
        if (columns.Count == 0) return string.Empty;

        var cells = new List<string>(columns.Count);
        foreach (var col in columns)
        {
            var inner = new StringBuilder();
            foreach (var block in col.Blocks ?? Array.Empty<SignatureBlock>())
            {
                inner.Append(RenderBlock(block, ctx));
            }
            // Keep an empty cell so the column grid stays aligned, but only if
            // the row has more than one column; a lone empty column collapses.
            if (inner.Length == 0 && columns.Count == 1) continue;

            var width = col.WidthPct is > 0 ? $" width=\"{col.WidthPct}%\"" : string.Empty;
            var valign = NormalizeValign(col.VAlign);
            cells.Add($"<td{width} valign=\"{valign}\" style=\"padding:0;vertical-align:{valign};\">{inner}</td>");
        }

        if (cells.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"border-collapse:collapse;\"><tr>");
        foreach (var c in cells) sb.Append(c);
        sb.Append("</tr></table>");
        return sb.ToString();
    }

    private string RenderBlock(SignatureBlock block, RenderContext ctx)
    {
        switch ((block.Type ?? "text").ToLowerInvariant())
        {
            case "text":
                return RenderText(block.Html, ctx, disclaimer: false);
            case "disclaimer":
                return RenderText(block.Html, ctx, disclaimer: true);
            case "image":
                return RenderImage(block, ctx);
            case "divider":
                return RenderDivider(block);
            case "spacer":
                return RenderSpacer(block);
            case "social":
                return RenderSocial(block, ctx);
            default:
                return string.Empty; // unknown block kind → render nothing
        }
    }

    private string RenderText(string? html, RenderContext ctx, bool disclaimer)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var clean = _sanitizer.Sanitize(html);
        var substituted = SubstituteAndCollapse(clean, ctx.Vars);
        if (IsVisuallyEmpty(substituted)) return string.Empty;

        if (disclaimer)
            return $"<div style=\"font-size:11px;line-height:1.35;color:#8a8a8a;\">{substituted}</div>";
        return $"<div>{substituted}</div>";
    }

    private string RenderImage(SignatureBlock block, RenderContext ctx)
    {
        string? cid = null;

        // Dynamic source (sender photo) wins over a static asset.
        if (string.Equals(block.Variable, SignatureTokens.AgentPhoto, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(ctx.Vars.PhotoBlobHash))
                cid = ctx.AllocCid(ctx.Vars.PhotoBlobHash!, ctx.Vars.PhotoMime ?? "image/jpeg", "photo");
        }
        else if (!string.IsNullOrWhiteSpace(block.AssetId)
                 && Guid.TryParse(block.AssetId, out var assetId)
                 && ctx.Assets.TryGetValue(assetId, out var asset))
        {
            cid = ctx.AllocCid(asset.ContentHash, asset.MimeType, asset.OriginalFilename);
        }

        if (cid is null) return string.Empty; // no resolvable source → collapse

        var attrs = new StringBuilder();
        if (block.WidthPx is > 0) attrs.Append($" width=\"{block.WidthPx}\"");
        if (block.HeightPx is > 0) attrs.Append($" height=\"{block.HeightPx}\"");

        var style = new StringBuilder("display:block;border:0;outline:none;text-decoration:none;");
        if (block.RadiusPx is > 0) style.Append($"border-radius:{block.RadiusPx}px;");

        var alt = WebUtility.HtmlEncode(block.Alt ?? string.Empty);
        var img = $"<img src=\"cid:{cid}\"{attrs} alt=\"{alt}\" style=\"{style}\" />";

        if (!string.IsNullOrWhiteSpace(block.Href) && IsSafeHref(block.Href!))
            return $"<a href=\"{WebUtility.HtmlEncode(block.Href)}\" style=\"text-decoration:none;\">{img}</a>";
        return img;
    }

    private static string RenderDivider(SignatureBlock block)
    {
        var color = CssValue(string.IsNullOrWhiteSpace(block.Color) ? "#dddddd" : block.Color!);
        var thickness = block.ThicknessPx is > 0 ? block.ThicknessPx!.Value : 1;
        var margin = block.MarginPx ?? 8;
        return $"<div style=\"border-top:{thickness}px solid {color};font-size:0;line-height:0;margin:{margin}px 0;\">&nbsp;</div>";
    }

    private static string RenderSpacer(SignatureBlock block)
    {
        var h = block.HeightPx is > 0 ? block.HeightPx!.Value : 8;
        return $"<div style=\"height:{h}px;line-height:{h}px;font-size:0;\">&nbsp;</div>";
    }

    private string RenderSocial(SignatureBlock block, RenderContext ctx)
    {
        var items = block.Social ?? Array.Empty<SignatureSocialItem>();
        if (items.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;\"><tr>");
        var any = false;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Url) || !IsSafeHref(item.Url)) continue;

            string inner;
            if (!string.IsNullOrWhiteSpace(item.AssetId)
                && Guid.TryParse(item.AssetId, out var assetId)
                && ctx.Assets.TryGetValue(assetId, out var asset))
            {
                var cid = ctx.AllocCid(asset.ContentHash, asset.MimeType, asset.OriginalFilename);
                inner = $"<img src=\"cid:{cid}\" width=\"24\" height=\"24\" alt=\"{WebUtility.HtmlEncode(item.Network)}\" style=\"display:block;border:0;\" />";
            }
            else
            {
                // No icon bytes → degrade to a labelled text link rather than a
                // broken image.
                inner = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(item.Network) ? item.Url : item.Network);
            }

            sb.Append("<td style=\"padding:0 4px;\"><a href=\"")
              .Append(WebUtility.HtmlEncode(item.Url))
              .Append("\" style=\"text-decoration:none;color:#222222;\">")
              .Append(inner)
              .Append("</a></td>");
            any = true;
        }
        sb.Append("</tr></table>");
        return any ? sb.ToString() : string.Empty;
    }

    /// Substitutes `{{agent.*}}` tokens and drops any `<br>`-separated line
    /// whose tokens ALL resolve to empty — so "Tel: {{agent.phone}}" vanishes
    /// when the agent has no phone, while a static line (no token) always stays.
    private static string SubstituteAndCollapse(string html, SignatureVariables vars)
    {
        var tokens = TokenMap(vars);
        var segments = BrSplit.Split(html);
        var kept = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            var presentTokens = tokens.Where(t => segment.Contains(t.Token, StringComparison.Ordinal)).ToList();
            if (presentTokens.Count > 0 && presentTokens.All(t => string.IsNullOrEmpty(t.Value)))
                continue; // every token on this line is empty → drop the line

            var line = segment;
            foreach (var (token, value) in tokens)
                line = line.Replace(token, WebUtility.HtmlEncode(value ?? string.Empty), StringComparison.Ordinal);
            kept.Add(line);
        }

        return string.Join("<br>", kept);
    }

    private static (string Token, string Value)[] TokenMap(SignatureVariables v) => new[]
    {
        (SignatureTokens.AgentFullName, v.FullName),
        (SignatureTokens.AgentFirstName, v.FirstName),
        (SignatureTokens.AgentLastName, v.LastName),
        (SignatureTokens.AgentJobTitle, v.JobTitle),
        (SignatureTokens.AgentEmail, v.Email),
        (SignatureTokens.AgentPhone, v.Phone),
        (SignatureTokens.AgentMobile, v.Mobile),
    };

    private static bool IsVisuallyEmpty(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;
        if (html.Contains("<img", StringComparison.OrdinalIgnoreCase)) return false;
        var text = WebUtility.HtmlDecode(TagStrip.Replace(html, " "))
            .Replace(" ", " ")
            .Trim();
        return text.Length == 0;
    }

    private static string NormalizeValign(string? v) => (v ?? "top").ToLowerInvariant() switch
    {
        "middle" => "middle",
        "bottom" => "bottom",
        _ => "top",
    };

    /// Only http(s)/mailto/tel links are allowed in generated markup; anything
    /// else (javascript:, data:) is dropped to inert.
    private static bool IsSafeHref(string href)
    {
        var h = href.Trim();
        return h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);
    }

    /// Defensive: a colour / font value lands inside a style="" attribute, so a
    /// stray quote or semicolon-injected property must not break out. Drop the
    /// characters that could close the attribute or start a new declaration we
    /// did not intend.
    private static string CssValue(string value)
    {
        var trimmed = value.Trim();
        var filtered = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c is '"' or '<' or '>' or '\\' or '{' or '}') continue;
            filtered.Append(c);
        }
        return filtered.ToString();
    }

    private sealed class RenderContext
    {
        private readonly Dictionary<string, string> _cidByHash = new(StringComparer.Ordinal);
        private readonly List<SignatureCidAsset> _assets = new();
        private int _counter;

        public RenderContext(SignatureVariables vars, Dictionary<Guid, SignatureAsset> assets)
        {
            Vars = vars;
            Assets = assets;
        }

        public SignatureVariables Vars { get; }
        public Dictionary<Guid, SignatureAsset> Assets { get; }
        public IReadOnlyList<SignatureCidAsset> CidAssets => _assets;

        /// Returns a stable cid for a content hash, registering the asset once
        /// so the same image reused across blocks attaches a single time.
        public string AllocCid(string contentHash, string mimeType, string fileName)
        {
            if (_cidByHash.TryGetValue(contentHash, out var existing)) return existing;
            var cid = $"sigimg-{++_counter}@servicedesk.local";
            _cidByHash[contentHash] = cid;
            _assets.Add(new SignatureCidAsset(cid, contentHash, mimeType,
                string.IsNullOrWhiteSpace(fileName) ? $"image-{_counter}" : fileName));
            return cid;
        }
    }
}

/// The rendered signature plus the inline images it references. Each
/// <see cref="SignatureCidAsset"/> must be attached to the outgoing message as
/// an inline part whose Content-Id equals <see cref="SignatureCidAsset.Cid"/>.
public sealed record RenderedSignature(string Html, IReadOnlyList<SignatureCidAsset> Assets);

public sealed record SignatureCidAsset(string Cid, string ContentHash, string MimeType, string FileName);

public interface ISignatureRenderer
{
    RenderedSignature Render(
        SignatureDesign design,
        SignatureVariables vars,
        IReadOnlyList<SignatureAsset> assets);
}
