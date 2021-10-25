using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;
using System.Diagnostics;
using NYoutubeDL;
using TagLib;
using Polly;
using File = System.IO.File;

namespace DiscordChatExporter.Core.Exporting
{
    internal partial class MediaDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly string _workingDirPath;
        private readonly bool _reuseMedia;

        // URL -> Local file path
        private readonly Dictionary<string, string> _pathCache = new(StringComparer.Ordinal);

        public MediaDownloader(HttpClient httpClient, string workingDirPath, bool reuseMedia)
        {
            _httpClient = httpClient;
            _workingDirPath = workingDirPath;
            _reuseMedia = reuseMedia;
        }

        public MediaDownloader(string workingDirPath, bool reuseMedia)
            : this(Http.Client, workingDirPath, reuseMedia) {}

        // Async download of media if it detects url in the discord api

        int i2 = 0;

        bool WrapperBreaksOnSecondTry = false;

        public async ValueTask<string> DownloadAsync(string url, string thumbnailUrl, bool hasEmbedUrl = false)
        {

            int i = 0;

            if (_pathCache.TryGetValue(url, out var cachedFilePath))
                return cachedFilePath;

            var fileName = GetFileNameFromUrl(url, hasEmbedUrl);
            var thumbnailName = "";
            var filePath = Path.Combine(_workingDirPath, fileName);

            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                thumbnailName = GetThumbnailNameFromUrl(thumbnailUrl, true);
            }

            // Reuse existing files if we're allowed to
            if (_reuseMedia && File.Exists(filePath))
                return _pathCache[url] = filePath;
            
            Process currentProcess = Process.GetCurrentProcess();

            Regex checkIfFormatsMerged = new Regex(@"^WARNING: Requested formats are incompatible");
            Regex checkIfPlaylistFormatsMerged = new Regex(@"^\[ffmpeg] Merging formats into");

            Regex getTrueExtension = new Regex(@"[^.]+$");

            Regex checkIfThrottled = new Regex(@"KiB/s ETA");

            TagLib.File tfile;

            string mergedFileFormatFixed = "";

            int trueExtensionLenght = 0;

            int throttleCounter = 0;

            

            YoutubeDL youtubeDl = new YoutubeDL();
            youtubeDl.Options.GeneralOptions.Update = true;
            youtubeDl.Options.VerbositySimulationOptions.Verbose = true;
            youtubeDl.Options.FilesystemOptions.Output = filePath;
            youtubeDl.RetrieveAllInfo = true;
            youtubeDl.StandardOutputEvent += async (sender, output) => {
                System.Diagnostics.Debug.WriteLine("STANDARDOUTPUT: " + output);
                if (checkIfThrottled.IsMatch(output) == true)
                {
                    throttleCounter = throttleCounter + 1;
                    if (throttleCounter == 15)
                    {

                        if (WrapperBreaksOnSecondTry == false)
                        {
                            youtubeDl.CancelDownload();
                            WrapperBreaksOnSecondTry = true;
                            throttleCounter = 0;
                            await youtubeDl.DownloadAsync(url);
                        }

                        // I don't know why it dosen't catch here, but it should
                        /*
                        try
                        {
                           if (WrapperBreaksOnSecondTry == false)
                           {
                               youtubeDl.CancelDownload();
                           } 
                        }
                        
                        catch (System.Threading.Tasks.TaskCanceledException) 
                        {
                            throttleCounter = 0;
                            await youtubeDl.DownloadAsync(url);
                            WrapperBreaksOnSecondTry = true;
                        }
                        */


                        throttleCounter = 0;
                        WrapperBreaksOnSecondTry = true;
                    }
                }
                if (checkIfThrottled.IsMatch(output) == false) { 
                    throttleCounter = 0; }
                if (checkIfPlaylistFormatsMerged.IsMatch(output) == true)
                {
                    try
                    {
                        int extensionStart = 31 + youtubeDl.Options.FilesystemOptions.Output.Length;
                        mergedFileFormatFixed = output.Substring(extensionStart).Remove(output.Substring(extensionStart).Length - 1);
                    }
                    catch (System.ArgumentOutOfRangeException)
                    {

                    }
                }
            };
            youtubeDl.StandardErrorEvent += (sender, e) => {
                System.Diagnostics.Debug.WriteLine(e);
                if (new Regex(@"^ERROR: unable to download video data: HTTP Error 403: Forbidden").IsMatch(e) == true)
                {
                    i2 = i2 + 1;
                    youtubeDl.CancelDownload();
                }
                if (checkIfFormatsMerged.IsMatch(e) == true)
                {
                    mergedFileFormatFixed = e.Substring(78).Remove(e.Substring(78).Length - 1).Insert(0, ".");
                    trueExtensionLenght = getTrueExtension.Match(youtubeDl.Options.FilesystemOptions.Output).Length + 1;
                }
            };

            // Does not work
            /* async ValueTask<string> Retry(string url)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("test");
                    await youtubeDl.DownloadAsync(url);
                    return "";
                }
                catch (System.Threading.Tasks.TaskCanceledException)
                {

                    await Retry (url);
                    return "";
                    throw;
                }
            }*/

            Directory.CreateDirectory(_workingDirPath);
            Directory.CreateDirectory(_workingDirPath + @"\Thumbnails");

            // This retries on IOExceptions which is dangerous as we're also working with files
            await Http.ExceptionPolicy.ExecuteAsync(async () =>
            {

                // Download the file
                if (!hasEmbedUrl)
                {
                    using var response = await _httpClient.GetAsync(url);
                    await using (var output = File.Create(filePath))
                    {
                        await response.Content.CopyToAsync(output);
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
                }
                else
                {
                    var testSupportedSites = await youtubeDl.GetDownloadInfoAsync(url);
                    // For or while loop required: https://gitlab.com/BrianAllred/NYoutubeDL/-/issues/40 Better to use a for loop to also accept webpage embeds after 5 tries of not finding a valid title. 5 tries ought to be enough for anybody.
                    for (int i = 0; i < 5; i++)
                    {
                        testSupportedSites = await youtubeDl.GetDownloadInfoAsync(url);
                        if (testSupportedSites.Title != null)
                        {
                            i = 5;
                        }
                    }
                    Regex checkIfUnsupportedSite = new Regex(@"^ERROR: Unsupported URL:");

                    string thumbnail;

                    if (thumbnailUrl != "")
                    {
                        using var thumbnailResponse = await _httpClient.GetAsync(thumbnailUrl);
                        await using (var output = File.Create(_workingDirPath + @"Thumbnails\" + thumbnailName))
                        {
                            await thumbnailResponse.Content.CopyToAsync(output);
                        }

                        thumbnail = _workingDirPath + @"Thumbnails\" + thumbnailName;
                    }
                    else
                    {
                        thumbnail = "";
                    }

                    if (testSupportedSites.Errors.Count != 0)
                    {
                        if (checkIfUnsupportedSite.IsMatch(testSupportedSites.Errors[0]))
                        {
                            using var response = await _httpClient.GetAsync(url);
                            System.Diagnostics.Debug.WriteLine("Output: " + filePath + ".html");
                            await using (var output = File.Create(filePath + ".html"))
                            {
                                await response.Content.CopyToAsync(output);
                            }

                            filePath = filePath + ".html";

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
                        }
                        else
                        {
                            try
                            {
                                if (((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Ext == null)
                                {
                                    youtubeDl.Options.FilesystemOptions.Output = filePath + ".html";
                                    System.Diagnostics.Debug.WriteLine("Output: " + youtubeDl.Options.FilesystemOptions.Output);
                                    await youtubeDl.DownloadAsync(url);

                                    filePath = filePath + ".html";
                                }
                                else
                                {
                                    youtubeDl.Options.FilesystemOptions.Output = filePath + "." + ((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Ext;
                                    System.Diagnostics.Debug.WriteLine("Output: " + youtubeDl.Options.FilesystemOptions.Output);
                                    await youtubeDl.DownloadAsync(url);

                                    filePath = filePath + "." + ((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Ext;

                                    try
                                    {
                                        if (mergedFileFormatFixed == "")
                                        {
                                            tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output);
                                            System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                            var picture = new Picture(thumbnail);
                                            picture.Type = TagLib.PictureType.FrontCover;
                                            var pictures = new Picture[1];
                                            pictures[0] = picture;
                                            tfile.Tag.Pictures = pictures;
                                            tfile.Save();
                                            System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                        }

                                        if (mergedFileFormatFixed != "")
                                        {
                                            tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output.Substring(0, youtubeDl.Options.FilesystemOptions.Output.Length - trueExtensionLenght) + mergedFileFormatFixed);
                                            System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                            var picture = new Picture(thumbnail);
                                            picture.Type = TagLib.PictureType.FrontCover;
                                            var pictures = new Picture[1];
                                            pictures[0] = picture;
                                            tfile.Tag.Pictures = pictures;
                                            tfile.Save();
                                            System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                        }
                                    }
                                    catch (UnsupportedFormatException)
                                    {

                                    }
                                }
                            }
                            catch (System.InvalidCastException)
                            {
                                youtubeDl.Options.FilesystemOptions.Output = filePath + ".mp4";
                                System.Diagnostics.Debug.WriteLine("Output: " + youtubeDl.Options.FilesystemOptions.Output);
                                await youtubeDl.DownloadAsync(url);

                                filePath = filePath + ".mp4";

                                try
                                {
                                    if (mergedFileFormatFixed == "")
                                    {
                                        tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output);
                                        System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                        var picture = new Picture(thumbnail);
                                        picture.Type = TagLib.PictureType.FrontCover;
                                        var pictures = new Picture[1];
                                        pictures[0] = picture;
                                        tfile.Tag.Pictures = pictures;
                                        tfile.Save();
                                        System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                    }

                                    if (mergedFileFormatFixed != "")
                                    {
                                        tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output.Substring(0, youtubeDl.Options.FilesystemOptions.Output.Length - trueExtensionLenght) + mergedFileFormatFixed);
                                        System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                        var picture = new Picture(thumbnail);
                                        picture.Type = TagLib.PictureType.FrontCover;
                                        var pictures = new Picture[1];
                                        pictures[0] = picture;
                                        tfile.Tag.Pictures = pictures;
                                        tfile.Save();
                                        System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                    }
                                }
                                catch (UnsupportedFormatException)
                                {
                                }

                                throw;
                            }
                            
                        }
                    }
                    else
                    {
                        if (testSupportedSites.Title == null)
                        {
                            using var response = await _httpClient.GetAsync(url);
                            System.Diagnostics.Debug.WriteLine("Output: " + youtubeDl.Options.FilesystemOptions.Output + ".html");
                            await using (var output = File.Create(youtubeDl.Options.FilesystemOptions.Output + ".html"))
                            {
                                await response.Content.CopyToAsync(output);
                            }

                            filePath = filePath + ".html";

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
                        }

                        // Set up another if else statement for playlist handling
                        else
                        {
                            try
                            {
                                if (((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Ext == null)
                                {
                                    using var response = await _httpClient.GetAsync(url);
                                    System.Diagnostics.Debug.WriteLine("Output: " + filePath + ".html");
                                    await using (var output = File.Create(filePath + ".html"))
                                    {
                                        await response.Content.CopyToAsync(output);
                                    }

                                    filePath = filePath + ".html";

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
                                }
                                else
                                {
                                    youtubeDl.Options.FilesystemOptions.Output = filePath + "." + ((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Ext;
                                    System.Diagnostics.Debug.WriteLine("Output: " + youtubeDl.Options.FilesystemOptions.Output);
                                    await youtubeDl.DownloadAsync(url);

                                    filePath = filePath + "." + ((NYoutubeDL.Models.VideoDownloadInfo)youtubeDl.Info).Ext;

                                    try
                                    {
                                        if (mergedFileFormatFixed == "")
                                        {
                                            tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output);
                                            System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                            var picture = new Picture(thumbnail);
                                            picture.Type = TagLib.PictureType.FrontCover;
                                            var pictures = new Picture[1];
                                            pictures[0] = picture;
                                            tfile.Tag.Pictures = pictures;
                                            tfile.Save();
                                            System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                        }

                                        if (mergedFileFormatFixed != "")
                                        {

                                            tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output.Substring(0, youtubeDl.Options.FilesystemOptions.Output.Length - trueExtensionLenght) + mergedFileFormatFixed);
                                            System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                            var picture = new Picture(thumbnail);
                                            picture.Type = TagLib.PictureType.FrontCover;
                                            var pictures = new Picture[1];
                                            pictures[0] = picture;
                                            tfile.Tag.Pictures = pictures;
                                            tfile.Save();
                                            System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                        }
                                    }
                                    catch (System.Exception)
                                    {
                                    }
                                }
                            }
                            catch (System.InvalidCastException)
                            {
                                if (((NYoutubeDL.Models.PlaylistDownloadInfo)youtubeDl.Info).Videos.Count == 0)
                                {
                                    using var response = await _httpClient.GetAsync(url);
                                    System.Diagnostics.Debug.WriteLine("Output: " + filePath + ".html");
                                    await using (var output = File.Create(filePath + ".html"))
                                    {
                                        await response.Content.CopyToAsync(output);
                                    }

                                    filePath = filePath + ".html";

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
                                }
                                else
                                {
                                    Directory.CreateDirectory(_workingDirPath + "\\" + "Playlist - " + youtubeDl.Info.Title);
                                    
                                    
                                    // youtubeDl.Options.FilesystemOptions.Output = _workingDirPath + "Playlist - " + youtubeDl.Info.Title + "\\Video - " + playlistIndex + $"{((((NYoutubeDL.Models.PlaylistDownloadInfo)youtubeDl.Info).Videos[0]).Title)}";
                                    for (i = 0; i < ((NYoutubeDL.Models.PlaylistDownloadInfo)youtubeDl.Info).Videos.Count; i++)
                                    {
                                        // make throttle safer
                                        i = i2;
                                        i2 = i;

                                        System.Diagnostics.Debug.WriteLine(i);
                                        youtubeDl.Options.FilesystemOptions.Output = _workingDirPath + "Playlist - " + youtubeDl.Info.Title + "\\Video - " + i;
                                        youtubeDl.Options.VideoSelectionOptions.PlaylistItems = (i + 1).ToString();
                                        youtubeDl.Options.PostProcessingOptions.EmbedThumbnail = true;
                                        await youtubeDl.DownloadAsync(url);

                                        i2++;

                                        throttleCounter = 0;
                                        filePath = youtubeDl.Options.FilesystemOptions.Output;

                                        try
                                        {
                                            if (mergedFileFormatFixed == "")
                                            {
                                                tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output);
                                                System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                                var picture = new Picture(thumbnail);
                                                picture.Type = TagLib.PictureType.FrontCover;
                                                var pictures = new Picture[1];
                                                pictures[0] = picture;
                                                tfile.Tag.Pictures = pictures;
                                                tfile.Save();
                                                System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                            }

                                            if (mergedFileFormatFixed != "")
                                            {
                                                tfile = TagLib.File.Create(youtubeDl.Options.FilesystemOptions.Output + mergedFileFormatFixed);
                                                System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                                var picture = new Picture(youtubeDl.Options.FilesystemOptions.Output + ".jpg");
                                                picture.Type = TagLib.PictureType.FrontCover;
                                                var pictures = new Picture[1];
                                                pictures[0] = picture;
                                                tfile.Tag.Pictures = pictures;
                                                tfile.Save();
                                                System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                            }
                                        }
                                        catch (UnsupportedFormatException)
                                        {
                                        }
                                    }

                                    /*
                                    if (mergedFileFormatFixed == "")
                                    {
                                        tfile = TagLib.File.Create(filePath);
                                        System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                        var picture = new Picture(thumbnail);
                                        picture.Type = TagLib.PictureType.FrontCover;
                                        var pictures = new Picture[1];
                                        pictures[0] = picture;
                                        tfile.Tag.Pictures = pictures;
                                        tfile.Save();
                                        System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                    }

                                    if (mergedFileFormatFixed != "")
                                    {
                                        try
                                        {
                                            tfile = TagLib.File.Create(filePath.Substring(0, filePath.Length - 4) + mergedFileFormatFixed);
                                            System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                            var picture = new Picture(thumbnail);
                                            picture.Type = TagLib.PictureType.FrontCover;
                                            var pictures = new Picture[1];
                                            pictures[0] = picture;
                                            tfile.Tag.Pictures = pictures;
                                            tfile.Save();
                                            System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            tfile = TagLib.File.Create(filePath + mergedFileFormatFixed);
                                            System.Diagnostics.Debug.WriteLine("Tag: " + tfile.TagTypes + "\n" + "Title: " + tfile.Tag.Title + "\n" + "Pictures: " + tfile.Tag.Pictures);
                                            var picture = new Picture(thumbnail);
                                            picture.Type = TagLib.PictureType.FrontCover;
                                            var pictures = new Picture[1];
                                            pictures[0] = picture;
                                            tfile.Tag.Pictures = pictures;
                                            tfile.Save();
                                            System.Diagnostics.Debug.WriteLine("img: " + tfile.Tag.Pictures);
                                            throw;
                                        }
                                    }*/
                                }
                            }
                        }
                    }
                }
            });

            // Make only for youtube-dl blocks
            // Some other stuff

            throttleCounter = 0;
            return _pathCache[url] = filePath;

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

        private static string GetFileNameFromUrl(string url, bool hasEmbedUrl)
        {
            var urlHash = GetUrlHash(url);

            int extensionLength = 0;

            string extensionCulled = "";

            string fileName = "";

            // Fixes gif urls by taking out the extension and the period, which stops it from getting into the filename and breaking the above set up. Should fix other urls with extensions in the name.
            if (hasEmbedUrl)
            {
                // Checks last 7 letters in url for a period denoting extension. There must be a better more reliable way to do this and ensure it works on every file extension, even the ones longer then 6 characters.
                if (Regex.IsMatch(url.Substring((url.Length - 7)), @"\."))
                {
                    extensionLength = Regex.Match(url, @"[^.]+$").Length + 1;
                    extensionCulled = url.Substring(0, url.Length - extensionLength);
                    fileName = Regex.Match(extensionCulled, @".+/([^?]*)").Groups[1].Value;
                }
                else
                {
                    // Try to extract file name from URL
                    fileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;
                }
            }
            else
            {
                // Try to extract file name from URL
                fileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;
            }

            // If it's not there, just use the URL hash as the file name
            if (string.IsNullOrWhiteSpace(fileName))
                return urlHash;

            // Otherwise, use the original file name but inject the hash in the middle
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            var fileExtension = Path.GetExtension(fileName);

            return PathEx.EscapePath(fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension);
        }

        private static string GetThumbnailNameFromUrl(string url, bool hasThumbnailUrl)
        {

            if (hasThumbnailUrl == true)
            {
                string urlEnd = Regex.Match(url, @"([^/]+$)").Value;

                var urlHash = GetUrlHash(url);

                return urlHash + "_" + urlEnd;
            }
            else
            {
                return "";
            }
        }
    }
}