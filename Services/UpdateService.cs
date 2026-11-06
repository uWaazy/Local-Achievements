using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LocalAchievements.Services
{
    public enum UpdateStatus
    {
        UpToDate,
        Available,
        Error
    }

    public class UpdateInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string Notes { get; set; }
        public string ReleaseUrl { get; set; }
        public string AssetName { get; set; }
        public string AssetUrl { get; set; }
        public string Sha256 { get; set; }
    }

    public class UpdateCheckResult
    {
        public UpdateStatus Status { get; set; }
        public UpdateInfo Info { get; set; }
        public Version Installed { get; set; }
        public string Error { get; set; }
    }

    public class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    public class UpdateService
    {
        private const string Owner = "uWaazy";
        private const string Repository = "Local-Achievements";
        private const long MaxPackageBytes = 25L * 1024 * 1024;
        private const long MaxJsonBytes = 2L * 1024 * 1024;
        private const int RequestTimeoutMs = 15000;
        private const int MaxNotesLength = 1500;

        private readonly string pluginDirectory;
        private readonly Func<string> feedOverride;

        public UpdateService(string pluginDirectory, Func<string> feedOverride)
        {
            this.pluginDirectory = pluginDirectory;
            this.feedOverride = feedOverride;
        }

        private string WorkDirectory => Path.Combine(Path.GetTempPath(), "LocalAchievements_update");

        private static bool localOverrideWarned;

        private string LocalFeedOverride()
        {
            string custom = feedOverride?.Invoke();
            if (string.IsNullOrWhiteSpace(custom)) return null;

            if (Uri.TryCreate(custom.Trim(), UriKind.Absolute, out Uri uri) && uri.IsFile && !uri.IsUnc)
                return uri.AbsoluteUri;

            if (!localOverrideWarned)
            {
                localOverrideWarned = true;
                AdvancedLogger.Log("[UPDATE] UpdateFeedUrl ignored: only local file addresses are accepted.");
            }
            return null;
        }

        private string FeedUrl => LocalFeedOverride() ?? $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var match = Regex.Match(text.Trim(), @"^[vV]?(\d+)\.(\d+)\.(\d+)");
            if (!match.Success) return null;

            return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        }

        private string ReadInstalledYaml(string key)
        {
            string file = Path.Combine(pluginDirectory, "extension.yaml");
            if (!File.Exists(file)) return null;

            var match = Regex.Match(File.ReadAllText(file), @"(?m)^" + key + @":\s*(\S+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        public Version GetInstalledVersion()
        {
            try { return ParseVersion(ReadInstalledYaml("Version")); }
            catch { return null; }
        }

        public bool IsInstalledCopy(string playniteConfigurationPath)
        {
            try
            {
                string extensions = Path.GetFullPath(Path.Combine(playniteConfigurationPath, "Extensions")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string plugin = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return plugin.StartsWith(extensions, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public void CleanupLeftovers()
        {
            try
            {
                foreach (string file in Directory.GetFiles(pluginDirectory, "*.old-*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        public UpdateCheckResult Check()
        {
            var result = new UpdateCheckResult { Installed = GetInstalledVersion() };

            try
            {
                if (result.Installed == null)
                    throw new UpdateException("Could not read the installed version from extension.yaml.");

                string json = DownloadText(FeedUrl);
                if (json == null)
                {
                    result.Status = UpdateStatus.UpToDate;
                    return result;
                }

                var release = JObject.Parse(json);
                if (release.Value<bool?>("draft") == true || release.Value<bool?>("prerelease") == true)
                {
                    result.Status = UpdateStatus.UpToDate;
                    return result;
                }

                string tag = release.Value<string>("tag_name");
                Version latest = ParseVersion(tag);
                if (latest == null)
                    throw new UpdateException("Unrecognized release tag: " + tag);

                if (latest <= result.Installed)
                {
                    result.Status = UpdateStatus.UpToDate;
                    return result;
                }

                string body = release.Value<string>("body") ?? "";
                var info = new UpdateInfo
                {
                    Version = latest,
                    Tag = tag,
                    Notes = BuildNotes(body),
                    ReleaseUrl = release.Value<string>("html_url")
                };

                var asset = (release["assets"] as JArray)?
                    .OfType<JObject>()
                    .FirstOrDefault(a => (a.Value<string>("name") ?? "").EndsWith(".pext", StringComparison.OrdinalIgnoreCase));

                if (asset != null)
                {
                    info.AssetName = asset.Value<string>("name");
                    info.AssetUrl = asset.Value<string>("browser_download_url");
                }

                info.Sha256 = ResolveSha256(body, asset?.Value<string>("digest"));

                result.Info = info;
                result.Status = UpdateStatus.Available;
            }
            catch (Exception ex)
            {
                result.Status = UpdateStatus.Error;
                result.Error = ex.Message;
            }

            return result;
        }

        public static string BuildNotes(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";

            string text = body.Replace("\r\n", "\n");
            int cut = text.Length;
            foreach (string marker in new[] { "### What's Changed", "### Security", "**Full Changelog**" })
            {
                int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && index < cut) cut = index;
            }

            text = text.Substring(0, cut).Trim();
            text = text.Replace("**", "").Replace("`", "");
            text = Regex.Replace(text, @"^###\s*", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            if (text.Length > MaxNotesLength)
            {
                int lastBreak = text.LastIndexOf('\n', MaxNotesLength);
                text = text.Substring(0, lastBreak > 0 ? lastBreak : MaxNotesLength).TrimEnd() + "\n...";
            }

            return text;
        }

        private static string ResolveSha256(string body, string digest)
        {
            string fromBody = null;
            var bodyMatch = Regex.Match(body ?? "", @"SHA-256:\s*`?([0-9a-fA-F]{64})");
            if (bodyMatch.Success) fromBody = bodyMatch.Groups[1].Value.ToLowerInvariant();

            string fromDigest = null;
            var digestMatch = Regex.Match(digest ?? "", @"^sha256:([0-9a-fA-F]{64})$");
            if (digestMatch.Success) fromDigest = digestMatch.Groups[1].Value.ToLowerInvariant();

            if (fromBody != null && fromDigest != null && fromBody != fromDigest) return null;
            return fromBody ?? fromDigest;
        }

        private bool IsAllowedAssetUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;

            if (LocalFeedOverride() != null)
                return uri.IsFile && !uri.IsUnc;

            return uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        private static WebRequest CreateRequest(string url)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }

            var request = WebRequest.Create(new Uri(url));
            request.Timeout = RequestTimeoutMs;

            if (request is HttpWebRequest http)
            {
                http.UserAgent = "LocalAchievements-Updater";
                http.Accept = "application/vnd.github+json, application/octet-stream";
                http.ReadWriteTimeout = 30000;
            }

            return request;
        }

        private static string DownloadText(string url)
        {
            try
            {
                using (var response = CreateRequest(url).GetResponse())
                using (var stream = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    CopyLimited(stream, memory, MaxJsonBytes, null, CancellationToken.None);
                    return Encoding.UTF8.GetString(memory.ToArray());
                }
            }
            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private static void CopyLimited(Stream source, Stream destination, long maxBytes, SHA256 hasher, CancellationToken cancel)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();

                total += read;
                if (total > maxBytes) throw new UpdateException("The download is larger than the allowed limit.");

                hasher?.TransformBlock(buffer, 0, read, null, 0);
                destination.Write(buffer, 0, read);
            }

            hasher?.TransformFinalBlock(new byte[0], 0, 0);
        }

        public string DownloadAndVerify(UpdateInfo info, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(info.AssetUrl))
                throw new UpdateException("The release has no .pext file attached.");
            if (!IsAllowedAssetUrl(info.AssetUrl))
                throw new UpdateException("The download address is not an official release address.");
            if (string.IsNullOrEmpty(info.Sha256))
                throw new UpdateException("The release does not publish a SHA-256 for the file, so it cannot be verified.");

            ResetDirectory(WorkDirectory);
            string packagePath = Path.Combine(WorkDirectory, Path.GetFileName(info.AssetName ?? "update.pext"));

            using (var hasher = SHA256.Create())
            {
                using (var response = CreateRequest(info.AssetUrl).GetResponse())
                {
                    if (response.ContentLength > MaxPackageBytes)
                        throw new UpdateException("The download is larger than the allowed limit.");

                    using (var stream = response.GetResponseStream())
                    using (var file = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        CopyLimited(stream, file, MaxPackageBytes, hasher, cancel);
                    }
                }

                string actual = BitConverter.ToString(hasher.Hash).Replace("-", "").ToLowerInvariant();
                if (actual != info.Sha256.ToLowerInvariant())
                {
                    SafeDelete(packagePath);
                    throw new UpdateException("SHA-256 mismatch: the downloaded file is not the one that was published.");
                }
            }

            ValidatePackage(packagePath, info.Version);
            return packagePath;
        }

        private void ValidatePackage(string packagePath, Version expectedVersion)
        {
            string expectedId = ReadInstalledYaml("Id");
            string module = ReadInstalledYaml("Module") ?? "LocalAchievements.dll";

            try
            {
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    var manifest = archive.Entries.FirstOrDefault(e => e.FullName.Equals("extension.yaml", StringComparison.OrdinalIgnoreCase));
                    if (manifest == null) throw new UpdateException("The package has no extension.yaml.");

                    string yaml;
                    using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8))
                        yaml = reader.ReadToEnd();

                    var id = Regex.Match(yaml, @"(?m)^Id:\s*(\S+)");
                    if (!id.Success || !string.Equals(id.Groups[1].Value, expectedId, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateException("The package belongs to a different extension.");

                    var version = Regex.Match(yaml, @"(?m)^Version:\s*(\S+)");
                    if (!version.Success || ParseVersion(version.Groups[1].Value) != expectedVersion)
                        throw new UpdateException("The package version does not match the release.");

                    if (!archive.Entries.Any(e => e.FullName.Equals(module, StringComparison.OrdinalIgnoreCase)))
                        throw new UpdateException("The package does not contain " + module + ".");
                }
            }
            catch (InvalidDataException)
            {
                throw new UpdateException("The downloaded file is not a valid package.");
            }
        }

        public void Install(string packagePath, UpdateInfo info)
        {
            string stage = Path.Combine(WorkDirectory, "stage");
            string backup = Path.Combine(WorkDirectory, "backup");
            ResetDirectory(stage);
            ResetDirectory(backup);

            ExtractSafely(packagePath, stage);
            CopyDirectory(pluginDirectory, backup);

            var journal = new List<Action>();

            try
            {
                foreach (string file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(stage.Length).TrimStart(Path.DirectorySeparatorChar);
                    string target = Path.Combine(pluginDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    ApplyFile(file, target, Path.Combine(backup, relative), journal);
                }

                if (GetInstalledVersion() != info.Version)
                    throw new UpdateException("extension.yaml was not updated to the new version.");
            }
            catch (Exception ex)
            {
                Rollback(journal);
                if (ex is UpdateException) throw;
                throw new UpdateException(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                SafeDelete(packagePath);
                try { Directory.Delete(stage, true); } catch { }
            }
        }

        private static void ApplyFile(string source, string target, string backupCopy, List<Action> journal)
        {
            if (!File.Exists(target))
            {
                File.Copy(source, target, false);
                journal.Add(() => File.Delete(target));
                return;
            }

            try
            {
                File.Copy(source, target, true);
                journal.Add(() => File.Copy(backupCopy, target, true));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                string renamed = target + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                File.Move(target, renamed);
                journal.Add(() =>
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(renamed, target);
                });
                File.Copy(source, target, false);
            }
        }

        private static void Rollback(List<Action> journal)
        {
            for (int i = journal.Count - 1; i >= 0; i--)
            {
                try { journal[i](); }
                catch (Exception ex) { AdvancedLogger.Log("[UPDATE] Rollback step failed: " + ex.Message); }
            }
        }

        private static void ExtractSafely(string packagePath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateException("The package contains an unsafe path.");

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                if (file.IndexOf(".old-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void ResetDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
            Directory.CreateDirectory(path);
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
