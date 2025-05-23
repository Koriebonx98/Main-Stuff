using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FitGirlStore
{
    public class FitGirlStore : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public override Guid Id { get; } = Guid.Parse("5c415b39-d755-4514-9be5-2701d3de94d4");
        public override string Name => "FitGirl Store";
        private static readonly string baseUrl = "https://fitgirl-repacks.site/all-my-repacks-a-z/?lcp_page0=";
        private static readonly string logFilePath = "Games.log";
        private readonly Timer updateTimer;
        private bool isScannerRunning = false; // Prevent overlapping runs
        private readonly object scannerLock = new object(); // For thread safety

        public FitGirlStore(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties { HasSettings = false };

            // Set up the timer to run every 3 hours (3 * 60 * 60 * 1000 milliseconds)
            updateTimer = new Timer(async _ => await TimerTriggeredUpdate(), null, 0, 3 * 60 * 60 * 1000);

            // Hook into the library update event
            PlayniteApi.Database.Games.ItemUpdated += async (sender, e) =>
            {
                await LibraryEventTriggeredUpdate();
            };
        }

        private async Task<List<GameMetadata>> ScrapeSite()
        {
            var gameEntries = new List<GameMetadata>();
            var uniqueGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Get the latest page number from the site
            int latestPage = await GetLatestPageNumber();

            for (int page = 1; page <= latestPage; page++)
            {
                string url = $"{baseUrl}{page}#lcp_instance_0";
                logger.Info($"Scraping: {url}");

                string pageContent = await LoadPageContent(url);
                var links = ParseLinks(pageContent);

                foreach (var link in links)
                {
                    string href = link.Item1;
                    string text = link.Item2;

                    if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(text) || !IsValidGameLink(href))
                        continue;

                    string siteVersion = ExtractVersionNumber(text); // Get the version from the site
                    string cleanName = CleanGameName(text);

                    if (string.IsNullOrEmpty(cleanName))
                    {
                        cleanName = Regex.Replace(href, @"https://fitgirl-repacks.site/([^/]+)/$", "$1").Replace('-', ' ');
                    }

                    if (!string.IsNullOrEmpty(cleanName) && !href.Contains("page0="))
                    {
                        var gameKey = $"{cleanName}|{siteVersion}";
                        if (uniqueGames.Contains(gameKey))
                            continue;

                        uniqueGames.Add(gameKey);

                        // Check for local version file in Games and Repacks folders
                        string localVersion = GetLocalVersion(cleanName);
                        bool isUpdateReady = false;

                        if (!string.IsNullOrEmpty(localVersion))
                        {
                            // Use your IsNewerVersion method to compare versions
                            if (IsNewerVersion(localVersion, siteVersion))
                            {
                                isUpdateReady = true; // Site version is newer, mark as needing update
                            }
                        }
                        else
                        {
                            // No local version found, use site's version
                            localVersion = siteVersion;
                        }

                        var gameMetadata = new GameMetadata
                        {
                            Name = cleanName,
                            Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("PC (Windows)") },
                            GameActions = new List<GameAction>
                    {
                        new GameAction
                        {
                            Name = "Download: Fitgirl",
                            Type = GameActionType.URL,
                            Path = href,
                            IsPlayAction = false
                        }
                    },
                            Version = localVersion, // Use either local or site version
                            IsInstalled = false
                        };

                        LogGameInfo(cleanName, localVersion);

                        var existingGame = PlayniteApi.Database.Games.FirstOrDefault(g => g.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase));
                        if (existingGame != null)
                        {
                            LogPlayniteGameInfo(existingGame.Name, existingGame.Version);

                            // Add "[Update Ready]" if site version is newer
                            if (isUpdateReady)
                            {
                                AddUpdateReadyFeature(existingGame); // Call the appropriate AddUpdateReadyFeature method
                                LogUpdateReadyAdded();
                            }

                            // If no local version exists, update the existing game's version
                            if (string.IsNullOrEmpty(existingGame.Version))
                            {
                                existingGame.Version = localVersion;
                                PlayniteApi.Database.Games.Update(existingGame);
                            }

                            AddFeatures(existingGame, text);
                        }
                        else
                        {
                            LogPlayniteGameInfo(cleanName, "Not in Playnite");

                            // Only add to game entries if it's not already processed
                            if (!IsDuplicate(gameMetadata))
                            {
                                gameEntries.Add(gameMetadata); // Add new game as a metadata entry
                            }
                        }
                    }
                }
            }

            return gameEntries;
        }




        private string GetLocalVersion(string gameName)
        {
            // Sanitize the game name to remove illegal characters
            string sanitizedGameName = SanitizeGameName(gameName);

            // Check in both "Games" and "Repacks" folders across all drives
            string[] searchFolders = { "Games", "Repacks" };
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network || d.DriveType == DriveType.Removable)))
            {
                foreach (var folderName in searchFolders)
                {
                    string folderPath = Path.Combine(drive.RootDirectory.FullName, folderName, $"{sanitizedGameName} [Repack]");
                    if (Directory.Exists(folderPath))
                    {
                        var versionFiles = Directory.GetFiles(folderPath, "*.txt")
                                                    .Where(file => Regex.IsMatch(Path.GetFileNameWithoutExtension(file), @"^v\d+(\.\d+)*$"));
                        if (versionFiles.Any())
                        {
                            // Use the first version file found
                            return Path.GetFileNameWithoutExtension(versionFiles.First());
                        }
                    }
                }
            }

            return string.Empty;
        }

        private void AddFeatures(Game game, string name)
        {
            var featuresToAdd = new List<string>();

            // Check for various patterns indicating the presence of DLCs
            var dlcPatterns = new string[]
            {
        "+ DLC", "+ dlcs", "+ DLC's", "+ dlc", "+ dlcs", "+ dlc's", "+ DLC'S"
            };

            bool dlcFound = dlcPatterns.Any(pattern => name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

            // Check for pattern "+ x DLC" where x is any number
            var dlcRegex = new Regex(@"\+\s*\d+\s*DLC", RegexOptions.IgnoreCase);
            if (dlcFound || dlcRegex.IsMatch(name))
            {
                featuresToAdd.Add("+ DLC");
            }

            // Check for Update patterns
            var updatePatterns = new string[]
            {
        "+ Update", "+ update", "+ UPDATE"
            };

            bool updateFound = updatePatterns.Any(pattern => name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

            // Check for pattern "+ x Update" or "+ Update x"
            var updateRegex = new Regex(@"\+\s*(\d+\s*Update|Update\s*\d+)", RegexOptions.IgnoreCase);
            if (updateFound || updateRegex.IsMatch(name))
            {
                featuresToAdd.Add("+ Update");
            }

            // Check for Fix patterns
            var fixPatterns = new string[]
            {
        "+ Fix", "+ fix", "+ FIX", "+ HotFix", "+ hotfix", "+ HOTFIX", "+ Windows 7 Fix", "+ windows 7 fix", "+ WINDOWS 7 FIX",
        "+ Controller Fix", "+ controller fix", "+ CONTROLLER FIX", "+ CrashFix", "+ crashfix", "+ CRASHFIX", "+ Videos Fix"
            };

            bool fixFound = fixPatterns.Any(pattern => name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

            // Check for pattern "+ HotFix #x" or similar
            var fixRegex = new Regex(@"\+\s*(HotFix\s*#?\d+|CrashFix|Controller Fix|Windows 7 Fix)", RegexOptions.IgnoreCase);
            if (fixFound || fixRegex.IsMatch(name))
            {
                featuresToAdd.Add("+ Fix");
            }

            // Check for Soundtrack patterns
            var soundtrackPatterns = new string[]
            {
        "+ Bonus Soundtrack", "+ Bonus OST", "+ Original Soundtrack Bundle", "- Game & Soundtrack Bundle",
        "+ Soundtrack Edition", "+ Soundtrack Bundle", "- Game & OST"
            };

            bool soundtrackFound = soundtrackPatterns.Any(pattern => name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
            if (soundtrackFound)
            {
                featuresToAdd.Add("+ Soundtrack");
            }

            // Check for Online patterns
            var onlinePatterns = new string[]
            {
        "+ Online", "- Online", "+ Online Multiplayer", "+ Online CO-OP"
            };

            bool onlineFound = onlinePatterns.Any(pattern => name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
            if (onlineFound)
            {
                featuresToAdd.Add("+ Online");
                if (name.IndexOf("+ Online Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    featuresToAdd.Add("+ Multiplayer");
                }
            }

            // Check for Local/Online Multiplayer
            if (name.IndexOf("+ Local/Online Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                featuresToAdd.Add("+ Local Multiplayer");
                featuresToAdd.Add("+ Online");
            }

            if (name.IndexOf("+ Multiplayer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                featuresToAdd.Add("+ Multiplayer");
            }

            // Ensure the feature exists in the Playnite library and add it to the game
            foreach (var featureName in featuresToAdd)
            {
                var feature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals(featureName, StringComparison.OrdinalIgnoreCase));
                if (feature == null)
                {
                    feature = new GameFeature(featureName);
                    PlayniteApi.Database.Features.Add(feature);
                }

                if (game.FeatureIds == null)
                {
                    game.FeatureIds = new List<Guid>();
                }

                if (!game.FeatureIds.Contains(feature.Id))
                {
                    game.FeatureIds.Add(feature.Id);
                    PlayniteApi.Database.Games.Update(game);
                }
            }
        }



        private bool IsDuplicate(GameMetadata gameMetadata)
        {
            // Use the original name for comparison
            return PlayniteApi.Database.Games.Any(existingGame => existingGame.PluginId == Id && existingGame.Name.Equals(gameMetadata.Name, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsNewerVersion(string existingVersion, string newVersion)
        {
            if (string.IsNullOrEmpty(existingVersion) || string.IsNullOrEmpty(newVersion))
                return false;

            var existingVersionParts = existingVersion.Split(new[] { ' ', 'v', 'V', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var newVersionParts = newVersion.Split(new[] { ' ', 'v', 'V', '.' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < Math.Min(existingVersionParts.Length, newVersionParts.Length); i++)
            {
                if (int.TryParse(existingVersionParts[i], out int existingVersionPart) && int.TryParse(newVersionParts[i], out int newVersionPart))
                {
                    if (newVersionPart > existingVersionPart)
                        return true;
                    if (newVersionPart < existingVersionPart)
                        return false;
                }
            }

            return newVersionParts.Length > existingVersionParts.Length;
        }

        private void AddUpdateReadyFeature(Game existingGame)
        {
            var updateReadyFeature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals("[Update Ready]", StringComparison.OrdinalIgnoreCase));
            if (updateReadyFeature == null)
            {
                updateReadyFeature = new GameFeature("[Update Ready]");
                PlayniteApi.Database.Features.Add(updateReadyFeature);
                PlayniteApi.Database.Features.Update(updateReadyFeature);
            }

            if (existingGame.FeatureIds == null)
            {
                existingGame.FeatureIds = new List<Guid>();
            }

            if (!existingGame.FeatureIds.Contains(updateReadyFeature.Id))
            {
                existingGame.FeatureIds.Add(updateReadyFeature.Id);
                PlayniteApi.Database.Games.Update(existingGame);
                LogUpdateReadyAdded();
            }
        }

        

        private void LogGameInfo(string gameName, string version)
        {
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine($"Name on Site: {gameName}");
                writer.WriteLine($"Version: {version}");
            }
        }

        private void LogPlayniteGameInfo(string gameName, string version)
        {
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine($"Playnite Name: {gameName}");
                writer.WriteLine($"Version: {version}");
                writer.WriteLine();
            }
        }

        private void LogUpdateReadyAdded()
        {
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine("[Update Ready] Added");
                writer.WriteLine();
            }
        }

        private void AddInstallReadyFeature(Game existingGame)
        {
            var installReadyFeature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals("[Install Ready]", StringComparison.OrdinalIgnoreCase));
            if (installReadyFeature == null)
            {
                installReadyFeature = new GameFeature("[Install Ready]");
                PlayniteApi.Database.Features.Add(installReadyFeature);
                PlayniteApi.Database.Features.Update(installReadyFeature);
            }

            if (existingGame.FeatureIds == null)
            {
                existingGame.FeatureIds = new List<Guid>();
            }

            if (!existingGame.FeatureIds.Contains(installReadyFeature.Id))
            {
                existingGame.FeatureIds.Add(installReadyFeature.Id);
                PlayniteApi.Database.Games.Update(existingGame);
                LogInstallReadyAdded();
            }
        }

        private void AddInstallReadyFeature(GameMetadata newGame)
        {
            var installReadyFeature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals("[Install Ready]", StringComparison.OrdinalIgnoreCase));
            if (installReadyFeature == null)
            {
                installReadyFeature = new GameFeature("[Install Ready]");
                PlayniteApi.Database.Features.Add(installReadyFeature);
                PlayniteApi.Database.Features.Update(installReadyFeature);
            }

            if (newGame.Features == null)
            {
                newGame.Features = new HashSet<MetadataProperty>();
            }

            // Check if the feature is already added using the feature ID
            bool featureExists = newGame.Features.OfType<MetadataSpecProperty>().Any(f => f.Id == installReadyFeature.Id.ToString());

            if (!featureExists)
            {
                newGame.Features.Add(new MetadataSpecProperty(installReadyFeature.Id.ToString()));
            }
        }

        private void LogInstallReadyAdded()
        {
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine("[Install Ready] Added");
                writer.WriteLine();
            }
        }

        private async Task<int> GetLatestPageNumber()
        {
            string homePageContent = await LoadPageContent("https://fitgirl-repacks.site/all-my-repacks-a-z/");
            var paginationLinks = ParseLinks(homePageContent);
            int latestPage = 1;

            foreach (var link in paginationLinks)
            {
                var match = Regex.Match(link.Item1, @"\?lcp_page0=(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pageNumber) && pageNumber > latestPage)
                {
                    latestPage = pageNumber;
                }
            }

            return latestPage;
        }

        private async Task<string> LoadPageContent(string url)
        {
            using (var httpClient = new HttpClient())
            {
                try
                {
                    return await httpClient.GetStringAsync(url);
                }
                catch (HttpRequestException e)
                {
                    logger.Error(e, $"Error while loading page content from {url}");
                    throw;
                }
            }
        }

        private List<Tuple<string, string>> ParseLinks(string pageContent)
        {
            var links = new List<Tuple<string, string>>();
            var matches = Regex.Matches(pageContent, @"<a\s+(?:[^>]*?\s+)?href=[""'](.*?)[""'].*?>(.*?)</a>");
            foreach (Match match in matches)
            {
                string href = match.Groups[1].Value;
                string text = Regex.Replace(match.Groups[2].Value, "<.*?>", string.Empty); // Remove HTML tags
                links.Add(new Tuple<string, string>(href, text));
            }
            return links;
        }

        private string CleanGameName(string name)
        {
            // Remove version numbers and unwanted characters
            var cleanName = Regex.Replace(name, @"\s*v[\d\.]+.*", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*Build \d+.*", "", RegexOptions.IgnoreCase);
            cleanName = cleanName.Replace("&#8217;", "'"); // Fix the apostrophe character
            cleanName = cleanName.Replace("&#8211;", "-");
            cleanName = cleanName.Replace("&#8216;", "‘");
            cleanName = cleanName.Replace("&#038;", "&"); // Fix the ampersand character
            cleanName = cleanName.Replace("&#8220;", "\""); // Fix the opening quotation mark
            cleanName = cleanName.Replace("&#8221;", "\""); // Fix the closing quotation mark

            // Remove specific phrases and patterns
            var phrasesToRemove = new string[]
            {
        "Windows 7 Fix", "Bonus Soundtrack", "Bonus OST", "Ultimate Fishing Bundle",
        "Digital Deluxe Edition", "Bonus Content", "Bonus","Bonuses", "Soundtrack",
        "All DLCs","All DLC","OST Bundle", "HotFix", "HotFix 1", "Multiplayer","All previous DLCs", "DLCs", "+ DLCs","DLC",
        "+ DLC", "+ Update", "+ Fix", "+ Soundtrack", "+ Online",
        "+ Local Multiplayer", "- Online", "+ Online Multiplayer", "+ Online CO-OP",
        "+ Local/Online Multiplayer", "+ Update", "+ DLC + Multiplayer", "- Game & Soundtrack Bundle", "+ CrackFix", "+ 36 DLCs"
            };

            foreach (var phrase in phrasesToRemove)
            {
                cleanName = Regex.Replace(cleanName, $@"(\s*\+\s*|\s*-\s*){Regex.Escape(phrase)}\s*\d*", "", RegexOptions.IgnoreCase);
            }

            // Remove patterns like "+ x DLC", "+ x Update"
            cleanName = Regex.Replace(cleanName, @"\+\s*\d+\s*DLC", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\+\s*\d+\s*Update", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\+\s*\d+\s*Fix", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\+\s*\d+\s*Soundtrack", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\+\s*\d+\s*Online", "", RegexOptions.IgnoreCase);

            // Remove text in parentheses or square brackets
            cleanName = Regex.Replace(cleanName, @"[\[\(].*?[\]\)]", "").Trim();

            // Trim and remove trailing hyphens or other unwanted characters
            cleanName = cleanName.Trim(' ', '-', '–').TrimEnd(',');

            return cleanName;
        }

        private bool IsValidGameLink(string href)
        {
            var nonGameUrls = new List<string>
    {
        "https://fitgirl-repacks.site/",
        "about:blank#search-container",
        "about:blank#content",
        "https://fitgirl-repacks.site/pop-repacks/",
        "https://fitgirl-repacks.site/popular-repacks/",
        "https://fitgirl-repacks.site/popular-repacks-of-the-year/",
        "https://fitgirl-repacks.site/all-playstation-3-emulated-repacks-a-z/",
        "https://fitgirl-repacks.site/all-switch-emulated-repacks-a-z/",
        "https://fitgirl-repacks.site/category/updates-digest/",
        "https://fitgirl-repacks.site/feed/",
        "http://fitgirl-repacks.site/feed/",
        "https://fitgirl-repacks.site/donations/",
        "http://fitgirl-repacks.site/donations/",
        "https://fitgirl-repacks.site/faq/",
        "https://fitgirl-repacks.site/contacts/",
        "https://fitgirl-repacks.site/repacks-troubleshooting/",
        "https://fitgirl-repacks.site/updates-list/",
        "https://fitgirl-repacks.site/all-my-repacks-a-z/",
        "https://fitgirl-repacks.site/games-with-my-personal-pink-paw-award/",
        "https://wordpress.org/",
        "https://fitgirl-repacks.site/all-my-repacks-a-z/#comment",
        "http://www.hairstylesvip.com"
    };

            if (Regex.IsMatch(href, @"^https://fitgirl-repacks.site/\d{4}/\d{2}/$") ||
                Regex.IsMatch(href, @"^https://fitgirl-repacks.site/all-my-repacks-a-z/\?lcp_page0=\d+#lcp_instance_0$") ||
                href.Contains("#comment-") ||
                href.Contains("https://www.hairstylesvip.com/") ||
                nonGameUrls.Contains(href))
            {
                return false;
            }

            return true;
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();
            var uniqueGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exclusions = LoadExclusions();
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network || d.DriveType == DriveType.Removable));

            // Step 1: Check existing installed games and update InstallDirectory if needed, else mark as uninstalled
            foreach (var existingGame in PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList())
            {
                bool isInstalled = false;

                foreach (var drive in drives)
                {
                    var gamesFolderPath = Path.Combine(drive.RootDirectory.FullName, "Games");
                    if (Directory.Exists(gamesFolderPath))
                    {
                        foreach (var folder in Directory.GetDirectories(gamesFolderPath))
                        {
                            var folderName = ConvertHyphenToColon(CleanGameName(SanitizePath(Path.GetFileName(folder))));
                            if (folderName.Equals(existingGame.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                isInstalled = true;
                                existingGame.InstallDirectory = folder;

                                var exeFiles = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories)
                                                        .Where(exe => !exclusions.Contains(Path.GetFileName(exe)) &&
                                                                      !Path.GetFileName(exe).ToLower().Contains("setup") &&
                                                                      !Path.GetFileName(exe).ToLower().Contains("unins"));

                                foreach (var exe in exeFiles)
                                {
                                    if (!existingGame.GameActions.Any(action => action.Path.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        existingGame.GameActions.Add(new GameAction()
                                        {
                                            Type = GameActionType.File,
                                            Path = exe,
                                            Name = Path.GetFileNameWithoutExtension(exe),
                                            IsPlayAction = true,
                                            WorkingDir = folder
                                        });
                                    }
                                }

                                // Preserve "Fitgirl Download" action
                                var fitgirlAction = existingGame.GameActions.FirstOrDefault(action => action.Name == "Download: Fitgirl");
                                if (fitgirlAction != null && !existingGame.GameActions.Contains(fitgirlAction))
                                {
                                    existingGame.GameActions.Add(fitgirlAction);
                                }

                                var versionFiles = Directory.GetFiles(folder, "*.txt")
                                                            .Where(file => Regex.IsMatch(Path.GetFileNameWithoutExtension(file), @"^v\d+(\.\d+)*$"));
                                if (versionFiles.Any())
                                {
                                    var localVersion = Path.GetFileNameWithoutExtension(versionFiles.First());
                                    existingGame.Version = localVersion;
                                }

                                existingGame.IsInstalled = true;
                                API.Instance.Database.Games.Update(existingGame);
                                uniqueGames.Add(existingGame.Name);
                                break;
                            }
                        }
                    }
                }

                if (!isInstalled)
                {
                    existingGame.IsInstalled = false;
                    existingGame.InstallDirectory = string.Empty;
                    API.Instance.Database.Games.Update(existingGame);
                }
            }

            // Step 2: Scrape the site and add new games, skip if already exist
            List<GameMetadata> scrapedGames = new List<GameMetadata>();
            try
            {
                scrapedGames = ScrapeSite().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to scrape site. Skipping scraping step.");
            }

            foreach (var game in scrapedGames)
            {
                var cleanGameName = ConvertHyphenToColon(SanitizePath(CleanGameName(game.Name)));
                if (uniqueGames.Contains(cleanGameName) || PlayniteApi.Database.Games.Any(g => g.Name.Equals(cleanGameName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var gameMetadata = new GameMetadata()
                {
                    Name = cleanGameName,
                    GameId = cleanGameName.ToLower(),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("PC (Windows)") },
                    GameActions = new List<GameAction>
            {
                new GameAction()
                {
                    Name = "Download: Fitgirl",
                    Type = GameActionType.URL,
                    Path = game.GameActions.FirstOrDefault()?.Path,
                    IsPlayAction = false
                }
            },
                    IsInstalled = false,
                    InstallDirectory = null,
                    Icon = new MetadataFile(Path.Combine(cleanGameName, "icon.png")),
                    BackgroundImage = new MetadataFile(Path.Combine(cleanGameName, "background.png")),
                    Version = game.Version
                };

                games.Add(gameMetadata);
                uniqueGames.Add(gameMetadata.Name);
            }

            // Step 3: Check "Games" folder, add new games, or update existing ones without deleting the Fitgirl Download action
            foreach (var drive in drives)
            {
                var gamesFolderPath = Path.Combine(drive.RootDirectory.FullName, "Games");
                if (Directory.Exists(gamesFolderPath))
                {
                    foreach (var folder in Directory.GetDirectories(gamesFolderPath))
                    {
                        var folderName = Path.GetFileName(folder);
                        var gameName = ConvertHyphenToColon(CleanGameName(SanitizePath(folderName)));
                        var existingGame = PlayniteApi.Database.Games.FirstOrDefault(g => g.PluginId == Id && g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase));
                        var versionFiles = Directory.GetFiles(folder, "*.txt")
                                                    .Where(file => Regex.IsMatch(Path.GetFileNameWithoutExtension(file), @"^v\d+(\.\d+)*$"));

                        if (existingGame != null)
                        {
                            // Update existing game
                            existingGame.IsInstalled = true;
                            existingGame.InstallDirectory = folder;

                            var exeFiles = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories)
                                                    .Where(exe => !exclusions.Contains(Path.GetFileName(exe)) &&
                                                                  !Path.GetFileName(exe).ToLower().Contains("setup") &&
                                                                  !Path.GetFileName(exe).ToLower().Contains("unins"));

                            foreach (var exe in exeFiles)
                            {
                                if (!existingGame.GameActions.Any(action => action.Path.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                                {
                                    existingGame.GameActions.Add(new GameAction()
                                    {
                                        Type = GameActionType.File,
                                        Path = exe,
                                        Name = Path.GetFileNameWithoutExtension(exe),
                                        IsPlayAction = true,
                                        WorkingDir = folder
                                    });
                                }
                            }

                            // Preserve "Fitgirl Download" action
                            var fitgirlAction = existingGame.GameActions.FirstOrDefault(action => action.Name == "Download: Fitgirl");
                            if (fitgirlAction != null && !existingGame.GameActions.Contains(fitgirlAction))
                            {
                                existingGame.GameActions.Add(fitgirlAction);
                            }

                            if (versionFiles.Any())
                            {
                                var localVersion = Path.GetFileNameWithoutExtension(versionFiles.First());
                                existingGame.Version = localVersion;
                            }

                            API.Instance.Database.Games.Update(existingGame);
                            uniqueGames.Add(existingGame.Name);
                        }
                        else
                        {
                            // Add as new game if it doesn't exist
                            var exeFiles = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories)
                                                    .Where(exe => !exclusions.Contains(Path.GetFileName(exe)) &&
                                                                  !Path.GetFileName(exe).ToLower().Contains("setup") &&
                                                                  !Path.GetFileName(exe).ToLower().Contains("unins"));

                            if (!exeFiles.Any())
                                continue;

                            var gameMetadata = new GameMetadata()
                            {
                                Name = gameName,
                                GameId = gameName.ToLower(),
                                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("PC (Windows)") },
                                GameActions = new List<GameAction>(),
                                IsInstalled = true,
                                InstallDirectory = folder,
                                Icon = new MetadataFile(Path.Combine(folder, "icon.png")),
                                BackgroundImage = new MetadataFile(Path.Combine(folder, "background.png")),
                                Version = ExtractVersionNumber(folderName)
                            };

                            if (versionFiles.Any())
                            {
                                var localVersion = Path.GetFileNameWithoutExtension(versionFiles.First());
                                gameMetadata.Version = localVersion;
                            }

                            foreach (var exe in exeFiles)
                            {
                                gameMetadata.GameActions.Add(new GameAction()
                                {
                                    Type = GameActionType.File,
                                    Path = exe,
                                    Name = Path.GetFileNameWithoutExtension(exe),
                                    IsPlayAction = true,
                                    WorkingDir = folder
                                });
                            }


                            games.Add(gameMetadata);
                            uniqueGames.Add(gameMetadata.Name);
                        }
                    }
                }
            }

            // Step 4: Check "Repacks" folder, add new games as uninstalled, and add "[Install Ready]" feature
            foreach (var drive in drives)
            {
                var repacksFolderPath = Path.Combine(drive.RootDirectory.FullName, "Repacks");
                if (Directory.Exists(repacksFolderPath))
                {
                    foreach (var folder in Directory.GetDirectories(repacksFolderPath))
                    {
                        var folderName = Path.GetFileName(folder);
                        var gameName = ConvertHyphenToColon(CleanGameName(SanitizePath(folderName)));
                        var existingGame = PlayniteApi.Database.Games.FirstOrDefault(g => g.PluginId == Id && g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase));

                        if (existingGame != null)
                        {
                            // Existing game: only add "[Install Ready]" feature, don't change InstallDirectory or IsInstalled
                            AddInstallReadyFeature(existingGame);
                            PlayniteApi.Database.Games.Update(existingGame);
                        }
                        else
                        {
                            // New game: add as uninstalled and add "[Install Ready]" feature
                            var gameMetadata = new GameMetadata
                            {
                                Name = gameName,
                                GameId = gameName.ToLower(),
                                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("PC (Windows)") },
                                GameActions = new List<GameAction>(),
                                IsInstalled = false,
                                InstallDirectory = null,
                                Icon = new MetadataFile(Path.Combine(folder, "icon.png")),
                                BackgroundImage = new MetadataFile(Path.Combine(folder, "background.png")),
                                Version = ExtractVersionNumber(folderName)
                            };

                            // Add "[Install Ready]" feature to the new game
                            AddInstallReadyFeature(gameMetadata);

                            games.Add(gameMetadata);
                        }
                    }
                }
            }



            return games;
        }

        private string ExtractVersionNumber(string name)
        {
            var versionPattern = @"(v[\d\.]+(?:\s*\(.*?\))?|Build \d+)";
            var match = Regex.Match(name, versionPattern);
            return match.Success ? match.Value : string.Empty;
        }

        private string ConvertHyphenToColon(string name)
        {
            var parts = name.Split(new[] { " - " }, 2, StringSplitOptions.None);
            if (parts.Length > 1)
            {
                return parts[0] + ": " + parts[1];
            }
            return name;
        }

        private string GetRelativePath(string fromPath, string toPath)
        {
            Uri fromUri = new Uri(fromPath);
            Uri toUri = new Uri(toPath);

            if (fromUri.Scheme != toUri.Scheme)
            {
                return toPath;
            }

            Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (toUri.Scheme.Equals("file", StringComparison.InvariantCultureIgnoreCase))
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }

        private List<string> LoadExclusions()
        {
            var exclusionsFilePath = Path.Combine(GetPluginUserDataPath(), "Exclusions.txt");
            if (!File.Exists(exclusionsFilePath))
            {
                File.WriteAllText(exclusionsFilePath, string.Empty);
            }

            return File.ReadAllLines(exclusionsFilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim('\"').Trim())
                .ToList();
        }

        private string SanitizePath(string path)
        {
            // Replace colons with hyphens for filesystem compatibility
            path = path.Replace(":", " -");

            // Remove any other invalid characters
            return Regex.Replace(path, @"[<>:""/\\|?*]", string.Empty);



        }

        private string SanitizeGameName(string gameName)
        {
            // Remove or replace illegal characters
            return string.Concat(gameName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return new LocalInstallController(args.Game, this);
            }
        }

        public static class HtmlUtility
        {
            private static readonly Dictionary<string, string> htmlEntities = new Dictionary<string, string>
    {
        { "&#038;", "&" },
        { "&amp;", "&" },
        { "&#39;", "'" },
        { "&quot;", "\"" },
        { "&lt;", "<" },
        { "&gt;", ">" }
        // Add more entities as needed
    };

            public static string HtmlDecode(string input)
            {
                foreach (var entity in htmlEntities)
                {
                    input = input.Replace(entity.Key, entity.Value);
                }
                return input;
            }
        }

        private async Task GameInstaller(Game game)
        {
            var userDataPath = GetPluginUserDataPath();
            var fdmPath = Path.Combine(userDataPath, "Free Download Manager", "fdm.exe");
            var logFilePath = Path.Combine(userDataPath, "install_log.log");

            if (!File.Exists(fdmPath))
            {
                API.Instance.Dialogs.ShowErrorMessage($"fdm.exe not found at {fdmPath}. Installation cancelled.", "Error");
                UpdateGameInstallationStatus(game, false);
                return;
            }

            bool isUpdateReady = game.FeatureIds != null && game.FeatureIds.Any(f => PlayniteApi.Database.Features.Get(f).Name.Equals("[Update Ready]", StringComparison.OrdinalIgnoreCase));
            string repackFolder = FindRepackFolder(game.Name);

            var downloadAction = game.GameActions.FirstOrDefault(action => action.Name == "Download: Fitgirl" && action.Type == GameActionType.URL);
            var gameDownloadUrl = downloadAction?.Path;

            if (string.IsNullOrEmpty(gameDownloadUrl))
            {
                // If no URL action, attempt to locate repack in "Repacks" folder and run setup.exe
                var repacksFolder = Path.Combine(userDataPath, "Repacks");

                if (Directory.Exists(repacksFolder))
                {
                    repackFolder = Directory.GetDirectories(repacksFolder, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(d => Path.GetFileName(d).Equals(game.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (string.IsNullOrEmpty(repackFolder))
                {
                    API.Instance.Dialogs.ShowErrorMessage($"Repack for {game.Name} not found in the 'Repacks' folder. Installation cancelled.", "Error");
                    UpdateGameInstallationStatus(game, false);
                    return;
                }

                // Run setup.exe directly from the located repack folder
                var setupExe = Directory.GetFiles(repackFolder, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrEmpty(setupExe))
                {
                    API.Instance.Dialogs.ShowErrorMessage("setup.exe not found. Installation cancelled.", "Error");
                    UpdateGameInstallationStatus(game, false);
                    return;
                }

                try
                {
                    await Task.Run(() =>
                    {
                        using (var process = new Process())
                        {
                            process.StartInfo.FileName = setupExe;
                            process.StartInfo.WorkingDirectory = repackFolder;
                            process.StartInfo.UseShellExecute = true;
                            process.Start();
                            process.WaitForExit();
                        }
                    });

                    game.InstallDirectory = repackFolder;
                    API.Instance.Database.Games.Update(game);

                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Error while running setup.exe");
                    API.Instance.Dialogs.ShowErrorMessage("An error occurred while running setup.exe. Installation cancelled.", "Error");
                    UpdateGameInstallationStatus(game, false);
                }
                return; // End processing here since the repack was handled directly
            }

            // Existing logic for magnet link download and "[Update Ready]" feature handling
            var magnetLink = ScrapeMagnetLink(gameDownloadUrl);
            if (string.IsNullOrEmpty(magnetLink))
            {
                API.Instance.Dialogs.ShowErrorMessage("Magnet link not found. Download cancelled.", "Error");
                UpdateGameInstallationStatus(game, false);
                return;
            }

            magnetLink = HtmlUtility.HtmlDecode(magnetLink);
            LogMagnetLink(logFilePath, magnetLink);

            if (isUpdateReady)
            {
                await StartFdmWithMagnetLink(fdmPath, magnetLink);

                if (IsDownloadIncomplete(repackFolder))
                {
                    API.Instance.Dialogs.ShowErrorMessage("Download incomplete. Installation cancelled.", "Error");
                    UpdateGameInstallationStatus(game, false);
                    return;
                }

                string latestVersion = await GetLatestVersionFromSite(gameDownloadUrl);
                var existingVersionFilePath = Directory.GetFiles(repackFolder, "*.txt").FirstOrDefault();
                if (existingVersionFilePath != null)
                {
                    File.Delete(existingVersionFilePath);
                }

                var versionFilePath = Path.Combine(repackFolder, $"{latestVersion}.txt");
                File.WriteAllText(versionFilePath, latestVersion);

                var updateReadyFeature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals("[Update Ready]", StringComparison.OrdinalIgnoreCase));
                if (updateReadyFeature != null && game.FeatureIds.Contains(updateReadyFeature.Id))
                {
                    game.FeatureIds.Remove(updateReadyFeature.Id);
                    API.Instance.Database.Games.Update(game);
                }

                game.Version = latestVersion;
                API.Instance.Database.Games.Update(game);
            }
            else if (!isUpdateReady && (string.IsNullOrEmpty(repackFolder) || IsDownloadIncomplete(repackFolder)))
            {
                await StartFdmWithMagnetLink(fdmPath, magnetLink);

                repackFolder = FindRepackFolder(game.Name);
                if (IsDownloadIncomplete(repackFolder))
                {
                    API.Instance.Dialogs.ShowErrorMessage("Download incomplete. Installation cancelled.", "Error");
                    UpdateGameInstallationStatus(game, false);
                    return;
                }
            }

            var setupExeFinal = Directory.GetFiles(repackFolder, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(setupExeFinal))
            {
                API.Instance.Dialogs.ShowErrorMessage("setup.exe not found. Installation cancelled.", "Error");
                UpdateGameInstallationStatus(game, false);
                return;
            }

            game.InstallDirectory = repackFolder;
            API.Instance.Database.Games.Update(game);

            try
            {
                await Task.Run(() =>
                {
                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = setupExeFinal;
                        process.StartInfo.WorkingDirectory = repackFolder;
                        process.StartInfo.UseShellExecute = true;
                        process.Start();
                        process.WaitForExit();
                    }

                    var rootDrive = Path.GetPathRoot(repackFolder);
                    var gamesFolderPath = Path.Combine(rootDrive, "Games");
                    if (Directory.Exists(gamesFolderPath))
                    {
                        var installedGameDir = Directory.GetDirectories(gamesFolderPath, "*", SearchOption.AllDirectories)
                            .FirstOrDefault(d => Path.GetFileName(d).Equals(game.Name, StringComparison.OrdinalIgnoreCase));

                        if (!string.IsNullOrEmpty(installedGameDir))
                        {
                            game.InstallDirectory = installedGameDir;
                            API.Instance.Database.Games.Update(game);

                            game = API.Instance.Database.Games.Get(game.Id);
                        }
                    }

                    UpdateGameActionsAndStatus(game, userDataPath);
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error while running setup.exe");
                API.Instance.Dialogs.ShowErrorMessage("An error occurred while running setup.exe. Installation cancelled.", "Error");
                UpdateGameInstallationStatus(game, false);
            }
        }

        private async Task<string> GetLatestVersionFromSite(string gameDownloadUrl)
        {
            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.GetStringAsync(gameDownloadUrl);
                var versionMatch = Regex.Match(response, @"v\d+(\.\d+)*");
                return versionMatch.Success ? versionMatch.Value : string.Empty;
            }

        }

        private bool IsDownloadIncomplete(string repackFolder)
        {
            var mainFilesIncomplete = Directory.GetFiles(repackFolder, "*.fdmdownload", SearchOption.TopDirectoryOnly).Any();
            var unwantedFolder = Path.Combine(repackFolder, "unwanted");
            return mainFilesIncomplete || (Directory.Exists(unwantedFolder) && Directory.GetFiles(unwantedFolder, "*.fdmdownload", SearchOption.AllDirectories).Any());
        }

        private string FindRepackFolder(string gameName)
        {
            // Convert colons to hyphens in the game name to match the repack folder format
            string convertedGameName = gameName.Replace(":", " -");

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network))
            {
                var repacksFolder = Path.Combine(drive.RootDirectory.FullName, "Repacks");
                if (Directory.Exists(repacksFolder))
                {
                    var potentialRepackFolders = Directory.GetDirectories(repacksFolder, "*", SearchOption.TopDirectoryOnly);
                    foreach (var potentialRepackFolder in potentialRepackFolders)
                    {
                        var folderName = Path.GetFileName(potentialRepackFolder);
                        var normalizedFolderName = NormalizeName(folderName);
                        var normalizedGameName = NormalizeName(convertedGameName);

                        // Log the folder and game names being compared
                        logger.Info($"Comparing normalized folder name '{normalizedFolderName}' with normalized game name '{normalizedGameName}'");

                        if (string.Equals(normalizedFolderName, normalizedGameName, StringComparison.OrdinalIgnoreCase))
                        {
                            return potentialRepackFolder;
                        }
                    }
                }
            }
            return null;
        }

        private string NormalizeName(string name)
        {
            var normalized = Regex.Replace(name, @"\[.*?\]", "").Trim();
            normalized = Regex.Replace(normalized, @"\(.+?\)", "").Trim();
            normalized = Regex.Replace(normalized, @"[^\w\s-]", "").Trim(); // Allow hyphens
            return normalized;
        }

        private async Task StartFdmWithMagnetLink(string fdmPath, string magnetLink)
        {
            await Task.Run(() =>
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = fdmPath;
                    process.StartInfo.Arguments = magnetLink;
                    process.StartInfo.UseShellExecute = true;
                    process.Start();
                    process.WaitForExit();
                }
            });
        }

        private string ScrapeMagnetLink(string gameDownloadUrl)
        {
            using (var httpClient = new HttpClient())
            {
                var response = httpClient.GetStringAsync(gameDownloadUrl).Result;
                var regex = new Regex(@"magnet:\?xt=urn:btih:[a-zA-Z0-9]+[^\""]*");
                var match = regex.Match(response);
                if (match.Success)
                {
                    var magnetLink = match.Value;
                    Console.WriteLine($"Magnet link found: {magnetLink}");
                    return magnetLink;
                }
                else
                {
                    Console.WriteLine("No magnet link found.");
                }
            }
            return null;
        }

        private void LogMagnetLink(string logFilePath, string magnetLink)
        {
            using (var writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Magnet link used: {magnetLink}");
            }
        }

        private void UpdateGameInstallationStatus(Game game, bool isInstalling)
        {
            game.IsInstalling = isInstalling;
            API.Instance.Database.Games.Update(game);
        }

        private void UpdateGameActionsAndStatus(Game game, string userDataPath)
        {
            // Add all .exe files as actions, excluding those listed in exclusions.txt
            var exclusionsPath = Path.Combine(userDataPath, "exclusions.txt");
            var exclusions = new HashSet<string>();
            if (File.Exists(exclusionsPath))
            {
                var exclusionLines = File.ReadAllLines(exclusionsPath);
                foreach (var line in exclusionLines)
                {
                    exclusions.Add(line.Trim().ToLower());
                }
            }

            var exeFiles = Directory.GetFiles(game.InstallDirectory, "*.exe", SearchOption.AllDirectories);
            foreach (var exeFile in exeFiles)
            {
                var exeName = Path.GetFileNameWithoutExtension(exeFile).ToLower();
                if (!exclusions.Contains(exeName))
                {
                    var action = new GameAction
                    {
                        Name = Path.GetFileNameWithoutExtension(exeFile),
                        Type = GameActionType.File,
                        Path = exeFile,
                        WorkingDir = Path.GetDirectoryName(exeFile)
                    };
                    game.GameActions.Add(action);
                }
            }
            API.Instance.Database.Games.Update(game);

            // Signal that the installation is completed
            InvokeOnInstalled(new GameInstalledEventArgs(game.Id));

            // Force library update for the specific game
            var pluginGames = GetGames(new LibraryGetGamesArgs());
            var updatedGame = pluginGames.FirstOrDefault(g => g.Name.Equals(game.Name, StringComparison.OrdinalIgnoreCase));
            if (updatedGame != null)
            {
                game.InstallDirectory = updatedGame.InstallDirectory;
                game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>(updatedGame.GameActions);
                API.Instance.Database.Games.Update(game);
            }
        }

        protected void InvokeOnInstalled(GameInstalledEventArgs args)
        {
            // Update the game's state
            var game = API.Instance.Database.Games.Get(args.GameId);
            if (game != null)
            {
                game.IsInstalling = false;
                game.IsInstalled = true;
                API.Instance.Database.Games.Update(game);

                // Notify Playnite
                PlayniteApi.Notifications.Add(new NotificationMessage("InstallCompleted", $"Installation of {game.Name} is complete!", NotificationType.Info));
            }
        }

        protected void InvokeOnUninstalled(GameUninstalledEventArgs args)
        {
            // Update the game's state after uninstallation
            var game = API.Instance.Database.Games.Get(args.GameId);
            if (game != null)
            {
                game.IsInstalling = false;
                game.IsInstalled = false;
                API.Instance.Database.Games.Update(game);

                // Notify Playnite
                PlayniteApi.Notifications.Add(new NotificationMessage("UninstallCompleted", $"Uninstallation of {game.Name} is complete!", NotificationType.Info));
            }
        }

        public class LocalInstallController : InstallController
        {
            private readonly FitGirlStore pluginInstance;

            public LocalInstallController(Game game, FitGirlStore instance) : base(game)
            {
                pluginInstance = instance;
                Name = "Install using setup.exe";
            }

            public override async void Install(InstallActionArgs args)
            {
                try
                {
                    await pluginInstance.GameInstaller(Game);
                }
                catch (Exception ex)
                {
                    // Handle the exception appropriately
                    logger.Error(ex, "Error occurred during game installation.");
                }
            }

        }

        public class LocalUninstallController : UninstallController
        {
            private readonly FitGirlStore pluginInstance;

            public LocalUninstallController(Game game, FitGirlStore instance) : base(game)
            {
                pluginInstance = instance;
                Name = "Uninstall using unins000.exe";
            }

            public override void Uninstall(UninstallActionArgs args)
            {
                pluginInstance.GameInstaller(Game);
            }
        }

        public class GameInstalledEventArgs : EventArgs
        {
            public Guid GameId { get; private set; }

            public GameInstalledEventArgs(Guid gameId)
            {
                GameId = gameId;
            }
        }

        public class GameUninstalledEventArgs : EventArgs
        {
            public Guid GameId { get; private set; }

            public GameUninstalledEventArgs(Guid gameId)
            {
                GameId = gameId;
            }
        }

        private async Task TimerTriggeredUpdate()
        {
            logger.Info("Timer triggered Auto Update Scanner.");
            await RunAutoUpdateScanner();
        }

        private async Task LibraryEventTriggeredUpdate()
        {
            logger.Info("Library event triggered Auto Update Scanner.");
            await RunAutoUpdateScanner();
        }

        private async Task RunAutoUpdateScanner()
        {
            lock (scannerLock)
            {
                if (isScannerRunning)
                {
                    logger.Info("Auto Update Scanner is already running. Skipping...");
                    return;
                }

                isScannerRunning = true; // Mark scanner as running
            }

            try
            {
                await AutoUpdateScanner();
            }
            catch (Exception ex)
            {
                logger.Error($"Error in Auto Update Scanner: {ex.Message}");
            }
            finally
            {
                lock (scannerLock)
                {
                    isScannerRunning = false; // Mark scanner as stopped
                }

                // Schedule the next run after 3 hours
                updateTimer.Change(3 * 60 * 60 * 1000, Timeout.Infinite); // 3 hours delay
            }
        }

        private async Task AutoUpdateScanner()
        {
            LogAutoUpdate("Auto Update Started");

            var autoUpdateFeature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals("[Auto Update]", StringComparison.OrdinalIgnoreCase));
            var updateReadyFeature = PlayniteApi.Database.Features.FirstOrDefault(f => f.Name.Equals("[Update Ready]", StringComparison.OrdinalIgnoreCase));

            if (autoUpdateFeature == null)
            {
                logger.Info("No '[Auto Update]' feature found in the database.");
                LogAutoUpdate("Auto Update Stopped");
                // Set the scanner to go off in 3 hours
                updateTimer.Change(3 * 60 * 60 * 1000, 3 * 60 * 60 * 1000);
                return;
            }

            if (updateReadyFeature == null)
            {
                logger.Info("No '[Update Ready]' feature found in the database.");
                LogAutoUpdate("Auto Update Stopped");
                // Set the scanner to go off in 3 hours
                updateTimer.Change(3 * 60 * 60 * 1000, 3 * 60 * 60 * 1000);
                return;
            }

            // Wait for the library to be fully updated
            await Task.Delay(10000); // Adjust the delay as needed

            var gamesWithAutoUpdate = PlayniteApi.Database.Games.Where(g => g.FeatureIds != null && g.FeatureIds.Contains(autoUpdateFeature.Id)).ToList();

            foreach (var game in gamesWithAutoUpdate)
            {
                if (game.GameActions == null)
                {
                    logger.Info($"No game actions found for game: {game.Name}");
                    continue;
                }

                var downloadAction = game.GameActions.FirstOrDefault(action => action.Name == "Download: Fitgirl" && action.Type == GameActionType.URL);
                if (downloadAction == null)
                {
                    logger.Info($"No download action found for game: {game.Name}");
                    continue;
                }

                string gameDownloadUrl = downloadAction.Path;
                if (string.IsNullOrEmpty(gameDownloadUrl))
                {
                    logger.Info($"No URL found for game: {game.Name}");
                    continue;
                }

                string latestVersion = await CheckForNewVersion(gameDownloadUrl);
                if (!string.IsNullOrEmpty(latestVersion) && IsNewerVersion(game.Version, latestVersion))
                {
                    logger.Info($"New version found for game: {game.Name}, Version: {latestVersion}");
                    LogAutoUpdate($"Updating {game.Name} to {latestVersion}");
                    AddUpdateReadyFeature(game);
                }
            }

            var gamesWithUpdateReady = PlayniteApi.Database.Games.Where(g => g.FeatureIds != null && g.FeatureIds.Contains(updateReadyFeature.Id)).ToList();

            foreach (var game in gamesWithUpdateReady)
            {
                // Run the game installer for the current game
                await GameInstaller(game);

                // Wait for the current game to finish installing before checking the next game
                while (game.IsInstalling)
                {
                    await Task.Delay(1000); // Adjust the delay as needed
                }
            }

            LogAutoUpdate("Auto Update Stopped");

            // Set the scanner to go off in 3 hours
            updateTimer.Change(3 * 60 * 60 * 1000, 3 * 60 * 60 * 1000);
        }

        private void LogAutoUpdate(string message)
        {
            File.AppendAllText("Games.log", $"{DateTime.Now:HH:mm} - {message}{Environment.NewLine}");
        }

        private async Task<string> CheckForNewVersion(string gameDownloadUrl)
        {
            string pageContent = await LoadPageContent(gameDownloadUrl);
            string versionPattern = @"v[\d\.]+";
            var match = Regex.Match(pageContent, versionPattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Value;
            }

            return string.Empty;
        }

    }
}
