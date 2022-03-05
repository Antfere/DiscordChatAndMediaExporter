using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;
using NYoutubeDL;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
// using TagLib;

namespace DiscordChatExporter.Core.Exporting;

internal partial class MediaDownloader
{
    private readonly string _workingDirPath;
    private readonly bool _reuseMedia;
    private string thumbnailName = "";

    // File paths of already downloaded media
    private readonly Dictionary<string, string> _pathCache = new(StringComparer.Ordinal);

    public MediaDownloader(string workingDirPath, bool reuseMedia)
    {
        _workingDirPath = workingDirPath;
        _reuseMedia = reuseMedia;
    }

    public async ValueTask<string> DownloadAsync(string url, CancellationToken cancellationToken = default, string thumbnailUrl = "", bool youtubeDLP = false)
    {
        if (_pathCache.TryGetValue(url, out var cachedFilePath))
            return cachedFilePath;

        // Checking if attachment or embed, this will set the behaviour for everything below this
        bool hasEmbedUrl = true;
        // This will break if the attachment doesen't conform to the regexes below
        // Although I haven't seen an attachment without this set up
        // It will default to embed anyways
        Regex checkIfAttachment1 = new Regex("https://cdn.discordapp.com/attachments/");
        Regex checkIfAttachment2 = new Regex("https://media.discordapp.net/attachments/");
        Regex extensionCheck = new @Regex(@"(\.[^.]*)$");
        if (checkIfAttachment1.IsMatch(url) == true || checkIfAttachment2.IsMatch(url) == true) { hasEmbedUrl = false; }

        var fileName = GetFileNameFromUrl(url);
        
        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            thumbnailName = GetThumbnailNameFromUrl(thumbnailUrl, true);
        }
        var filePath = Path.Combine(_workingDirPath, fileName);

        // Reuse existing files if we're allowed to
        if (_reuseMedia && File.Exists(filePath))
            return _pathCache[url] = filePath;

        Directory.CreateDirectory(_workingDirPath);
        Directory.CreateDirectory(_workingDirPath + @"\Thumbnails");

        if (thumbnailUrl != "")
        {
            await Http.ExceptionPolicy.ExecuteAsync(async () =>
            {
                using var thumbnailResponse = await Http.Client.GetAsync(thumbnailUrl);
                await using (var output = File.Create(_workingDirPath + @"\Thumbnails\" + thumbnailName))
                {
                    await thumbnailResponse.Content.CopyToAsync(output);
                }
            });
        }

        if (youtubeDLP && hasEmbedUrl)
        {
            var youtubeDl = new YoutubeDLP();
            youtubeDl.Options.GeneralOptions.Update = true;
            youtubeDl.Options.VerbositySimulationOptions.Verbose = true;
            var test = Path.GetFullPath(_workingDirPath);
            youtubeDl.Options.FilesystemOptions.Paths = test;
            youtubeDl.Options.PostProcessingOptions.EmbedThumbnail = true;
            youtubeDl.Options.PostProcessingOptions.EmbedChapters = true;
            youtubeDl.Options.PostProcessingOptions.AddMetadata = true;
            string titleAndID = "";
            Regex ExtensionRegexBasedOnId = new Regex("");
            string extension;

            // youtubeDl.RetrieveAllInfo = true;
            youtubeDl.StandardOutputEvent += (sender, output) => {
                System.Diagnostics.Debug.WriteLine("STANDARDOUTPUT: " + output);
                // Gets final true file path
                if (ExtensionRegexBasedOnId.IsMatch(output) == true)
                {
                    extension = ExtensionRegexBasedOnId.Match(output).Groups[1].Value;
                    filePath = _workingDirPath + "\\" + titleAndID + extension;
                }
            };
            youtubeDl.StandardErrorEvent += (sender, e) => {
                System.Diagnostics.Debug.WriteLine(e);
                /*if (new Regex(@"^ERROR: unable to download video data: HTTP Error 403: Forbidden").IsMatch(e) == true)
                {
                    youtubeDl.CancelDownload();
                }*/
            };

            // I should add a counter here to see how many times it tends to fail without the for loop trick
            var testSupportedSites = await youtubeDl.GetDownloadInfoAsync(url);

            if (testSupportedSites.Title == null) {
                // From my testing this is still required
                // For or while loop required: https://gitlab.com/BrianAllred/NYoutubeDL/-/issues/40 Better to use a for loop to also accept webpage embeds after 5 tries of not finding a valid title. 5 tries ought to be enough for anybody.
                for (int i = 0; i < 5; i++)
                {
                    testSupportedSites = await youtubeDl.GetDownloadInfoAsync(url);
                    if (testSupportedSites.Title != null)
                    {
                        i = 5;
                    }
                }
            }

            // There should never be more than one error... And if there is the first one will be the unsupported url error.
            if (hasEmbedUrl && (testSupportedSites.Title != null))
            {
                if (youtubeDl.Info.ToString() == "NYoutubeDL.Models.PlaylistDownloadInfo" && ((NYoutubeDL.Models.PlaylistDownloadInfo)youtubeDl.Info).Videos.Count != 0)
                {
                    Directory.CreateDirectory(_workingDirPath + youtubeDl.Info.Title);
                    youtubeDl.Options.FilesystemOptions.Paths = _workingDirPath + youtubeDl.Info.Title;
                    await youtubeDl.DownloadAsync(url);
                    return _pathCache[url] = _workingDirPath + youtubeDl.Info.Title;
                }
                else if (youtubeDl.Info.ToString() == "NYoutubeDL.Models.VideoDownloadInfo")
                {
                    titleAndID = (youtubeDl.Info.Title + " [" + ((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Id + "]");
                    youtubeDl.Options.FilesystemOptions.Paths = _workingDirPath + @"\";
                    if (youtubeDl.Info.Title != "")
                    {
                        ExtensionRegexBasedOnId = new Regex($"\\[{((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Id}\\](.\\w*)+(?:\")");
                    }
                    await youtubeDl.DownloadAsync(url);
                    return _pathCache[url] = filePath;
                }
                // Not all website urls get embedded, but the ones that do and don't have any valid media will be downloaded as html here
                // I might add a more general url grabber for url's that don't embed and save them as html aswell.
                else
                {
                    // This retries on IOExceptions which is dangerous as we're also working with files
                    await Http.ExceptionPolicy.ExecuteAsync(async () =>
                    {
                        // Download the file
                        using var response = await Http.Client.GetAsync(url, cancellationToken);
                        await using (var output = File.Create(filePath + ".html"))
                        {
                            await response.Content.CopyToAsync(output, cancellationToken);
                        }

                        // Try to set the file date according to the last-modified header
                        try
                        {
                            var lastModified = response.Content.Headers.TryGetValue("Last-Modified")?.Pipe(s =>
                                DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                                    ? date
                                    : (DateTimeOffset?)null
                            );

                            if (lastModified is not null)
                            {
                                File.SetCreationTimeUtc(filePath, lastModified.Value.UtcDateTime);
                                File.SetLastWriteTimeUtc(filePath, lastModified.Value.UtcDateTime);
                                File.SetLastAccessTimeUtc(filePath, lastModified.Value.UtcDateTime);
                            }
                        }
                        catch
                        {
                            // This can apparently fail for some reason.
                            // https://github.com/Tyrrrz/DiscordChatExporter/issues/585
                            // Updating file dates is not a critical task, so we'll just
                            // ignore exceptions thrown here.
                        }
                    });
                }
                // Youtube-dlp really dislikes having 3 back slashes at the end of the path in quotes
                // This makes it 5 backslashes, I guess one escapes the end quotation mark, and the 4 others become 2?
            }

            else
            {
                await Http.ExceptionPolicy.ExecuteAsync(async () =>
                {
                    // Download the file
                    using var response = await Http.Client.GetAsync(url, cancellationToken);
                    if (filePath.Count(x => x == '.') < 2)
                    {
                        await using (var output = File.Create(filePath + ".html"))
                        {
                            await response.Content.CopyToAsync(output, cancellationToken);
                        }
                    }
                    else
                    {
                        await using (var output = File.Create(filePath))
                        {
                            await response.Content.CopyToAsync(output, cancellationToken);
                        }
                    }

                    // Try to set the file date according to the last-modified header
                    try
                    {
                        var lastModified = response.Content.Headers.TryGetValue("Last-Modified")?.Pipe(s =>
                            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                                ? date
                                : (DateTimeOffset?)null
                        );

                        if (lastModified is not null)
                        {
                            File.SetCreationTimeUtc(filePath, lastModified.Value.UtcDateTime);
                            File.SetLastWriteTimeUtc(filePath, lastModified.Value.UtcDateTime);
                            File.SetLastAccessTimeUtc(filePath, lastModified.Value.UtcDateTime);
                        }
                    }
                    catch
                    {
                        // This can apparently fail for some reason.
                        // https://github.com/Tyrrrz/DiscordChatExporter/issues/585
                        // Updating file dates is not a critical task, so we'll just
                        // ignore exceptions thrown here.
                    }
                });
                // Can also put a return statement here and remove the "!youtubeDLP" flag below.
            }

        }        

        // If embed download fails due to network error it should default back here
        // If this fails due to network error then the media simply won't be downloaded, but the original url will be returned
        else
        {
            // This retries on IOExceptions which is dangerous as we're also working with files
            await Http.ExceptionPolicy.ExecuteAsync(async () =>
            {
                // Download the file
                using var response = await Http.Client.GetAsync(url, cancellationToken);
                // (extensionCheck.Match(filePath).Value == "")
                if (filePath.Count(x => x == '.') < 2)
                {
                    await using (var output = File.Create(filePath + ".html"))
                    {
                        await response.Content.CopyToAsync(output, cancellationToken);
                    }
                }
                else
                {
                    await using (var output = File.Create(filePath))
                    {
                        await response.Content.CopyToAsync(output, cancellationToken);
                    }
                }
                // Try to set the file date according to the last-modified header
                try
                {
                    var lastModified = response.Content.Headers.TryGetValue("Last-Modified")?.Pipe(s =>
                        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                            ? date
                            : (DateTimeOffset?)null
                    );

                    if (lastModified is not null)
                    {
                        File.SetCreationTimeUtc(filePath, lastModified.Value.UtcDateTime);
                        File.SetLastWriteTimeUtc(filePath, lastModified.Value.UtcDateTime);
                        File.SetLastAccessTimeUtc(filePath, lastModified.Value.UtcDateTime);
                    }
                }
                catch
                {
                    // This can apparently fail for some reason.
                    // https://github.com/Tyrrrz/DiscordChatExporter/issues/585
                    // Updating file dates is not a critical task, so we'll just
                    // ignore exceptions thrown here.
                }
            });
        }

        if (filePath.Count(x => x == '.') < 2)
        {
            // Will download embeds as an html file if youtubeDLP option is not selected.
            return _pathCache[url] = filePath + ".html";
        }
        else
        {
            return _pathCache[url] = filePath;
        }
    }
}

internal partial class MediaDownloader
{
    private static string GetUrlHash(string url)
    {
        using var hash = SHA256.Create();

        var data = hash.ComputeHash(Encoding.UTF8.GetBytes(url));
        return data.ToHex().Truncate(5); // 5 chars ought to be enough for anybody
    }

    private static string GetFileNameFromUrl(string url)
    {
        var urlHash = GetUrlHash(url);

        // Try to extract file name from URL
        var fileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);

        return PathEx.EscapeFileName(fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension);
    }

    private static string GetThumbnailNameFromUrl(string url, bool hasThumbnailUrl)
    {

        if (hasThumbnailUrl == true)
        {
            string urlEnd = Regex.Match(url, @"([^/]+$)").Value;

            var urlHash = GetUrlHash(url);

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(urlEnd);
            var fileExtension = Path.GetExtension(urlEnd);

            return fileNameWithoutExtension.Truncate(42) + "-" + urlHash + fileExtension;
        }
        else
        {
            return "";
        }
    }

}