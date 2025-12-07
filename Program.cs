using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IaYank;

internal static class Program
{
    private static readonly HttpClient HttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private sealed class Options
    {
        public List<string> Identifiers { get; } = new();
        public string? IncludeFilter { get; set; }
        public bool DryRun { get; set; }
    }

    private sealed class ItemContext
    {
        public required string Identifier { get; init; }
        public required List<IaFileEntry> Files { get; init; }
        public long TotalBytes { get; init; }
        public bool HasPrivateFiles { get; init; }
        public long ExistingCompleteBytes { get; set; }
    }

    private static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args);
        if (options == null || options.Identifiers.Count == 0)
        {
            PrintUsage();
            return 1;
        }

        // Normalize identifiers / URLs
        var normalizedIds = options.Identifiers
            .Select(GetIdentifierFromArg)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (normalizedIds.Count == 0)
        {
            Console.WriteLine("Could not determine any valid Internet Archive identifiers from input.");
            return 1;
        }

        Console.WriteLine("Internet Archive Yank");
        Console.WriteLine($"Identifiers: {string.Join(", ", normalizedIds)}");
        if (!string.IsNullOrEmpty(options.IncludeFilter))
        {
            Console.WriteLine($"Include filter: \"{options.IncludeFilter}\" (case-insensitive substring)");
        }
        Console.WriteLine($"Dry run: {(options.DryRun ? "yes" : "no")}");
        Console.WriteLine();

        // 1) Fetch metadata for all identifiers (anonymous)
        var items = new List<ItemContext>();

        foreach (var identifier in normalizedIds)
        {
            JsonDocument metadata;
            try
            {
                metadata = await FetchMetadataAsync(identifier);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching metadata for '{identifier}': {ex.Message}");
                return 1;
            }

            using (metadata)
            {
                var files = ExtractFileEntries(metadata.RootElement);

                if (!string.IsNullOrEmpty(options.IncludeFilter))
                {
                    files = files
                        .Where(f => f.Name.Contains(options.IncludeFilter,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                if (files.Count == 0)
                {
                    Console.WriteLine($"No files match the given criteria for '{identifier}'.");
                    continue;
                }

                var totalBytes = files.Sum(f => f.SizeBytes);
                var hasPrivate = files.Any(f => f.IsPrivate);

                items.Add(new ItemContext
                {
                    Identifier = identifier,
                    Files = files,
                    TotalBytes = totalBytes,
                    HasPrivateFiles = hasPrivate,
                    ExistingCompleteBytes = 0
                });
            }
        }

        if (items.Count == 0)
        {
            Console.WriteLine("No files to process across all identifiers.");
            return 0;
        }

        var overallTotalBytes = items.Sum(i => i.TotalBytes);
        var anyPrivateAcrossAll = items.Any(i => i.HasPrivateFiles);

        if (options.DryRun)
        {
            Console.WriteLine($"Total items:           {items.Count}");
            Console.WriteLine($"Total files (overall): {items.Sum(i => i.Files.Count)}");
            Console.WriteLine($"Total archive size:    {FormatSize(overallTotalBytes)}");
            Console.WriteLine($"Contains private files across any item: {(anyPrivateAcrossAll ? "yes" : "no")}");
            Console.WriteLine();

            foreach (var item in items)
            {
                Console.WriteLine($"== {item.Identifier} ==");
                Console.WriteLine($"  Files: {item.Files.Count}, Size: {FormatSize(item.TotalBytes)}, Has private: {(item.HasPrivateFiles ? "yes" : "no")}");
                int idx = 0;
                foreach (var f in item.Files)
                {
                    idx++;
                    var privFlag = f.IsPrivate ? " (private)" : "";
                    Console.WriteLine($"  [{idx}] {f.Name}{privFlag} - {FormatSize(f.SizeBytes)}");
                }

                Console.WriteLine();
            }

            Console.WriteLine("No files were downloaded (dry run).");
            return 0;
        }

        // 2) Prompt for cookie once if ANY item has private files
        string? cookieHeader = null;
        if (anyPrivateAcrossAll)
        {
            Console.WriteLine("One or more items contain files that require a logged-in session to download.");
            Console.WriteLine("Paste your full 'Cookie' header from your browser's dev tools.");
            Console.Write("Cookie: ");
            cookieHeader = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                Console.WriteLine("No Cookie header provided. Aborting.");
                return 1;
            }
        }

        // 3) Base directory for downloads is the current directory
        var baseDir = Directory.GetCurrentDirectory();

        // 4) Scan for already-complete files across all items
        long existingCompleteBytesOverall = 0;

        foreach (var item in items)
        {
            var targetDir = Path.Combine(baseDir, item.Identifier);

            foreach (var file in item.Files)
            {
                if (file.SizeBytes <= 0)
                    continue;

                var (finalPath, _) = GetFilePaths(targetDir, file);
                if (!File.Exists(finalPath))
                    continue;

                var fi = new FileInfo(finalPath);
                if (fi.Length == file.SizeBytes)
                {
                    item.ExistingCompleteBytes += fi.Length;
                    existingCompleteBytesOverall += fi.Length;
                }
            }
        }

        var requiredBytesOverall = overallTotalBytes - existingCompleteBytesOverall;
        if (requiredBytesOverall < 0) requiredBytesOverall = 0;

        Console.WriteLine($"Total items:                  {items.Count}");
        Console.WriteLine($"Total files (overall):        {items.Sum(i => i.Files.Count)}");
        Console.WriteLine($"Total archive size:           {FormatSize(overallTotalBytes)}");
        Console.WriteLine($"Already present on disk:      {FormatSize(existingCompleteBytesOverall)}");
        Console.WriteLine($"Additional download required: {FormatSize(requiredBytesOverall)}");
        Console.WriteLine($"Contains private files:       {(anyPrivateAcrossAll ? "yes" : "no")}");
        Console.WriteLine();

        // 5) Check free space on the volume backing the current directory
        if (!HasEnoughFreeSpace(baseDir, requiredBytesOverall, out var availableBytes))
        {
            Console.WriteLine("Not enough free space on target volume.");
            Console.WriteLine($"Required:  {FormatSize(requiredBytesOverall)}");
            Console.WriteLine($"Available: {FormatSize(availableBytes)}");
            return 1;
        }

        Console.WriteLine($"Base download directory: {baseDir}");
        Console.WriteLine($"Free space: {FormatSize(availableBytes)} (sufficient)");
        Console.WriteLine();

        // 6) Download / resume files sequentially across all items
        long downloadedBytesSoFar = 0;
        var globalStopwatch = Stopwatch.StartNew();

        int successCount = 0;
        int failCount = 0;
        int skippedCount = 0;

        foreach (var item in items)
        {
            var targetDir = Path.Combine(baseDir, item.Identifier);
            Directory.CreateDirectory(targetDir);

            Console.WriteLine($"=== Processing {item.Identifier} ===");
            Console.WriteLine();

            int index = 0;
            int totalCount = item.Files.Count;

            foreach (var file in item.Files)
            {
                index++;

                var (finalPath, _) = GetFilePaths(targetDir, file);

                // Resumable behavior: skip already-complete files
                bool skip = false;
                long existingBytes = 0;

                if (File.Exists(finalPath))
                {
                    var fi = new FileInfo(finalPath);
                    existingBytes = fi.Length;

                    if (file.SizeBytes > 0 && fi.Length == file.SizeBytes)
                    {
                        Console.WriteLine($"[{index}/{totalCount}] SKIP (already complete) - {file.Name}");
                        skip = true;
                    }
                    else if (file.SizeBytes > 0 && fi.Length < file.SizeBytes)
                    {
                        Console.WriteLine($"[{index}/{totalCount}] Found partial file, restarting - {file.Name}");
                        try
                        {
                            File.Delete(finalPath);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  Warning: could not delete partial file: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[{index}/{totalCount}] Existing file size mismatch, redownloading - {file.Name}");
                        try
                        {
                            File.Delete(finalPath);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  Warning: could not delete existing file: {ex.Message}");
                        }
                    }
                }

                if (skip)
                {
                    successCount++;
                    skippedCount++;
                    downloadedBytesSoFar += existingBytes;

                    globalStopwatch.Stop();
                    var elapsedSkip = globalStopwatch.Elapsed;
                    globalStopwatch.Start();

                    if (downloadedBytesSoFar > 0 && elapsedSkip.TotalSeconds > 0.5 && overallTotalBytes > downloadedBytesSoFar)
                    {
                        var remainingBytes = overallTotalBytes - downloadedBytesSoFar;
                        var avgSpeed = downloadedBytesSoFar / elapsedSkip.TotalSeconds;
                        var etaSeconds = remainingBytes / avgSpeed;
                        var eta = TimeSpan.FromSeconds(etaSeconds);

                        Console.WriteLine(
                            $"Estimated time remaining: {FormatTimeSpan(eta)} (avg {FormatSpeed(avgSpeed)})");
                    }

                    Console.WriteLine();
                    continue;
                }

                Console.WriteLine($"[{index}/{totalCount}] {file.Name} ({FormatSize(file.SizeBytes)})");

                try
                {
                    var result = await DownloadAndVerifyFileAsync(
                        item.Identifier,
                        file,
                        targetDir,
                        file.IsPrivate,     // only private files get cookies
                        cookieHeader);

                    downloadedBytesSoFar += result.BytesDownloaded;
                    globalStopwatch.Stop();
                    var elapsed = globalStopwatch.Elapsed;
                    globalStopwatch.Start();

                    if (result.Success)
                    {
                        successCount++;
                        Console.WriteLine($"[{index}/{totalCount}] OK - {file.Name}");
                    }
                    else
                    {
                        failCount++;
                        Console.WriteLine($"[{index}/{totalCount}] FAILED - {file.Name}");
                        Console.WriteLine($"  Reason: {result.Error}");
                    }

                    if (downloadedBytesSoFar > 0 && elapsed.TotalSeconds > 0.5 && overallTotalBytes > downloadedBytesSoFar)
                    {
                        var remainingBytes = overallTotalBytes - downloadedBytesSoFar;
                        var avgSpeed = downloadedBytesSoFar / elapsed.TotalSeconds; // bytes/sec
                        var etaSeconds = remainingBytes / avgSpeed;
                        var eta = TimeSpan.FromSeconds(etaSeconds);

                        Console.WriteLine(
                            $"Estimated time remaining: {FormatTimeSpan(eta)} (avg {FormatSpeed(avgSpeed)})");
                    }

                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    failCount++;
                    Console.WriteLine($"[{index}/{totalCount}] FAILED - {file.Name}");
                    Console.WriteLine($"  Unexpected error: {ex.Message}");
                    Console.WriteLine();
                    // Keep going to next file even on unexpected errors
                }
            }
        }

        Console.WriteLine("Download complete.");
        Console.WriteLine($"  Succeeded: {successCount}");
        Console.WriteLine($"  Failed:    {failCount}");
        Console.WriteLine($"  Skipped:   {skippedCount} (already complete)");

        return failCount == 0 ? 0 : 2;
    }

    // --- Argument parsing & helpers ---

    private static Options? ParseArgs(string[] args)
    {
        if (args.Length == 0)
            return null;

        var opts = new Options();
        int i = 0;

        while (i < args.Length)
        {
            var arg = args[i];

            if (arg == "--dry-run")
            {
                opts.DryRun = true;
                i++;
            }
            else if (arg.StartsWith("--include=", StringComparison.Ordinal))
            {
                opts.IncludeFilter = arg.Substring("--include=".Length);
                i++;
            }
            else if (arg == "--include")
            {
                if (i + 1 >= args.Length)
                    return null;
                opts.IncludeFilter = args[i + 1];
                i += 2;
            }
            else if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                Console.WriteLine($"Unknown option: {arg}");
                return null;
            }
            else
            {
                // positional identifier/url
                opts.Identifiers.Add(arg);
                i++;
            }
        }

        if (opts.Identifiers.Count == 0)
            return null;

        return opts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  ia-yank [--include <substring>] [--dry-run] <identifier-or-url> [<identifier-or-url> ...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --include <substring>   Only download files whose names contain this substring (case-insensitive).");
        Console.WriteLine("  --dry-run               Show which files would be downloaded and total size, but do not download.");
        Console.WriteLine();
    }

    private static string GetIdentifierFromArg(string arg)
    {
        arg = arg.Trim();

        if (Uri.TryCreate(arg, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], "details", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segments[i], "download", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[i + 1];
                }
            }

            return segments.Length > 0 ? segments[^1] : string.Empty;
        }

        return arg;
    }

    private static async Task<JsonDocument> FetchMetadataAsync(string identifier)
    {
        var url = $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    // --- Metadata model ---

    private sealed class IaFileEntry
    {
        public required string Name { get; init; }
        public long SizeBytes { get; init; }
        public string? Md5 { get; init; }
        public bool IsPrivate { get; init; }
    }

    private static List<IaFileEntry> ExtractFileEntries(JsonElement root)
    {
        var list = new List<IaFileEntry>();

        if (!root.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.ValueKind != JsonValueKind.Object)
                continue;

            if (!fileElement.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameProp.GetString();
            if (string.IsNullOrEmpty(name))
                continue;

            long size = 0;
            if (fileElement.TryGetProperty("size", out var sizeProp) &&
                sizeProp.ValueKind == JsonValueKind.String &&
                long.TryParse(sizeProp.GetString(), out var parsedSize))
            {
                size = parsedSize;
            }

            string? md5 = null;
            if (fileElement.TryGetProperty("md5", out var md5Prop) &&
                md5Prop.ValueKind == JsonValueKind.String)
            {
                md5 = md5Prop.GetString();
                if (string.IsNullOrWhiteSpace(md5))
                {
                    md5 = null;
                }
            }

            bool isPrivate = false;
            if (fileElement.TryGetProperty("private", out var privProp) &&
                privProp.ValueKind == JsonValueKind.String)
            {
                var privStr = privProp.GetString();
                isPrivate = string.Equals(privStr, "true", StringComparison.OrdinalIgnoreCase);
            }

            list.Add(new IaFileEntry
            {
                Name = name,
                SizeBytes = size,
                Md5 = md5,
                IsPrivate = isPrivate
            });
        }

        return list;
    }

    private static (string finalPath, string tempPath) GetFilePaths(string baseDirectory, IaFileEntry file)
    {
        var relativePathParts = file.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var localPath = baseDirectory;
        for (int i = 0; i < relativePathParts.Length - 1; i++)
        {
            localPath = Path.Combine(localPath, relativePathParts[i]);
        }

        var fileName = relativePathParts[^1];
        var finalPath = Path.Combine(localPath, fileName);
        var tempPath = finalPath + ".part";
        return (finalPath, tempPath);
    }

    private static bool HasEnoughFreeSpace(string baseDir, long requiredBytes, out long availableBytes)
    {
        var fullPath = Path.GetFullPath(baseDir);
        var drive = new DriveInfo(fullPath);
        availableBytes = drive.AvailableFreeSpace;
        return availableBytes >= requiredBytes;
    }

    // --- Downloading ---

    private sealed class DownloadResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public long BytesDownloaded { get; init; }
        public TimeSpan Duration { get; init; }
    }

    private static async Task<DownloadResult> DownloadAndVerifyFileAsync(
        string identifier,
        IaFileEntry file,
        string baseDirectory,
        bool isPrivateFile,
        string? cookieHeader)
    {
        var (finalPath, tempPath) = GetFilePaths(baseDirectory, file);

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var downloadUrl = BuildDownloadUrl(identifier, file.Name);

        var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (isPrivateFile && !string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var sw = Stopwatch.StartNew();
        long totalRead = 0;

        var lastProgressUpdate = TimeSpan.Zero;
        long lastBytesForSpeed = 0;
        var lastTimeForSpeed = TimeSpan.Zero;

        try
        {
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult
                {
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    BytesDownloaded = 0,
                    Duration = sw.Elapsed
                };
            }

            await using (var responseStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                int read;

                var contentLength = file.SizeBytes > 0 ? file.SizeBytes : (long?)null;

                while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;

                    var elapsed = sw.Elapsed;
                    if ((elapsed - lastProgressUpdate).TotalMilliseconds >= 250) // ~4x/sec
                    {
                        var deltaBytes = totalRead - lastBytesForSpeed;
                        var deltaTime = (elapsed - lastTimeForSpeed).TotalSeconds;

                        double speedBytesPerSec = 0;
                        if (deltaTime > 0 && deltaBytes > 0)
                        {
                            speedBytesPerSec = deltaBytes / deltaTime;
                        }

                        lastBytesForSpeed = totalRead;
                        lastTimeForSpeed = elapsed;
                        lastProgressUpdate = elapsed;

                        string progress;
                        if (contentLength.HasValue && contentLength.Value > 0)
                        {
                            var pct = (double)totalRead / contentLength.Value * 100.0;
                            progress =
                                $"{FormatSize(totalRead)}/{FormatSize(contentLength.Value)} ({pct:0.0}%) @ {FormatSpeed(speedBytesPerSec)}";
                        }
                        else
                        {
                            progress = $"{FormatSize(totalRead)} downloaded @ {FormatSpeed(speedBytesPerSec)}";
                        }

                        WriteProgressLine(progress);
                    }
                }

                // Always write a final 100% progress line
                var totalElapsed = sw.Elapsed;
                double finalSpeed = totalElapsed.TotalSeconds > 0
                    ? totalRead / totalElapsed.TotalSeconds
                    : 0;

                string finalProgress;
                if (contentLength.HasValue && contentLength.Value > 0 && totalRead == contentLength.Value)
                {
                    finalProgress =
                        $"{FormatSize(totalRead)}/{FormatSize(contentLength.Value)} (100.0%) @ {FormatSpeed(finalSpeed)}";
                }
                else if (contentLength.HasValue && contentLength.Value > 0)
                {
                    var pct = (double)totalRead / contentLength.Value * 100.0;
                    finalProgress =
                        $"{FormatSize(totalRead)}/{FormatSize(contentLength.Value)} ({pct:0.0}%) @ {FormatSpeed(finalSpeed)}";
                }
                else
                {
                    finalProgress = $"{FormatSize(totalRead)} downloaded @ {FormatSpeed(finalSpeed)}";
                }

                WriteProgressLine(finalProgress);
                Console.WriteLine();
            }

            // Verify size if we know it
            var fi = new FileInfo(tempPath);
            if (file.SizeBytes > 0 && fi.Length != file.SizeBytes)
            {
                File.Delete(tempPath);
                return new DownloadResult
                {
                    Success = false,
                    Error = $"Size mismatch. Expected {file.SizeBytes} bytes, got {fi.Length} bytes.",
                    BytesDownloaded = totalRead,
                    Duration = sw.Elapsed
                };
            }

            // Verify MD5 if available (only for freshly downloaded files)
            if (!string.IsNullOrEmpty(file.Md5))
            {
                var expectedMd5 = file.Md5.Trim().ToLowerInvariant();
                var actualMd5 = await ComputeFileMd5HexAsync(tempPath);

                if (!string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    return new DownloadResult
                    {
                        Success = false,
                        Error = $"MD5 mismatch. Expected {expectedMd5}, got {actualMd5}.",
                        BytesDownloaded = totalRead,
                        Duration = sw.Elapsed
                    };
                }
            }

            // Move temp file into place (overwrite existing)
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);

            return new DownloadResult
            {
                Success = true,
                Error = null,
                BytesDownloaded = totalRead,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            sw.Stop();
            // If something blows up mid-download, the .part file stays; next run we treat it as partial and restart.
        }
    }

    private static void WriteProgressLine(string text)
    {
        const int padWidth = 80;
        if (text.Length < padWidth)
        {
            text = text.PadRight(padWidth);
        }

        Console.Write("\r" + text);
    }

    private static string BuildDownloadUrl(string identifier, string fileName)
    {
        var segments = fileName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                               .Select(Uri.EscapeDataString);
        var path = string.Join("/", segments);
        var idEscaped = Uri.EscapeDataString(identifier);

        return $"https://archive.org/download/{idEscaped}/{path}";
    }

    private static async Task<string> ComputeFileMd5HexAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    // --- Formatting helpers ---

    private static string FormatSize(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;
        const double TB = GB * 1024.0;

        double value;
        string unit;

        if (bytes >= TB)
        {
            value = bytes / TB;
            unit = "TB";
        }
        else if (bytes >= GB)
        {
            value = bytes / GB;
            unit = "GB";
        }
        else if (bytes >= MB)
        {
            value = bytes / MB;
            unit = "MB";
        }
        else if (bytes >= KB)
        {
            value = bytes / KB;
            unit = "KB";
        }
        else
        {
            value = bytes;
            unit = "bytes";
        }

        return $"{value:0.0} {unit}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0.0)
            return "0 B/s";

        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;

        double value;
        string unit;

        if (bytesPerSecond >= GB)
        {
            value = bytesPerSecond / GB;
            unit = "GB/s";
        }
        else if (bytesPerSecond >= MB)
        {
            value = bytesPerSecond / MB;
            unit = "MB/s";
        }
        else if (bytesPerSecond >= KB)
        {
            value = bytesPerSecond / KB;
            unit = "KB/s";
        }
        else
        {
            value = bytesPerSecond;
            unit = "B/s";
        }

        return $"{value:0.0} {unit}";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1.0)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        }

        if (ts.TotalMinutes >= 1.0)
        {
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        return $"{ts.Seconds}s";
    }
}
