using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils.Extensions;
using Tyrrrz.Extensions;
using NYoutubeDL;

namespace DiscordChatExporter.Core.Exporting
{
    internal class ExportContext
    {
        private readonly MediaDownloader _mediaDownloader;

        public ExportRequest Request { get; }

        public IReadOnlyCollection<Member> Members { get; }

        public IReadOnlyCollection<Channel> Channels { get; }

        public IReadOnlyCollection<Role> Roles { get; }

        public ExportContext(
            ExportRequest request,
            IReadOnlyCollection<Member> members,
            IReadOnlyCollection<Channel> channels,
            IReadOnlyCollection<Role> roles)
        {
            Request = request;
            Members = members;
            Channels = channels;
            Roles = roles;

            // "request" queries the gui options? makes sense
            _mediaDownloader = new MediaDownloader(request.OutputMediaDirPath, request.ShouldReuseMedia);
        }

        public string FormatDate(DateTimeOffset date) => Request.DateFormat switch
        {
            "unix" => date.ToUnixTimeSeconds().ToString(),
            "unixms" => date.ToUnixTimeMilliseconds().ToString(),
            var dateFormat => date.ToLocalString(dateFormat)
        };

        public Member? TryGetMember(Snowflake id) => Members.FirstOrDefault(m => m.Id == id);

        public Channel? TryGetChannel(Snowflake id) => Channels.FirstOrDefault(c => c.Id == id);

        public Role? TryGetRole(Snowflake id) => Roles.FirstOrDefault(r => r.Id == id);

        public Color? TryGetUserColor(Snowflake id)
        {
            var member = TryGetMember(id);
            var roles = member?.RoleIds.Join(Roles, i => i, r => r.Id, (_, role) => role);

            return roles?
                .Where(r => r.Color is not null)
                .OrderByDescending(r => r.Position)
                .Select(r => r.Color)
                .FirstOrDefault();
        }

        public async ValueTask<string> ResolveMediaUrlAsync(string url, string thumbnailUrl = "", bool hasEmbedUrl = false)
        {

            // System.Diagnostics.Debug.WriteLine(url); // Writes one time with txt, does it twice with html. Gets avatar and profile pic with html and dosen't get youtube or vids

            if (!Request.ShouldDownloadMedia)
                return url; // Does nothing if download media option is off?

            try
            {
                Regex checkIfAttachment = new Regex(@"^https://cdn.discordapp.com/attachments");
                if (checkIfAttachment.IsMatch(url) == true) { hasEmbedUrl = false; }
                var filePath = await _mediaDownloader.DownloadAsync(url, thumbnailUrl, hasEmbedUrl);

                // System.Diagnostics.Debug.WriteLine(filePath); // Prints absolute file path to the archive of media. This also downloads youtube thumbnails which is not ideal.

                // We want relative path so that the output files can be copied around without breaking.
                // Base directory path may be null if the file is stored at the root or relative to working directory.
                var relativeFilePath = !string.IsNullOrWhiteSpace(Request.OutputBaseDirPath)
                    ? Path.GetRelativePath(Request.OutputBaseDirPath, filePath)
                    : filePath;

                // I guess he is saying if you wanna change your backup folder it won't break because relative. cool

                // System.Diagnostics.Debug.WriteLine(relativeFilePath); // See difference between absolute and relative

                // HACK: for HTML, we need to format the URL properly
                if (Request.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight)
                {
                    // Need to escape each path segment while keeping the directory separators intact
                    return relativeFilePath
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Select(Uri.EscapeDataString)
                        .JoinToString(Path.AltDirectorySeparatorChar.ToString());
                }

                return relativeFilePath;
            }
            // Try to catch only exceptions related to failed HTTP requests
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/332
            // https://github.com/Tyrrrz/DiscordChatExporter/issues/372
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // TODO: add logging so we can be more liberal with catching exceptions
                // We don't want this to crash the exporting process in case of failure
                return url;
            }
        }
    }
}