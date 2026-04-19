using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quicker.Public;

public static string Exec(IStepContext context)
{
    try
    {
        string extensionId = (context.GetVarValue("extensionId") as string ?? "").Trim();
        string defaultKeyword = (context.GetVarValue("keyword") as string ?? "").Trim();
        string defaultOutputDir = (context.GetVarValue("outputDir") as string ?? "").Trim();
        int defaultMaxVideos = ToInt(context.GetVarValue("maxVideos"), 3);

        extensionId = ResolveExtensionId(extensionId);
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return "ERROR: failed to resolve extension id for Douyin Keyword Search.";
        }
        if (string.IsNullOrWhiteSpace(defaultOutputDir))
        {
            defaultOutputDir = @"D:\chajian\downloads";
        }
        if (defaultMaxVideos <= 0)
        {
            defaultMaxVideos = 3;
        }

        var formData = ShowInputDialog(defaultKeyword, defaultOutputDir, defaultMaxVideos);
        if (formData == null)
        {
            return "CANCELLED";
        }

        string keyword = formData["keyword"].Trim();
        string outputDir = formData["outputDir"].Trim();
        int maxVideos = ToInt(formData["maxVideos"], defaultMaxVideos);

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return "ERROR: keyword is required.";
        }
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = defaultOutputDir;
        }
        if (maxVideos <= 0)
        {
            maxVideos = 1;
        }

        context.SetVarValue("keyword", keyword);
        context.SetVarValue("outputDir", outputDir);
        context.SetVarValue("maxVideos", maxVideos);

        Directory.CreateDirectory(outputDir);

        DateTime triggerTime = DateTime.Now;
        string triggerUrl =
            $"chrome-extension://{extensionId}/trigger.html?keyword={Uri.EscapeDataString(keyword)}&downloadVideos=0&active=0&closeTab=1";

        OpenEdge(triggerUrl);

        string jsonPath = WaitForExportJson(triggerTime, TimeSpan.FromSeconds(90));
        string rawText = File.ReadAllText(jsonPath, Encoding.UTF8);
        List<Dictionary<string, string>> items = ExtractItems(rawText).Take(maxVideos).ToList();

        if (items.Count == 0)
        {
            return $"ERROR: no videoUrl entries found in {jsonPath}";
        }

        var downloaded = new List<string>();
        var progress = CreateProgressWindow(items.Count);

        try
        {
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                string baseFileName = BuildBaseFileName(keyword, item);
                string destination = Path.Combine(outputDir, baseFileName + ".mp4");

                UpdateProgress(progress, index, items.Count, baseFileName);
                DownloadVideo(
                    item["videoUrl"],
                    item["detailUrl"],
                    destination,
                    (received, total) => UpdateProgressValue(progress, index, items.Count, baseFileName, received, total));

                WriteMetadataJson(keyword, jsonPath, item, Path.Combine(outputDir, baseFileName + ".json"));
                downloaded.Add(destination);
                UpdateProgress(progress, index + 1, items.Count, baseFileName);
            }
        }
        finally
        {
            CloseProgressWindow(progress);
        }

        string summary = $"OK: keyword={keyword}; json={jsonPath}; downloaded={downloaded.Count}";
        context.SetVarValue("rtn", summary);
        context.SetVarValue("text", string.Join(Environment.NewLine, downloaded));

        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show(
                $"关键词：{keyword}\n导出文件：{jsonPath}\n下载完成：{downloaded.Count} 个",
                "抖音搜索下载器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });

        return summary;
    }
    catch (Exception ex)
    {
        context.SetVarValue("errMessage", ex.ToString());
        return "ERROR: " + ex.Message;
    }
}

static Dictionary<string, string> ShowInputDialog(string defaultKeyword, string defaultOutputDir, int defaultMaxVideos)
{
    Dictionary<string, string> result = null;

    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window
        {
            Title = "抖音搜索下载器",
            Width = 520,
            Height = 300,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            FontSize = 14
        };

        var root = new Grid
        {
            Margin = new Thickness(20)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "输入关键词后，调用插件搜索并由 Quicker 直接下载",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = new SolidColorBrush(Color.FromRgb(34, 34, 34))
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        Grid BuildFieldRow(string labelText, Control inputControl, int rowIndex)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = labelText,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68))
            };

            inputControl.Height = 34;
            inputControl.VerticalContentAlignment = VerticalAlignment.Center;
            inputControl.Padding = new Thickness(10, 4, 10, 4);

            Grid.SetColumn(label, 0);
            Grid.SetColumn(inputControl, 1);
            grid.Children.Add(label);
            grid.Children.Add(inputControl);
            Grid.SetRow(grid, rowIndex);
            return grid;
        }

        var keywordBox = new TextBox
        {
            Text = defaultKeyword ?? ""
        };
        root.Children.Add(BuildFieldRow("关键词", keywordBox, 1));

        var outputDirBox = new TextBox
        {
            Text = defaultOutputDir ?? @"D:\chajian\downloads"
        };
        root.Children.Add(BuildFieldRow("下载目录", outputDirBox, 2));

        var maxVideosBox = new TextBox
        {
            Text = defaultMaxVideos.ToString()
        };
        root.Children.Add(BuildFieldRow("下载数量", maxVideosBox, 3));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelButton.Click += (_, __) =>
        {
            window.Close();
        };

        var okButton = new Button
        {
            Content = "开始下载",
            Width = 100,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(255, 92, 53)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 92, 53))
        };
        okButton.Click += (_, __) =>
        {
            result = new Dictionary<string, string>
            {
                ["keyword"] = keywordBox.Text ?? "",
                ["outputDir"] = outputDirBox.Text ?? "",
                ["maxVideos"] = maxVideosBox.Text ?? ""
            };
            window.Close();
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, __) =>
        {
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
            window.Focus();
            keywordBox.Focus();
            keywordBox.SelectAll();
        };
        window.ShowDialog();
    });

    return result;
}

static Dictionary<string, object> CreateProgressWindow(int totalCount)
{
    var state = new Dictionary<string, object>();

    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window
        {
            Title = "下载进度",
            Width = 520,
            Height = 180,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            Topmost = true
        };

        var root = new Grid
        {
            Margin = new Thickness(20)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"准备下载 0 / {totalCount}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = new SolidColorBrush(Color.FromRgb(34, 34, 34))
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var detail = new TextBlock
        {
            Text = "等待开始...",
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85))
        };
        Grid.SetRow(detail, 1);
        root.Children.Add(detail);

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 22,
            Value = 0
        };
        Grid.SetRow(progressBar, 2);
        root.Children.Add(progressBar);

        window.Content = root;
        window.Show();

        state["window"] = window;
        state["title"] = title;
        state["detail"] = detail;
        state["bar"] = progressBar;
    });

    return state;
}

static void UpdateProgress(Dictionary<string, object> progress, int completed, int total, string currentName)
{
    if (progress == null)
    {
        return;
    }

    Application.Current.Dispatcher.Invoke(() =>
    {
        var title = progress["title"] as TextBlock;
        var detail = progress["detail"] as TextBlock;
        var bar = progress["bar"] as ProgressBar;

        double percent = total <= 0 ? 0 : Math.Min(100, (completed * 100.0) / total);
        if (title != null)
        {
            title.Text = $"下载进度 {completed} / {total}";
        }
        if (detail != null)
        {
            detail.Text = string.IsNullOrWhiteSpace(currentName)
                ? "正在准备下载..."
                : $"当前文件：{currentName}";
        }
        if (bar != null)
        {
            bar.Value = percent;
        }
    });
}

static void UpdateProgressValue(
    Dictionary<string, object> progress,
    int index,
    int total,
    string currentName,
    long receivedBytes,
    long totalBytes)
{
    if (progress == null)
    {
        return;
    }

    Application.Current.Dispatcher.Invoke(() =>
    {
        var title = progress["title"] as TextBlock;
        var detail = progress["detail"] as TextBlock;
        var bar = progress["bar"] as ProgressBar;

        double itemRatio = totalBytes > 0 ? Math.Min(1.0, receivedBytes / (double)totalBytes) : 0;
        double overall = total <= 0 ? 0 : ((index + itemRatio) / total) * 100.0;

        if (title != null)
        {
            title.Text = $"下载进度 {index + 1} / {total}";
        }
        if (detail != null)
        {
            string sizeText = totalBytes > 0
                ? $"{FormatBytes(receivedBytes)} / {FormatBytes(totalBytes)}"
                : $"{FormatBytes(receivedBytes)}";
            detail.Text = $"当前文件：{currentName}\n已下载：{sizeText}";
        }
        if (bar != null)
        {
            bar.Value = Math.Min(100, Math.Max(0, overall));
        }
    });
}

static void CloseProgressWindow(Dictionary<string, object> progress)
{
    if (progress == null)
    {
        return;
    }

    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = progress.ContainsKey("window") ? progress["window"] as Window : null;
        if (window != null)
        {
            window.Close();
        }
    });
}

static string ResolveExtensionId(string configuredId)
{
    if (!string.IsNullOrWhiteSpace(configuredId))
    {
        return configuredId;
    }

    foreach (string prefsPath in GetBrowserPreferencePaths())
    {
        if (!File.Exists(prefsPath))
        {
            continue;
        }

        string raw = File.ReadAllText(prefsPath, Encoding.UTF8);
        string matched = FindExtensionIdByPath(raw);
        if (!string.IsNullOrWhiteSpace(matched))
        {
            return matched;
        }
    }

    return "";
}

static IEnumerable<string> GetBrowserPreferencePaths()
{
    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string[] roots = new[]
    {
        Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
        Path.Combine(localAppData, "Google", "Chrome", "User Data")
    };

    foreach (string root in roots)
    {
        if (!Directory.Exists(root))
        {
            continue;
        }

        foreach (string profileDir in Directory.GetDirectories(root))
        {
            string profileName = Path.GetFileName(profileDir);
            if (profileName.Equals("System Profile", StringComparison.OrdinalIgnoreCase) ||
                profileName.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return Path.Combine(profileDir, "Preferences");
            yield return Path.Combine(profileDir, "Secure Preferences");
        }
    }
}

static string FindExtensionIdByPath(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return "";
    }

    MatchCollection pathMatches = Regex.Matches(
        raw,
        "\"path\"\\s*:\\s*\"(?<path>(?:\\\\.|[^\"])*)\"",
        RegexOptions.Singleline);

    foreach (Match pathMatch in pathMatches)
    {
        string extensionPath = pathMatch.Groups["path"].Value
            .Replace("\\\\", "\\")
            .Replace("\\/", "/")
            .Replace("\\\"", "\"")
            .Trim();

        if (!IsDouyinExtensionPath(extensionPath))
        {
            continue;
        }

        string beforePath = raw.Substring(0, pathMatch.Index);
        MatchCollection idMatches = Regex.Matches(
            beforePath,
            "\"(?<id>[a-p]{32})\"\\s*:\\s*\\{",
            RegexOptions.Singleline);

        if (idMatches.Count > 0)
        {
            return idMatches[idMatches.Count - 1].Groups["id"].Value;
        }
    }

    return "";
}

static bool IsDouyinExtensionPath(string extensionPath)
{
    if (string.IsNullOrWhiteSpace(extensionPath) || !Directory.Exists(extensionPath))
    {
        return false;
    }

    string manifestPath = Path.Combine(extensionPath, "manifest.json");
    string triggerPath = Path.Combine(extensionPath, "trigger.html");
    string backgroundPath = Path.Combine(extensionPath, "background.js");

    if (!File.Exists(manifestPath) || !File.Exists(triggerPath) || !File.Exists(backgroundPath))
    {
        return false;
    }

    string manifest = File.ReadAllText(manifestPath, Encoding.UTF8);
    return manifest.Contains("\"name\": \"Douyin Keyword Search\"") ||
           manifest.Contains("\"name\":\"Douyin Keyword Search\"") ||
           manifest.Contains("\"host_permissions\"") && manifest.Contains("douyin.com");
}

static int ToInt(object value, int fallback)
{
    if (value == null)
    {
        return fallback;
    }

    if (int.TryParse(Convert.ToString(value), out int parsed))
    {
        return parsed;
    }

    return fallback;
}

static void OpenEdge(string url)
{
    string[] candidates = new[]
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
    };

    string edgePath = candidates.FirstOrDefault(File.Exists);
    if (!string.IsNullOrWhiteSpace(edgePath))
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = edgePath,
            Arguments = $"\"{url}\"",
            UseShellExecute = true
        });
        return;
    }

    Process.Start(new ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });
}

static string WaitForExportJson(DateTime triggerTime, TimeSpan timeout)
{
    List<string> candidateDirs = GetCandidateDownloadDirectories();

    DateTime deadline = DateTime.Now.Add(timeout);
    while (DateTime.Now < deadline)
    {
        FileInfo latest = candidateDirs
            .Where(Directory.Exists)
            .SelectMany(dir =>
            {
                try
                {
                    return new DirectoryInfo(dir).GetFiles("*.*");
                }
                catch
                {
                    return Array.Empty<FileInfo>();
                }
            })
            .Where(file => file.LastWriteTime >= triggerTime)
            .Where(IsReadableExportCandidate)
            .OrderByDescending(file => file.LastWriteTime)
            .FirstOrDefault(file =>
            {
                try
                {
                    string raw = File.ReadAllText(file.FullName, Encoding.UTF8);
                    return raw.Contains("\"videoUrl\"");
                }
                catch
                {
                    return false;
                }
            });

        if (latest != null)
        {
            return latest.FullName;
        }

        System.Threading.Thread.Sleep(1000);
    }

    throw new TimeoutException("Timed out waiting for export json.");
}

static bool IsReadableExportCandidate(FileInfo file)
{
    if (file == null || !file.Exists || file.Length <= 0 || file.Length > 20 * 1024 * 1024)
    {
        return false;
    }

    string extension = (file.Extension ?? "").ToLowerInvariant();
    if (extension == ".crdownload" || extension == ".tmp" || extension == ".part")
    {
        return false;
    }

    return true;
}

static List<string> GetCandidateDownloadDirectories()
{
    var dirs = new List<string>();

    void AddDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!dirs.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            dirs.Add(normalized);
        }
    }

    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    AddDir(Path.Combine(userProfile, "Downloads"));
    AddDir(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    foreach (string prefsPath in GetBrowserPreferencePaths().Where(File.Exists))
    {
        string raw = File.ReadAllText(prefsPath, Encoding.UTF8);

        foreach (Match match in Regex.Matches(
            raw,
            "\"default_directory\"\\s*:\\s*\"(?<path>(?:\\\\.|[^\"])*)\"",
            RegexOptions.Singleline))
        {
            string path = match.Groups["path"].Value
                .Replace("\\\\", "\\")
                .Replace("\\/", "/")
                .Replace("\\\"", "\"");
            AddDir(path);
        }
    }

    return dirs;
}

static List<Dictionary<string, string>> ExtractItems(string rawText)
{
    var items = new List<Dictionary<string, string>>();

    MatchCollection videoIdMatches = Regex.Matches(rawText, "\"videoId\"\\s*:\\s*\"(?<videoId>\\d+)\"");
    for (int i = 0; i < videoIdMatches.Count; i++)
    {
        int start = videoIdMatches[i].Index;
        int end = i + 1 < videoIdMatches.Count ? videoIdMatches[i + 1].Index : rawText.Length;
        string block = rawText.Substring(start, end - start);
        string videoId = ExtractField(block, "videoId");
        string detailUrl = ExtractField(block, "detailUrl");
        string videoUrl = ExtractField(block, "videoUrl");

        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(videoUrl))
        {
            continue;
        }

        items.Add(new Dictionary<string, string>
        {
            ["videoId"] = videoId,
            ["detailUrl"] = detailUrl,
            ["videoUrl"] = videoUrl,
            ["author"] = ExtractField(block, "author"),
            ["title"] = ExtractField(block, "title"),
            ["rawObject"] = block.Trim()
        });
    }

    return items;
}

static string ExtractField(string rawObject, string fieldName)
{
    if (string.IsNullOrWhiteSpace(rawObject) || string.IsNullOrWhiteSpace(fieldName))
    {
        return "";
    }

    Match match = Regex.Match(
        rawObject,
        $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.Singleline);

    if (!match.Success)
    {
        return "";
    }

    return Regex.Unescape(match.Groups["value"].Value);
}

static string BuildBaseFileName(string keyword, Dictionary<string, string> item)
{
    string composite = $"{keyword}-{item["title"]}";
    if (string.IsNullOrWhiteSpace(item["title"]))
    {
        composite = $"{keyword}-{item["videoId"]}";
    }

    string sanitized = composite.Normalize(NormalizationForm.FormKC);

    foreach (char c in Path.GetInvalidFileNameChars())
    {
        sanitized = sanitized.Replace(c, '_');
    }

    sanitized = Regex.Replace(sanitized, "\\s+", "_").Trim('_');
    if (string.IsNullOrWhiteSpace(sanitized))
    {
        sanitized = item["videoId"];
    }
    if (sanitized.Length > 80)
    {
        sanitized = sanitized.Substring(0, 80);
    }

    return sanitized;
}

static void WriteMetadataJson(string keyword, string sourceJsonPath, Dictionary<string, string> item, string destination)
{
    string rawObject = item.ContainsKey("rawObject") ? item["rawObject"] : "{}";
    string originalRecord = IsJsonObject(rawObject) ? rawObject : "{}";

    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"keyword\": \"" + EscapeJson(keyword) + "\",");
    sb.AppendLine("  \"sourceExportJson\": \"" + EscapeJson(sourceJsonPath) + "\",");
    sb.AppendLine("  \"savedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",");
    sb.AppendLine("  \"videoId\": \"" + EscapeJson(GetItemValue(item, "videoId")) + "\",");
    sb.AppendLine("  \"title\": \"" + EscapeJson(GetItemValue(item, "title")) + "\",");
    sb.AppendLine("  \"author\": \"" + EscapeJson(GetItemValue(item, "author")) + "\",");
    sb.AppendLine("  \"detailUrl\": \"" + EscapeJson(GetItemValue(item, "detailUrl")) + "\",");
    sb.AppendLine("  \"videoUrl\": \"" + EscapeJson(GetItemValue(item, "videoUrl")) + "\",");
    sb.AppendLine("  \"originalRecord\": " + originalRecord);
    sb.AppendLine("}");

    File.WriteAllText(destination, sb.ToString(), new UTF8Encoding(false));
}

static string GetItemValue(Dictionary<string, string> item, string key)
{
    return item.ContainsKey(key) ? item[key] ?? "" : "";
}

static bool IsJsonObject(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    string trimmed = text.Trim();
    return trimmed.StartsWith("{") && trimmed.EndsWith("}");
}

static string EscapeJson(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return "";
    }

    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
}

static string FormatBytes(long bytes)
{
    string[] units = new[] { "B", "KB", "MB", "GB" };
    double value = Math.Max(0, bytes);
    int unitIndex = 0;

    while (value >= 1024 && unitIndex < units.Length - 1)
    {
        value /= 1024;
        unitIndex++;
    }

    return $"{value:0.##} {units[unitIndex]}";
}

static void DownloadVideo(string url, string referer, string destination, Action<long, long> progressCallback)
{
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
    request.Method = "GET";
    request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    request.Referer = string.IsNullOrWhiteSpace(referer) ? "https://www.douyin.com/" : referer;
    request.Accept = "*/*";
    request.Headers["Accept-Language"] = "zh-SG,zh-CN;q=0.9,zh;q=0.8";
    request.AddRange(0);
    request.Timeout = 30000;
    request.ReadWriteTimeout = 30000;

    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
    {
        if (response.StatusCode != HttpStatusCode.PartialContent &&
            response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException("Unexpected status code: " + (int)response.StatusCode);
        }

        using (Stream source = response.GetResponseStream())
        using (FileStream target = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            long totalBytes = response.ContentLength > 0 ? response.ContentLength : -1;
            long receivedBytes = 0;
            byte[] buffer = new byte[1024 * 256];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                receivedBytes += read;
                progressCallback?.Invoke(receivedBytes, totalBytes);
            }
        }
    }
}
