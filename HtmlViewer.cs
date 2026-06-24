using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeTracer;

/// <summary>
/// Emits a sibling, FULLY SELF-CONTAINED .html next to each generated .md: the Mermaid call-graph
/// rendered into a viewer that fits the whole graph to the window (no scrolling to find it), with
/// mouse-wheel zoom and drag-pan — so a huge graph is visible at a glance and you zoom in where needed.
///
/// Why a separate file and not HTML inside the .md: Markdown DOES allow raw HTML, but GitHub / VS Code
/// (and every safe renderer) SANITISE it — &lt;script&gt;/&lt;style&gt;/handlers are stripped — so an
/// interactive viewer can never live inside the .md. A standalone .html is the only thing that survives.
///
/// "Self-contained" is literal: mermaid.min.js is embedded in the binary (assets/mermaid.min.js) and
/// inlined into every page, so the output opens with NO internet access (locked-down / Artifactory nets).
/// The .md is untouched — it still renders its ASCII + Mermaid on GitHub; the .html is a bonus viewer.
/// </summary>
public static class HtmlViewer
{
    // One timestamp for the whole CLI invocation, so every file a single run emits sorts together
    // (and runs never clobber each other — see Program.AutoOutPath). Computed once, on first use.
    public static readonly string RunStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

    private static string? _mermaidJs;
    private static string MermaidJs() => _mermaidJs ??= LoadMermaidJs();

    private static string LoadMermaidJs()
    {
        var asm = typeof(HtmlViewer).GetTypeInfo().Assembly;
        using var s = asm.GetManifestResourceStream("mermaid.min.js")
            ?? throw new InvalidOperationException(
                "embedded resource 'mermaid.min.js' not found (assets/mermaid.min.js must be an EmbeddedResource).");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ```mermaid ... ``` fenced block(s); inner text only (the %%{init}%% line + flowchart body).
    private static readonly Regex MermaidBlock =
        new(@"```mermaid\r?\n(.*?)\r?\n```", RegexOptions.Singleline | RegexOptions.Compiled);

    private static List<string> ExtractMermaid(string md)
    {
        var list = new List<string>();
        foreach (Match m in MermaidBlock.Matches(md))
        {
            var src = m.Groups[1].Value.TrimEnd();
            if (src.Length > 0) list.Add(src);
        }
        return list;
    }

    /// Writes "<md-without-.md>.html" next to the .md and returns its path, or null if the .md has no
    /// Mermaid graph (e.g. a &lt;2-node finding — nothing worth a viewer).
    public static async Task<string?> WriteSiblingAsync(string mdPath, string mdText)
    {
        var sources = ExtractMermaid(mdText);
        if (sources.Count == 0) return null;
        var htmlPath = Path.ChangeExtension(mdPath, ".html");
        var title = Path.GetFileNameWithoutExtension(mdPath);
        await Compat.WriteAllTextAsync(htmlPath, Build(title, sources));
        return htmlPath;
    }

    /// Builds the standalone HTML. `sources` are the RAW mermaid blocks (entity-encoded exactly as in
    /// the .md); they are handed to mermaid.render() verbatim via a JSON array, so the viewer renders
    /// byte-identically to GitHub (no HTML-decoding surprises from dropping them into the DOM as text).
    public static string Build(string title, IReadOnlyList<string> sources)
    {
        // JSON escaping doubles as HTML/script safety: System.Text.Json emits <, >, & as \uXXXX, so the
        // array literal can never contain a stray "</script>" or break out of the inline <script>.
        var sourcesJson = JsonSerializer.Serialize(sources);
        var panes = new StringBuilder();
        for (int i = 0; i < sources.Count; i++)
            panes.AppendLine("  <section class=\"pane\"><div class=\"stage\"></div></section>");

        var t = Esc(title);
        var sb = new StringBuilder();
        sb.Append(@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>").Append(t).Append(@"</title>
<style>
  *{box-sizing:border-box}
  html,body{margin:0;height:100%}
  body{background:#f6f8fa;color:#24292f;font:13px/1.45 -apple-system,Segoe UI,system-ui,sans-serif}
  #bar{position:fixed;inset:0 0 auto 0;height:40px;display:flex;align-items:center;gap:8px;
       padding:0 12px;background:#fff;border-bottom:1px solid #d0d7de;z-index:5}
  #bar .title{font-weight:600;max-width:48vw;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  #bar button{font:inherit;cursor:pointer;background:#f6f8fa;border:1px solid #d0d7de;border-radius:6px;
              padding:4px 11px}
  #bar button:hover{background:#eaeef2}
  #bar .count{color:#57606a}
  #bar .hint{margin-left:auto;color:#57606a;font-size:12px}
  .panes{position:absolute;inset:40px 0 0 0;overflow:auto}
  .pane{position:relative;height:100%;overflow:hidden;background:#fff;cursor:grab;touch-action:none}
  .pane+.pane{border-top:8px solid #d0d7de}
  .pane.grabbing{cursor:grabbing}
  .stage{position:absolute;top:0;left:0;transform-origin:0 0;will-change:transform}
  .stage svg{display:block}
  .err{color:#cf222e;padding:14px;font-family:ui-monospace,Consolas,monospace;white-space:pre-wrap}
</style>
</head>
<body>
<div id=""bar"">
  <span class=""title"">").Append(t).Append(@"</span>");
        if (sources.Count > 1)
            sb.Append(@"
  <span class=""count"">").Append(sources.Count).Append(@" graphs</span>");
        sb.Append(@"
  <button data-act=""fit"">Fit</button>
  <button data-act=""in"">+</button>
  <button data-act=""out"">&minus;</button>
  <span class=""hint"">wheel = zoom &middot; drag = pan &middot; double-click = fit</span>
</div>
<div class=""panes"">
").Append(panes).Append(@"</div>
<script>
").Append(MermaidJs()).Append(@"
</script>
<script>
(function(){
  var SOURCES = ").Append(sourcesJson).Append(@";
  var PANES = Array.prototype.slice.call(document.querySelectorAll('.pane'));
  var controllers = [];
  var active = 0;
  PANES.forEach(function(p,i){ p.addEventListener('mouseenter', function(){ active = i; }); });

  mermaid.initialize({ startOnLoad:false, securityLevel:'loose', theme:'default',
                       flowchart:{ useMaxWidth:false, htmlLabels:true } });

  function makeController(pane){
    var stage = pane.querySelector('.stage');
    var s = 1, tx = 0, ty = 0;
    function apply(){ stage.style.transform = 'translate('+tx+'px,'+ty+'px) scale('+s+')'; }
    function natural(){
      var svg = stage.querySelector('svg');
      if (svg && svg.viewBox && svg.viewBox.baseVal && svg.viewBox.baseVal.width)
        return { w: svg.viewBox.baseVal.width, h: svg.viewBox.baseVal.height };
      if (svg) { try { var b = svg.getBBox(); if (b.width) return { w:b.width, h:b.height }; } catch(e){} }
      return { w: stage.offsetWidth || 1, h: stage.offsetHeight || 1 };
    }
    function fit(){
      var n = natural(), pw = pane.clientWidth, ph = pane.clientHeight;
      s = Math.min(pw / n.w, ph / n.h) * 0.96;
      if (!isFinite(s) || s <= 0) s = 1;
      tx = (pw - n.w * s) / 2; ty = (ph - n.h * s) / 2; apply();
    }
    function zoomAt(cx, cy, f){
      var ns = Math.max(0.05, Math.min(40, s * f));
      tx = cx - (cx - tx) * (ns / s); ty = cy - (cy - ty) * (ns / s); s = ns; apply();
    }
    pane.addEventListener('wheel', function(e){
      e.preventDefault();
      var r = pane.getBoundingClientRect();
      zoomAt(e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * 0.0015));
    }, { passive:false });
    var dragging = false, lx = 0, ly = 0;
    pane.addEventListener('pointerdown', function(e){
      dragging = true; lx = e.clientX; ly = e.clientY;
      pane.classList.add('grabbing'); pane.setPointerCapture(e.pointerId);
    });
    pane.addEventListener('pointermove', function(e){
      if (!dragging) return;
      tx += e.clientX - lx; ty += e.clientY - ly; lx = e.clientX; ly = e.clientY; apply();
    });
    function end(){ dragging = false; pane.classList.remove('grabbing'); }
    pane.addEventListener('pointerup', end);
    pane.addEventListener('pointercancel', end);
    pane.addEventListener('dblclick', fit);
    return {
      fit: fit,
      zoomCenter: function(f){ var r = pane.getBoundingClientRect(); zoomAt(r.width/2, r.height/2, f); }
    };
  }

  (function render(){
    var i = 0;
    function next(){
      if (i >= SOURCES.length){ done(); return; }
      var stage = PANES[i].querySelector('.stage');
      var id = 'g' + i, idx = i; i++;
      Promise.resolve()
        .then(function(){ return mermaid.render(id, SOURCES[idx]); })
        .then(function(res){ stage.innerHTML = res.svg; })
        .catch(function(err){
          stage.innerHTML = '<div class=""err"">render error: ' + (err && err.message ? err.message : err) + '</div>';
        })
        .then(next);
    }
    function done(){
      PANES.forEach(function(p){ controllers.push(makeController(p)); });
      requestAnimationFrame(function(){ controllers.forEach(function(c){ c.fit(); }); });
    }
    next();
  })();

  document.getElementById('bar').addEventListener('click', function(e){
    var act = e.target && e.target.getAttribute && e.target.getAttribute('data-act');
    if (!act || !controllers.length) return;
    var c = controllers[active] || controllers[0];
    if (act === 'fit') controllers.forEach(function(x){ x.fit(); });
    else if (act === 'in') c.zoomCenter(1.25);
    else if (act === 'out') c.zoomCenter(1 / 1.25);
  });
})();
</script>
</body>
</html>");
        return sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
