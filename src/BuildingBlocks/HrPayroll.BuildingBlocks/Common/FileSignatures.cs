namespace HrPayroll.BuildingBlocks.Common;

/// <summary>
/// Detects upload types from their leading bytes, never from the client-supplied content type or file name.
/// SVG and HTML are never accepted (they can carry script).
/// </summary>
public static class FileSignatures
{
    public static string? DetectImage(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    public static string? DetectImageOrPdf(byte[] b) =>
        b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-'
            ? "application/pdf"
            : DetectImage(b);

    /// <summary>PDF, or a Word .docx (a ZIP container holding word/document.xml).</summary>
    public static string? DetectDocument(byte[] b)
    {
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return "application/pdf";
        if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4 && ContainsAscii(b, "word/"))
            return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        return null;
    }

    /// <summary>
    /// Chat attachments: images, PDF, Office documents (new and legacy), ZIP, and plain-text .txt / .csv.
    /// Returns the content type and the extension to store, or null when the file is not allowed.
    /// </summary>
    public static (string ContentType, string Extension)? DetectChatFile(byte[] b, string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (DetectImage(b) is { } image) return (image, image switch { "image/png" => ".png", "image/jpeg" => ".jpg", _ => ".webp" });
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a')
            return ("image/gif", ".gif");
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return ("application/pdf", ".pdf");

        if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4)   // ZIP container
        {
            if (ContainsAscii(b, "word/")) return ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");
            if (ContainsAscii(b, "xl/")) return ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx");
            if (ContainsAscii(b, "ppt/")) return ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx");
            return ("application/zip", ".zip");
        }

        if (b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0)   // legacy Office (OLE)
            return ext switch
            {
                ".doc" => ("application/msword", ".doc"),
                ".xls" => ("application/vnd.ms-excel", ".xls"),
                ".ppt" => ("application/vnd.ms-powerpoint", ".ppt"),
                _ => null,
            };

        if (ext is ".txt" or ".csv" && IsPlainText(b)) return (ext == ".csv" ? "text/csv" : "text/plain", ext);
        return null;
    }

    private static bool IsPlainText(byte[] b)
    {
        if (b.AsSpan().IndexOf((byte)0) >= 0) return false;
        try
        {
            _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Letters, digits, space, '-' and '_' only, max 80 chars, plus the given extension.</summary>
    public static string SafeFileName(string? name, string extension, string fallback)
    {
        var baseName = Path.GetFileNameWithoutExtension(name ?? "");
        baseName = new string(baseName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
        if (baseName.Length == 0) baseName = fallback;
        if (baseName.Length > 80) baseName = baseName[..80];
        return baseName + extension;
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        return haystack.AsSpan().IndexOf(n) >= 0;
    }

    /// <summary>Reads an upload into memory after checking its size.</summary>
    public static async Task<byte[]> ReadAsync(IFormFile file, int maxBytes, CancellationToken ct)
    {
        if (file.Length == 0) throw new AppException("The selected file is empty.");
        if (file.Length > maxBytes) throw new AppException($"File must be {maxBytes / 1024} KB or smaller.");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public static string? ToDataUrl(byte[]? bytes, string? contentType) =>
        bytes is { Length: > 0 } && contentType is not null ? $"data:{contentType};base64,{Convert.ToBase64String(bytes)}" : null;
}
