using Microsoft.AspNetCore.Http;

namespace RankMaster2.Server.Media;

/// <summary>
/// Streams the original video bytes, with Range support, and does nothing else.
/// <para/>
/// SERVER_PLAN.md § 3.4 and SERVER_SPEC.md § 1.1: <b>the server never decodes a video frame</b>.
/// No transcoding, no segmenting, no poster frames, no duration or codec probing. The file on
/// disk is the response body. If anything in this file ever needs a decoder, the decision in the
/// plan has been broken, not this class.
/// </summary>
public static class VideoStreamer
{
    /// <summary>§ 12.4: <c>Content-Type</c> is by extension, with no sniffing of the bytes.</summary>
    public static string ContentTypeOf(string id) =>
        Path.GetExtension(id).TrimStart('.').ToLowerInvariant() switch
        {
            "mp4" => "video/mp4",
            "webm" => "video/webm",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            _ => "application/octet-stream",
        };

    /// <summary>
    /// Writes the whole file, or one range of it, per § 12.4. The caller has already dealt with
    /// <c>If-None-Match</c>; this handles <c>Range</c> and <c>If-Range</c>.
    /// </summary>
    public static async Task WriteAsync(
        HttpContext context,
        string path,
        long length,
        string etag,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.ContentType = ContentTypeOf(Path.GetFileName(path));

        // Always advertised, on a 200 and a 206 alike, so a player knows it may seek.
        response.Headers.AcceptRanges = "bytes";

        var rangeHeader = context.Request.Headers.Range.ToString();
        var range = MediaHttp.IfRangeAllowsRange(context, etag, TryGetLastWriteTimeUtc(path))
            ? RangeParser.Parse(rangeHeader, length)
            : RangeResult.None;

        switch (range.Outcome)
        {
            case RangeOutcome.Unsatisfiable:
                // 416 carries Content-Range: bytes */size, and the standard error envelope.
                response.Headers.ContentRange = RangeParser.UnsatisfiedContentRange(length);
                await MediaHttp.WriteErrorAsync(
                    context,
                    Contracts.ErrorCodes.RangeNotSatisfiable,
                    "The requested byte range cannot be satisfied.",
                    new { sizeBytes = length },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return;

            case RangeOutcome.Satisfiable:
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = RangeParser.ContentRange(range.Range, length);
                response.ContentLength = range.Range.Length;
                if (HttpMethods.IsHead(context.Request.Method))
                    return;
                await SendFileAsync(context, path, range.Range.From, range.Range.Length, cancellationToken).ConfigureAwait(false);
                return;

            default:
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentLength = length;
                if (HttpMethods.IsHead(context.Request.Method))
                    return;
                if (length > 0)
                    await SendFileAsync(context, path, 0, length, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// The file's actual last-write time, for the HTTP-date form of <c>If-Range</c>
    /// (<see cref="MediaHttp.IfRangeAllowsRange"/>). Best-effort: a stat that fails here (the file
    /// vanished between the caller's own check and this one) just means that form of <c>If-Range</c>
    /// is not honoured for this response, not a reason to fail the request — the entity-tag form
    /// still works, and § 12.4's ordinary "no Range" and "unconditional Range" paths never call
    /// this at all.
    /// </summary>
    private static DateTimeOffset? TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>SendFileAsync</c> where the server supports it, so a 200 MB file on a USB drive never
    /// passes through a managed buffer. The fallback copies a slice with a small buffer; either
    /// way the server's memory does not grow with the size of the video.
    /// </summary>
    private static async Task SendFileAsync(HttpContext context, string path, long offset, long count, CancellationToken cancellationToken)
    {
        var sendFile = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        if (sendFile is not null)
        {
            await sendFile.SendFileAsync(path, offset, count, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 64 * 1024,
        });

        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }
}
