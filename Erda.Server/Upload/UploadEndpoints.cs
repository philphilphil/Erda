using Erda.Core.Configuration;
using Erda.Core.Upload;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Erda.Server.Upload;

/// <summary>
/// Maps <c>POST /upload</c>: an authenticated multipart audio upload (e.g. an iOS Shortcut) that is fed
/// into the same Apple-Voice-Memo pipeline as a WhatsApp share. The endpoint authenticates with a
/// bearer token, validates size, hands the file to <see cref="UploadIntake"/> (which enqueues it onto
/// the WhatsApp inbound queue), and returns <c>202</c> immediately — the memo result is delivered over
/// WhatsApp and saved to <c>1 Inbox/</c> by the existing worker.
///
/// Note: a Development instance with <c>WhatsApp:DevPrefix</c> set drops audio messages that lack the
/// prefix (identical to a shared WhatsApp voice memo), so an upload to such an instance is accepted
/// (202) but not processed. Production is unaffected (no prefix gating), and that is the deploy target.
/// </summary>
public static class UploadEndpoints
{
    public static void MapUploadEndpoint(this WebApplication app)
    {
        var upload = app.Services.GetRequiredService<IOptions<UploadOptions>>().Value;
        var whatsApp = app.Services.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Upload");

        if (!upload.Enabled)
        {
            log.LogInformation("Upload endpoint disabled (Upload:Enabled=false); POST /upload not mapped.");
            return;
        }

        // The pipeline transcribes, writes to the vault, and replies over WhatsApp — all of which need
        // the bridge. Fail fast at startup rather than accept uploads we can never answer.
        if (!whatsApp.Enabled)
            throw new InvalidOperationException(
                "Upload:Enabled=true requires WhatsApp:Enabled=true — the uploaded audio runs through the " +
                "voice-memo pipeline and the result is delivered over WhatsApp. Enable WhatsApp or set Upload__Enabled=false.");

        var maxBytes = (long)upload.MaxUploadMb * 1024 * 1024;
        const long multipartSlack = 1024 * 1024; // boundary/headers framing around a max-sized file part

        log.LogInformation("Upload endpoint enabled; mapping POST /upload (maxUploadMb={Max}).", upload.MaxUploadMb);

        app.MapPost("/upload", async (HttpRequest request, UploadIntake intake, ILoggerFactory lf, CancellationToken ct) =>
        {
            var inLog = lf.CreateLogger("UploadInbound");

            // 1) Authenticate before touching the (potentially large) body.
            if (!intake.IsAuthorized(request.Headers.Authorization.ToString()))
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            if (!request.HasFormContentType)
                return Results.Json(new { error = "expected multipart/form-data" }, statusCode: StatusCodes.Status400BadRequest);

            // Raise Kestrel's per-request body cap above the configured max so our own size check below
            // returns a clean JSON 413; bodies beyond even the slack are cut off by Kestrel (413, caught).
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = maxBytes + multipartSlack;

            // Raise the multipart length limit to match (default 128 MB), so an over-cap body is rejected
            // by Kestrel as a clean 413 rather than tripping the form reader's own limit (which surfaces
            // as a 400). Without this, a MaxUploadMb above ~128 would misreport oversize bodies as 400.
            request.HttpContext.Features.Set<IFormFeature>(
                new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = maxBytes + multipartSlack }));

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.Json(new { error = "file too large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            catch (Exception ex)
            {
                inLog.LogWarning(ex, "Rejected malformed /upload form.");
                return Results.Json(new { error = "invalid form data" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var file = form.Files.GetFile("audio");
            if (file is null || file.Length == 0)
                return Results.Json(new { error = "no audio file" }, statusCode: StatusCodes.Status400BadRequest);

            await using var stream = file.OpenReadStream();
            var outcome = await intake.IngestAsync(file.Length, stream, ct);
            return outcome switch
            {
                UploadOutcome.TooLarge => Results.Json(new { error = "file too large" }, statusCode: StatusCodes.Status413PayloadTooLarge),
                UploadOutcome.NoFile => Results.Json(new { error = "no audio file" }, statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Json(new { status = "accepted" }, statusCode: StatusCodes.Status202Accepted),
            };
        });
    }
}
