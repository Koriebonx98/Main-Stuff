using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Xml.Linq;

namespace FitGirlStore
{
    public class FitGirlStore : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public override Guid Id { get; } = Guid.Parse("5c415b39-d755-4514-9be5-2701d3de94d4");
        public override string Name => "FitGirl Store";
        private static readonly string baseUrl = "https://fitgirl-repacks.site/all-my-repacks-a-z/?lcp_page0=";

        public FitGirlStore(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties { HasSettings = false };
        }

        private async Task<List<GameMetadata>> ScrapeSite()
        {
            var gameEntries = new List<GameMetadata>();
            var uniqueGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    string version = ExtractVersionNumber(text);
                    string cleanName = CleanGameName(text);

                    if (string.IsNullOrEmpty(cleanName))
                    {
                        cleanName = Regex.Replace(href, @"https://fitgirl-repacks.site/([^/]+)/$", "$1").Replace('-', ' ');
                    }

                    if (!string.IsNullOrEmpty(cleanName) && !href.Contains("page0="))
                    {
                        var gameKey = $"{cleanName}|{version}";
                        if (uniqueGames.Contains(gameKey))
                            continue;

                        uniqueGames.Add(gameKey);

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
                            Version = version,
                            IsInstalled = false
                        };

                        if (!IsDuplicate(gameMetadata))
                        {
                            gameEntries.Add(gameMetadata);
                        }
                    }
                }
            }

            return gameEntries;
        }

        private string ExtractVersionNumber(string name)
        {
            var buildMatch = Regex.Match(name, @"Build (\d+)");
            if (buildMatch.Success)
            {
                return buildMatch.Groups[1].Value;
            }

            var versionMatch = Regex.Match(name, @"v[\d\.]+");
            return versionMatch.Success ? versionMatch.Value : "0";
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
                return await httpClient.GetStringAsync(url);
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

            // Remove specific phrases
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Windows 7 Fix", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Bonus Soundtrack", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Bonus OST", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, "- Ultimate Fishing Bundle", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, "- Digital Deluxe Edition", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Bonus Content", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Bonus", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Soundtrack", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*2 DLCs", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*2 DLC", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*All DLC", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*HotFix", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*HotFix 1", "", RegexOptions.IgnoreCase);
            cleanName = Regex.Replace(cleanName, @"\s*\+\s*Multiplayer", "", RegexOptions.IgnoreCase);

            // Remove text in parentheses or square brackets
            cleanName = Regex.Replace(cleanName, @"[

\[\(].*?[\]

\)]", "").Trim();

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
                "https://wordpress.org/"
            };

            if (Regex.IsMatch(href, @"^https://fitgirl-repacks.site/\d{4}/\d{2}/$") ||
                Regex.IsMatch(href, @"^https://fitgirl-repacks.site/all-my-repacks-a-z/\?lcp_page0=\d+#lcp_instance_0$") ||
                nonGameUrls.Contains(href))
            {
                return false;
            }

            return true;
        }

        private bool IsDuplicate(GameMetadata gameMetadata)
        {
            // Use the original name for comparison
            return PlayniteApi.Database.Games.Any(existingGame => existingGame.PluginId == Id && existingGame.Name.Equals(gameMetadata.Name, StringComparison.OrdinalIgnoreCase));
        }


        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();
            var scrapedGames = ScrapeSite().GetAwaiter().GetResult();
            logger.Info($"Total repack entries: {scrapedGames.Count}");

            // Add scraped games to the Playnite database
            foreach (var game in scrapedGames)
            {
                var gameName = ConvertHyphens(game.Name);
                var sanitizedGameName = SanitizePath(gameName);

                if (!PlayniteApi.Database.Games.Any(existingGame => existingGame.PluginId == Id && existingGame.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase)))
                {
                    var platformId = PlayniteApi.Database.Platforms.FirstOrDefault(p => p.Name.Equals(game.Platforms.First().ToString(), StringComparison.OrdinalIgnoreCase))?.Id;
                    if (platformId != null)
                    {
                        var gameMetadata = new GameMetadata()
                        {
                            Name = gameName,
                            GameId = gameName.ToLower(),
                            Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("PC (Windows)") },
                            GameActions = new List<GameAction>(),
                            IsInstalled = false,
                            InstallDirectory = null, // Scraped games don't have an install directory
                            Icon = new MetadataFile(Path.Combine(sanitizedGameName, "icon.png")),
                            BackgroundImage = new MetadataFile(Path.Combine(sanitizedGameName, "background.png"))
                        };

                        gameMetadata.GameActions.Add(new GameAction
                        {
                            Name = "Download: Fitgirl",
                            Type = GameActionType.URL,
                            Path = game.GameActions.FirstOrDefault()?.Path,
                            IsPlayAction = false
                        });

                        games.Add(gameMetadata);
                    }
                    else
                    {
                        logger.Error($"Platform not found for game: {gameName}, Platform: {game.Platforms.First()}");
                    }
                }
            }

            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network));
            var exclusions = LoadExclusions();
            var gameFolders = new List<string>();

            foreach (var drive in drives)
            {
                var gamesFolderPath = Path.Combine(drive.RootDirectory.FullName, "Games");

                if (Directory.Exists(gamesFolderPath))
                {
                    gameFolders.AddRange(Directory.GetDirectories(gamesFolderPath));
                }
            }

            var existingGames = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();

            // Mark games as uninstalled if not found in the "Games" folder
            foreach (var existingGame in existingGames)
            {
                var folder = gameFolders.FirstOrDefault(f => ConvertHyphens(Path.GetFileName(f)).Equals(existingGame.Name, StringComparison.OrdinalIgnoreCase));
                if (folder == null)
                {
                    existingGame.InstallDirectory = null;
                    existingGame.IsInstalled = false;
                    API.Instance.Database.Games.Update(existingGame);
                    logger.Info($"Marked game as uninstalled: {existingGame.Name}");
                }
            }

            // Update existing games and add new games
            foreach (var folder in gameFolders)
            {
                var folderName = Path.GetFileName(folder);
                var gameName = ConvertHyphens(CleanGameName(folderName));
                var sanitizedGameName = SanitizePath(gameName);
                var exeFiles = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories)
                                        .Where(exe => !exclusions.Contains(Path.GetFileName(exe)) &&
                                                      !Path.GetFileName(exe).ToLower().Contains("setup") &&
                                                      !Path.GetFileName(exe).ToLower().Contains("unins"));

                logger.Info($"Found {exeFiles.Count()} executable files for game: {gameName} in folder: {folder}");
                foreach (var exe in exeFiles)
                {
                    logger.Info($"Found executable: {exe}");
                }

                if (!exeFiles.Any())
                {
                    logger.Warn($"No valid executable files found for game: {gameName} in folder: {folder}");
                    continue;
                }

                var existingGame = existingGames.FirstOrDefault(eg => eg.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase));
                if (existingGame != null)
                {
                    // Update the existing game with install directory and play actions
                    existingGame.InstallDirectory = folder;
                    existingGame.IsInstalled = true;
                    logger.Info($"Updated install directory for existing game: {gameName} to {folder}");

                    if (!existingGame.GameActions.Any(action => action.Type == GameActionType.URL && action.Name == "Download: Fitgirl"))
                    {
                        var scrapedGame = games.FirstOrDefault(g => g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase));
                        if (scrapedGame != null && scrapedGame.GameActions.FirstOrDefault() != null)
                        {
                            existingGame.GameActions.Add(new GameAction
                            {
                                Name = "Download: Fitgirl",
                                Type = GameActionType.URL,
                                Path = scrapedGame.GameActions.FirstOrDefault()?.Path,
                                IsPlayAction = false
                            });
                            logger.Info($"Added 'Download: Fitgirl' action to existing game: {gameName}");
                        }
                    }

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
                            logger.Info($"Added play action: {exe} to existing game: {gameName}");
                        }
                    }

                    API.Instance.Database.Games.Update(existingGame);
                    logger.Info($"Updated existing game: {existingGame.Name} with new actions and install directory.");
                }
                else
                {
                    var gameMetadata = new GameMetadata()
                    {
                        Name = gameName,
                        Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("PC (Windows)") },
                        GameId = gameName.ToLower(),
                        GameActions = new List<GameAction>(),
                        IsInstalled = true,
                        InstallDirectory = folder,
                        Icon = new MetadataFile(Path.Combine(folder, "icon.png")),
                        BackgroundImage = new MetadataFile(Path.Combine(folder, "background.png"))
                    };

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
                        logger.Info($"Added play action: {exe} to new game: {gameName}");
                    }

                    games.Add(gameMetadata);
                    logger.Info($"Added new game: {gameMetadata.Name} with actions and install directory.");
                }
            }

            return games;
        }


        private string ConvertHyphens(string name)
        {
            return Regex.Replace(name, @"(\w)\s-\s(\w)", "$1: $2");
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

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id)
            {
                yield return new LocalInstallController(args.Game, this);
            }
        }


        public async void GameInstaller(Game game)
        {
            var userDataPath = GetPluginUserDataPath();
            var fdmPath = Path.Combine(userDataPath, "Free Download Manager", "fdm.exe");

            if (!File.Exists(fdmPath))
            {
                API.Instance.Dialogs.ShowErrorMessage($"fdm.exe not found at {fdmPath}. Installation cancelled.", "Error");
                return;
            }

            // Check for existing repacks and set the install directory if found
            string repackFolder = null;
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network))
            {
                var repacksFolder = Path.Combine(drive.RootDirectory.FullName, "Repacks");
                if (Directory.Exists(repacksFolder))
                {
                    var potentialRepackFolders = Directory.GetDirectories(repacksFolder, "*", SearchOption.TopDirectoryOnly);
                    foreach (var potentialRepackFolder in potentialRepackFolders)
                    {
                        if (Path.GetFileName(potentialRepackFolder).IndexOf(game.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            repackFolder = potentialRepackFolder;
                            break;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(repackFolder))
                {
                    break;
                }
            }

            if (!string.IsNullOrEmpty(repackFolder))
            {
                var setupExe = Directory.GetFiles(repackFolder, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(setupExe))
                {
                    game.InstallDirectory = repackFolder;
                    API.Instance.Database.Games.Update(game);

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

                    // Wait and retry to find the newly installed game directory
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

                            // Reload the game's data to ensure the install directory is up-to-date
                            game = API.Instance.Database.Games.Get(game.Id);
                        }
                    }

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
                else
                {
                    API.Instance.Dialogs.ShowErrorMessage("Setup.exe not found. Installation cancelled.", "Error");
                }
            }
            else
            {
                // If repack not found, proceed to download via fdm.exe
                var downloadAction = game.GameActions.FirstOrDefault(action => action.Name == "Download: Fitgirl" && action.Type == GameActionType.URL);
                var gameDownloadUrl = downloadAction?.Path;
                if (!string.IsNullOrEmpty(gameDownloadUrl))
                {
                    var magnetLink = ScrapeMagnetLink(gameDownloadUrl);
                    if (!string.IsNullOrEmpty(magnetLink))
                    {
                        // Start Free Download Manager with the magnet link
                        await Task.Run(() =>
                        {
                            using (var process = new Process())
                            {
                                process.StartInfo.FileName = fdmPath;
                                process.StartInfo.Arguments = magnetLink;
                                process.StartInfo.UseShellExecute = true;
                                process.Start();
                                process.WaitForExit(); // Wait for FDM to close
                            }

                            // Check for the downloaded repack after FDM is closed
                            string downloadedRepackFolder = null;
                            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network))
                            {
                                var potentialDownloadedRepackFolder = Path.Combine(drive.RootDirectory.FullName, "Repacks");
                                if (Directory.Exists(potentialDownloadedRepackFolder))
                                {
                                    var potentialRepackFolders = Directory.GetDirectories(potentialDownloadedRepackFolder, "*", SearchOption.TopDirectoryOnly);
                                    foreach (var potentialRepackFolder in potentialRepackFolders)
                                    {
                                        if (Path.GetFileName(potentialRepackFolder).IndexOf(game.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            downloadedRepackFolder = potentialRepackFolder;
                                            break;
                                        }
                                    }
                                }
                                if (!string.IsNullOrEmpty(downloadedRepackFolder))
                                {
                                    break;
                                }
                            }

                            if (string.IsNullOrEmpty(downloadedRepackFolder))
                            {
                                API.Instance.Dialogs.ShowErrorMessage("Repack folder not found after download.", "Error");
                                return;
                            }

                            // Set the game's install directory to the downloaded repack folder
                            game.InstallDirectory = downloadedRepackFolder;
                            API.Instance.Database.Games.Update(game);

                            // Run setup.exe after the download completes
                            var setupExe = Directory.GetFiles(downloadedRepackFolder, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
                            if (!string.IsNullOrEmpty(setupExe))
                            {
                                using (var process = new Process())
                                {
                                    process.StartInfo.FileName = setupExe;
                                    process.StartInfo.WorkingDirectory = Path.GetDirectoryName(setupExe);
                                    process.StartInfo.UseShellExecute = true;
                                    process.Start();
                                    process.WaitForExit();
                                }

                                // Wait and retry to find the newly installed game directory
                                var rootDrive = Path.GetPathRoot(downloadedRepackFolder);
                                var gamesFolderPath = Path.Combine(rootDrive, "Games");
                                if (Directory.Exists(gamesFolderPath))
                                {
                                    var installedGameDir = Directory.GetDirectories(gamesFolderPath, "*", SearchOption.AllDirectories)
                                        .FirstOrDefault(d => Path.GetFileName(d).Equals(game.Name, StringComparison.OrdinalIgnoreCase));

                                    if (!string.IsNullOrEmpty(installedGameDir))
                                    {
                                        game.InstallDirectory = installedGameDir;
                                        API.Instance.Database.Games.Update(game);

                                        // Reload the game's data to ensure the install directory is up-to-date
                                        game = API.Instance.Database.Games.Get(game.Id);
                                    }
                                }

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
                        });
                    }
                    else
                    {
                        API.Instance.Dialogs.ShowErrorMessage("Magnet link not found. Download cancelled.", "Error");
                    }
                }
                else
                {
                    API.Instance.Dialogs.ShowErrorMessage("Game download URL not found. Download cancelled.", "Error");
                }
            }
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
                    return match.Value;
                }
            }
            return null;
        }

        public void GameUninstaller(Game game)
        {
            // Fetch the latest game data at the beginning of the method
            game = API.Instance.Database.Games.Get(game.Id);

            var uninstallExe = Directory.GetFiles(game.InstallDirectory, "unins000.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(uninstallExe))
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = uninstallExe;
                    process.StartInfo.WorkingDirectory = game.InstallDirectory;
                    process.StartInfo.UseShellExecute = true;
                    process.Start();
                    process.WaitForExit();
                }

                // Ensure "unins000.exe" has stopped running
                var processName = Path.GetFileNameWithoutExtension(uninstallExe);
                while (Process.GetProcessesByName(processName).Any())
                {
                    System.Threading.Thread.Sleep(1000);
                }

                // Check if the game is no longer in the current InstallDirectory
                while (Directory.Exists(game.InstallDirectory))
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
            else
            {
                if (Directory.Exists(game.InstallDirectory))
                {
                    Directory.Delete(game.InstallDirectory, true);
                }

                // Check if the game is no longer in the current InstallDirectory
                while (Directory.Exists(game.InstallDirectory))
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }

            // Update the install directory to Repacks if it exists, otherwise set to empty
            var rootDrive = Path.GetPathRoot(game.InstallDirectory);
            var repacksFolderPath = Path.Combine(rootDrive, "Repacks");
            var repacksGameDir = Path.Combine(repacksFolderPath, game.Name);

            if (Directory.Exists(repacksGameDir))
            {
                game.InstallDirectory = repacksGameDir;

                var setupExe = Directory.GetFiles(repacksGameDir, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(setupExe))
                {
                    game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
            {
                new GameAction
                {
                    Name = "Install",
                    Type = GameActionType.File,
                    Path = setupExe,
                    IsPlayAction = true,
                    WorkingDir = repacksGameDir
                }
            };
                }
                else
                {
                    game.GameActions.Clear();
                }
            }
            else
            {
                game.InstallDirectory = string.Empty;
                game.GameActions.Clear();
            }

            game.IsInstalled = false;
            game.IsInstalling = false;
            API.Instance.Database.Games.Update(game);

            // Signal that the uninstallation is completed
            InvokeOnUninstalled(new GameUninstalledEventArgs(game.Id));
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

            public override void Install(InstallActionArgs args)
            {
                pluginInstance.GameInstaller(Game);
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
                pluginInstance.GameUninstaller(Game);
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

    }
}
