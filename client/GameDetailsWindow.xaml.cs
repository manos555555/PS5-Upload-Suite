using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace PS5Upload
{
    public partial class GameDetailsWindow : Window
    {
        private readonly PS5MountedGame _game;
        private readonly PS5Protocol? _protocol;
        private readonly string _paramJsonRaw;
        // Pre-configured HttpClient (properties locked after first use, set once here)
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.Timeout = TimeSpan.FromSeconds(8);
            return client;
        }

        public GameDetailsWindow(PS5MountedGame game, Dictionary<string, string> details, PS5Protocol? protocol = null)
        {
            InitializeComponent();
            _game = game;
            _protocol = protocol;
            _paramJsonRaw = details.TryGetValue("param_json", out var pj) ? pj : "";

            // Header
            if (game.Icon != null)
            {
                GameIconImage.Source = game.Icon;
            }
            GameNameText.Text = details.TryGetValue("name", out var n) ? n : game.Name;
            TitleIdText.Text = game.TitleId;
            RegionText.Text = details.TryGetValue("region", out var r) ? r : game.Region;
            bool isActive = details.TryGetValue("is_active", out var act) && act == "1";
            StatusText.Text = isActive ? "✓ Mounted" : "✗ Not Mounted";
            StatusText.Foreground = isActive
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x35, 0x45));

            // Sizes
            if (details.TryGetValue("total_size", out var ts) && ulong.TryParse(ts, out ulong totalBytes))
            {
                TotalSizeText.Text = FormatSize(totalBytes);
            }
            if (details.TryGetValue("eboot_size", out var es) && ulong.TryParse(es, out ulong ebootBytes))
            {
                EbootSizeText.Text = FormatSize(ebootBytes);
            }

            // Paths
            SourcePathText.Text = details.TryGetValue("path", out var p) ? p : game.Path;
            InstallDateText.Text = details.TryGetValue("install_date", out var d) ? d : "Unknown";

            // param.json
            if (details.TryGetValue("param_json", out var param))
            {
                ParamJsonText.Text = FormatJson(param);
            }
            else
            {
                ParamJsonText.Text = "(not available)";
            }

            // Load cover art async (pic0.png)
            _ = LoadCoverArtAsync();
        }

        private async Task LoadCoverArtAsync()
        {
            if (_protocol == null || !_protocol.IsConnected) return;

            try
            {
                var picBytes = await _protocol.GetGamePicAsync(_game.TitleId, 0);
                if (picBytes == null || picBytes.Length == 0) return;

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = new MemoryStream(picBytes);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        BackgroundCoverImage.Source = bitmap;
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string query = !string.IsNullOrWhiteSpace(_game.Name) && _game.Name != "Unknown"
                    ? _game.Name
                    : _game.TitleId;
                string url = $"https://store.playstation.com/en-gb/search/{Uri.EscapeDataString(query)}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open browser: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void FetchPsnInfoButton_Click(object sender, RoutedEventArgs e)
        {
            FetchPsnInfoButton.IsEnabled = false;
            PsnStatusText.Text = "Fetching from PSN Store...";
            PsnInfoPanel.Visibility = Visibility.Collapsed;

            try
            {
                var info = await FetchPsnInfoAsync(_game.TitleId);
                if (info == null)
                {
                    PsnStatusText.Text = "❌ Not found on PSN Store (game may be pirated/unreleased/region-locked). Try the browser search button below.";
                    return;
                }

                PsnTitleText.Text = info.Title;
                PsnPublisherText.Text = string.IsNullOrEmpty(info.Publisher) ? "" : $"🏢 {info.Publisher}";
                PsnReleaseDateText.Text = string.IsNullOrEmpty(info.ReleaseDate) ? "" : $"📅 Released: {info.ReleaseDate}";
                PsnDescriptionText.Text = info.Description ?? "";

                PsnInfoPanel.Visibility = Visibility.Visible;
                PsnStatusText.Text = $"✓ Found on PSN Store ({info.Source})";
            }
            catch (Exception ex)
            {
                PsnStatusText.Text = $"❌ Error: {ex.Message}";
            }
            finally
            {
                FetchPsnInfoButton.IsEnabled = true;
            }
        }

        private class PsnGameInfo
        {
            public string Title { get; set; } = "";
            public string Publisher { get; set; } = "";
            public string ReleaseDate { get; set; } = "";
            public string Description { get; set; } = "";
            public string Source { get; set; } = "";
        }

        private async Task<PsnGameInfo?> FetchPsnInfoAsync(string titleId)
        {
            // HttpClient is already configured in the static initializer (UserAgent + Timeout)
            
            // Build list of IDs to try: primary title_id first, then alternates from param.json
            var idsToTry = new List<string> { titleId + "_00" };
            
            // Extract alternate IDs from param.json serviceIdForSharing field
            // Format examples: "UP4572-PPSA01768_00", "UP4572-CUSA12650_00", "UP4572-PPSA16388_00-00000000000HWOAD"
            var alternateIds = ExtractAlternateTitleIds(_paramJsonRaw);
            foreach (var altId in alternateIds)
            {
                if (!idsToTry.Contains(altId)) idsToTry.Add(altId);
            }

            // Strategy 1: Chihiro API (titlecontainer) - VERIFIED working (returns full game data)
            string[] chihiroRegions = { "US/en", "GB/en", "DE/de", "FR/fr", "JP/ja", "IT/it", "ES/es" };
            foreach (var tryId in idsToTry)
            {
                foreach (var region in chihiroRegions)
                {
                    try
                    {
                        var parts = region.Split('/');
                        string url = $"https://store.playstation.com/store/api/chihiro/00_09_000/titlecontainer/{parts[0]}/{parts[1]}/999/{tryId}";
                        var response = await _http.GetAsync(url);
                        if (!response.IsSuccessStatusCode) continue;

                        string json = await response.Content.ReadAsStringAsync();
                        if (json.Length < 50) continue;

                        var info = ParsePsnJson(json);
                        if (info != null)
                        {
                            string tag = tryId == titleId + "_00" ? "" : $" via alt ID {tryId}";
                            info.Source = $"Chihiro API ({region}){tag}";
                            return info;
                        }
                    }
                    catch { }
                }
            }

            // Strategy 2: Valkyrie API resolve endpoint (newer, for newer games)
            string[] valkyrieRegions = { "gb/GB", "us/US", "de/DE", "fr/FR", "ja/JP", "it/IT", "es/ES" };
            foreach (var region in valkyrieRegions)
            {
                try
                {
                    string url = $"https://store.playstation.com/valkyrie-api/{region.Split('/')[0]}/{region.Split('/')[1]}/19/resolve/{titleId}_00";
                    var response = await _http.GetAsync(url);
                    if (!response.IsSuccessStatusCode) continue;

                    string json = await response.Content.ReadAsStringAsync();
                    if (json.Length < 50) continue;

                    var info = ParseValkyrieJson(json);
                    if (info != null)
                    {
                        info.Source = $"Valkyrie API ({region})";
                        return info;
                    }
                }
                catch { }
            }

            // Strategy 3: Search by game name on Valkyrie search API (useful for pirated games with real title)
            string gameName = _game.Name;
            if (!string.IsNullOrWhiteSpace(gameName) && gameName != "Unknown")
            {
                try
                {
                    string encoded = Uri.EscapeDataString(gameName);
                    string url = $"https://store.playstation.com/valkyrie-api/en/US/19/tumbler-search/{encoded}?suggested_size=5&mode=game";
                    var response = await _http.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        var info = ParseValkyrieSearchJson(json, gameName);
                        if (info != null)
                        {
                            info.Source = "Valkyrie Search (by name)";
                            return info;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static PsnGameInfo? ParseValkyrieJson(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Valkyrie format: { "included": [ { "attributes": { "name", "long-description", "release-date", ... } } ] }
                if (!root.TryGetProperty("included", out var included)) return null;
                if (included.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

                foreach (var item in included.EnumerateArray())
                {
                    if (!item.TryGetProperty("attributes", out var attrs)) continue;

                    var info = new PsnGameInfo();

                    if (attrs.TryGetProperty("name", out var name))
                        info.Title = name.GetString() ?? "";

                    if (attrs.TryGetProperty("long-description", out var ld))
                        info.Description = StripHtml(ld.GetString() ?? "");
                    else if (attrs.TryGetProperty("description", out var sd))
                        info.Description = StripHtml(sd.GetString() ?? "");

                    if (attrs.TryGetProperty("release-date", out var rd))
                    {
                        string raw = rd.GetString() ?? "";
                        if (DateTime.TryParse(raw, out DateTime dt))
                            info.ReleaseDate = dt.ToString("yyyy-MM-dd");
                        else
                            info.ReleaseDate = raw;
                    }

                    if (attrs.TryGetProperty("provider-name", out var pub))
                        info.Publisher = pub.GetString() ?? "";
                    else if (attrs.TryGetProperty("publisher-name", out var pub2))
                        info.Publisher = pub2.GetString() ?? "";

                    if (!string.IsNullOrWhiteSpace(info.Title))
                        return info;
                }
            }
            catch { }
            return null;
        }

        private static PsnGameInfo? ParseValkyrieSearchJson(string json, string searchName)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("included", out var included)) return null;
                if (included.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

                PsnGameInfo? bestMatch = null;
                foreach (var item in included.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type)) continue;
                    if (type.GetString() != "game") continue;

                    if (!item.TryGetProperty("attributes", out var attrs)) continue;
                    if (!attrs.TryGetProperty("name", out var nameProp)) continue;

                    string name = nameProp.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Find best match by name similarity
                    if (string.Equals(name, searchName, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(searchName, StringComparison.OrdinalIgnoreCase) ||
                        searchName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        var info = new PsnGameInfo { Title = name };

                        if (attrs.TryGetProperty("long-description", out var ld))
                            info.Description = StripHtml(ld.GetString() ?? "");

                        if (attrs.TryGetProperty("release-date", out var rd))
                        {
                            string raw = rd.GetString() ?? "";
                            if (DateTime.TryParse(raw, out DateTime dt))
                                info.ReleaseDate = dt.ToString("yyyy-MM-dd");
                        }

                        if (attrs.TryGetProperty("provider-name", out var pub))
                            info.Publisher = pub.GetString() ?? "";

                        // Exact match wins immediately
                        if (string.Equals(name, searchName, StringComparison.OrdinalIgnoreCase))
                            return info;

                        bestMatch ??= info;
                    }
                }
                return bestMatch;
            }
            catch { }
            return null;
        }

        private static PsnGameInfo? ParsePsnJson(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var info = new PsnGameInfo();

                if (root.TryGetProperty("name", out var name))
                    info.Title = name.GetString() ?? "";
                else if (root.TryGetProperty("title_name", out var tn))
                    info.Title = tn.GetString() ?? "";

                if (root.TryGetProperty("provider_name", out var pub))
                    info.Publisher = pub.GetString() ?? "";
                else if (root.TryGetProperty("publisher_name", out var pub2))
                    info.Publisher = pub2.GetString() ?? "";

                if (root.TryGetProperty("release_date", out var rd))
                {
                    string rawDate = rd.GetString() ?? "";
                    if (DateTime.TryParse(rawDate, out DateTime dt))
                        info.ReleaseDate = dt.ToString("yyyy-MM-dd");
                    else
                        info.ReleaseDate = rawDate;
                }

                if (root.TryGetProperty("long_desc", out var ld))
                    info.Description = StripHtml(ld.GetString() ?? "");
                else if (root.TryGetProperty("short_desc", out var sd))
                    info.Description = StripHtml(sd.GetString() ?? "");

                if (!string.IsNullOrWhiteSpace(info.Title))
                    return info;
            }
            catch { }
            return null;
        }

        // Extract alternate title IDs from param.json serviceIdForSharing field.
        // The payload strips newlines from param.json but keeps structure intact.
        // Examples of entries to match:
        //   "UP4572-PPSA01768_00"
        //   "UP4572-CUSA12650_00"
        //   "UP4572-PPSA16388_00-00000000000HWOAD"  (full contentId with suffix)
        // Returns list of IDs in "PPSAxxxxx_00" or "CUSAxxxxx_00" format ready for API use.
        private static List<string> ExtractAlternateTitleIds(string paramJson)
        {
            var ids = new List<string>();
            if (string.IsNullOrWhiteSpace(paramJson)) return ids;

            // Match publisher-prefix-free IDs: "PPSA12345_00" or "CUSA12345_00"
            // Handles both "UP4572-PPSA01768_00" (short) and "UP4572-PPSA16388_00-SUFFIX" (full contentId)
            var regex = new System.Text.RegularExpressions.Regex(@"(?:[A-Z]{2}\d{4}-)?(PPSA\d{5}_\d{2}|CUSA\d{5}_\d{2})");
            var matches = regex.Matches(paramJson);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (m.Groups.Count >= 2)
                {
                    string id = m.Groups[1].Value;
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
            return ids;
        }

        // Strip HTML tags and decode common entities
        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            // Replace <br>, <br/>, </p> with newlines
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Strip all remaining tags
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", "");
            // Decode common HTML entities
            html = System.Net.WebUtility.HtmlDecode(html);
            // Collapse excessive whitespace but keep newlines
            html = System.Text.RegularExpressions.Regex.Replace(html, @"[ \t]+", " ");
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");
            return html.Trim();
        }

        private static string FormatSize(ulong bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024) return $"{mb:F1} MB ({bytes:N0} bytes)";
            return $"{mb / 1024.0:F2} GB ({bytes:N0} bytes)";
        }

        private static string FormatJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
            // Basic pretty-print: add newlines after { [ , and before } ]
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            bool inString = false;
            foreach (char c in raw)
            {
                if (c == '"') inString = !inString;
                if (!inString)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c);
                        sb.Append('\n');
                        depth++;
                        sb.Append(new string(' ', depth * 2));
                        continue;
                    }
                    if (c == '}' || c == ']')
                    {
                        sb.Append('\n');
                        depth = System.Math.Max(0, depth - 1);
                        sb.Append(new string(' ', depth * 2));
                        sb.Append(c);
                        continue;
                    }
                    if (c == ',')
                    {
                        sb.Append(c);
                        sb.Append('\n');
                        sb.Append(new string(' ', depth * 2));
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
