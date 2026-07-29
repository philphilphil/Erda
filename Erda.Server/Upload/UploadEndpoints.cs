using Erda.Core.Configuration;
using Erda.Core.Upload;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Erda.Server.Upload;

/// <summary>
/// Maps <c>POST /upload</c>: an authenticated audio upload (e.g. an iOS Shortcut) that is fed into the
/// same Apple-Voice-Memo pipeline as a WhatsApp share. Accepts either a raw audio body (iOS Shortcut
/// "Request Body: File") or <c>multipart/form-data</c> with a file field named <c>audio</c>. The
/// endpoint authenticates with a bearer token, validates size, hands the file to <see cref="UploadIntake"/> (which enqueues it onto
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

            // Raise Kestrel's per-request body cap above the configured max so reads/drains are bounded
            // and an oversize body yields a clean JSON 413 rather than a mid-upload connection reset.
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = maxBytes + multipartSlack;

            // Return an error only AFTER draining any unread request body. A client still uploading when
            // the server responds early (notably iOS Shortcuts) otherwise sees the early response as a
            // dropped connection — "The network connection was lost." — instead of the real status code.
            // Bounded by the cap above, and only on the reject paths, so the cost is acceptable for a
            // single-user, bearer-protected endpoint.
            async Task<IResult> Reject(int status, string error)
            {
                try { await request.Body.CopyToAsync(Stream.Null, ct); }
                catch { /* already consumed, reset, or over-cap — nothing more to drain */ }
                return Results.Json(new { error }, statusCode: status);
            }

            // Authenticate before reading the (potentially large) body.
            if (!intake.IsAuthorized(request.Headers.Authorization.ToString()))
                return await Reject(StatusCodes.Status401Unauthorized, "unauthorized");

            // Two accepted request shapes:
            //   • raw body  — iOS Shortcut "Request Body: File" posts the audio bytes directly.
            //   • multipart — curl -F / Shortcut "Form" with a file field named "audio".
            Stream audio;
            long? declaredLength;
            string? fileName = null;
            if (request.HasFormContentType)
            {
                // Match the multipart length limit to the body cap so an over-cap part is a clean 413,
                // not the form reader's own limit (which would surface as a 400).
                request.HttpContext.Features.Set<IFormFeature>(
                    new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = maxBytes + multipartSlack }));

                IFormCollection form;
                try
                {
                    form = await request.ReadFormAsync(ct);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return await Reject(StatusCodes.Status413PayloadTooLarge, "file too large");
                }
                catch (Exception ex)
                {
                    inLog.LogWarning(ex, "Rejected malformed /upload form.");
                    return await Reject(StatusCodes.Status400BadRequest, "invalid form data");
                }

                var file = form.Files.GetFile("audio");
                if (file is null)
                    return Results.Json(new { error = "no audio file (expected a raw body or a form field named 'audio')" }, statusCode: StatusCodes.Status400BadRequest);
                audio = file.OpenReadStream();
                declaredLength = file.Length;
                fileName = file.FileName; // original name → shown in the archive; raw bodies have none
            }
            else
            {
                // Raw audio body. Content-Length (iOS sets it) gives the size up front; when absent the
                // bounded save in UploadIntake still enforces the cap against the bytes written. A raw
                // body carries no filename; an optional X-Filename header lets a Shortcut supply one.
                audio = request.Body;
                declaredLength = request.ContentLength;
                var hinted = request.Headers["X-Filename"].ToString();
                if (!string.IsNullOrWhiteSpace(hinted)) fileName = hinted;
            }

            try
            {
                var outcome = await intake.IngestAsync(declaredLength, audio, fileName, ct);
                return outcome switch
                {
                    UploadOutcome.TooLarge => await Reject(StatusCodes.Status413PayloadTooLarge, "file too large"),
                    UploadOutcome.NoFile => await Reject(StatusCodes.Status400BadRequest, "no audio (empty body)"),
                    _ => Results.Json(new { status = "accepted" }, statusCode: StatusCodes.Status202Accepted),
                };
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                // Kestrel cut off a raw body that overran the cap mid-read.
                return await Reject(StatusCodes.Status413PayloadTooLarge, "file too large");
            }
            finally
            {
                await audio.DisposeAsync();
            }
        });
    }
}
