using System.Diagnostics;
using OpenTelemetry;

namespace Erda.Core.Services;

/// <summary>
/// Tags every exported span with a top-level <c>app</c> attribute.
///
/// Why: Seq stores OTLP <b>resource</b> attributes (service.name, …) under the <c>@ra</c> field,
/// which isn't filterable like a normal property — so a span's <c>service.name=Erda</c> is NOT
/// found by <c>app = 'Erda'</c>, the dimension Erda's Serilog logs (and other apps) use. Span
/// <b>attributes</b>, by contrast, do become top-level filterable properties. Setting a no-dot
/// <c>app</c> tag therefore makes traces show up alongside the logs under one <c>app = 'Erda'</c>
/// filter. Registered before the exporters so the tag is present at export time.
/// </summary>
public sealed class AppTagSpanProcessor(string app) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity) => activity.SetTag("app", app);
}
