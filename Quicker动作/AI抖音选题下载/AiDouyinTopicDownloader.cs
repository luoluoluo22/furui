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
using System.Windows.Media.Imaging;
using Quicker.Public;

public static string Exec(IStepContext context)
{
    try
    {
        string apiUrl = ReadVar(context, "apiBaseUrl", "https://new.12ai.org/v1/chat/completions");
        string apiKey = ReadVar(context, "apiKey", "");
        string model = ReadVar(context, "model", "gemini-3.1-flash-lite-preview");
        string prompt = ReadVar(context, "systemPrompt", DefaultPrompt());
        string script = ReadVar(context, "scriptText", "");
        string outputDir = ReadVar(context, "outputDir", @"D:\chajian\downloads");
        string extensionId = ReadVar(context, "extensionId", "");
        int maxPerKeyword = Math.Max(1, ToInt(context.GetVarValue("maxResultsPerKeyword"), 8));

        var setup = ShowSetupDialog(apiUrl, apiKey, model, prompt, script, outputDir, maxPerKeyword);
        if (setup == null)
        {
            return "CANCELLED";
        }

        apiUrl = setup["apiUrl"].Trim();
        apiKey = setup["apiKey"].Trim();
        model = setup["model"].Trim();
        prompt = setup["prompt"].Trim();
        script = setup["script"].Trim();
        outputDir = setup["outputDir"].Trim();
        maxPerKeyword = Math.Max(1, ToInt(setup["maxPerKeyword"], maxPerKeyword));

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return "ERROR: API 地址、密钥、模型 ID 都不能为空。";
        }
        if (string.IsNullOrWhiteSpace(script))
        {
            return "ERROR: 脚本内容不能为空。";
        }
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = @"D:\chajian\downloads";
        }

        context.SetVarValue("apiBaseUrl", apiUrl);
        context.SetVarValue("apiKey", apiKey);
        context.SetVarValue("model", model);
        context.SetVarValue("systemPrompt", prompt);
        context.SetVarValue("scriptText", script);
        context.SetVarValue("outputDir", outputDir);
        context.SetVarValue("maxResultsPerKeyword", maxPerKeyword);

        List<string> keywords = ShowKeywordSelection(CallAiForKeywords(apiUrl, apiKey, model, prompt, script));
        if (keywords == null)
        {
            return "CANCELLED";
        }
        if (keywords.Count == 0)
        {
            return "ERROR: 未选择关键词。";
        }

        extensionId = ResolveExtensionId(extensionId);
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return "ERROR: failed to resolve extension id for Douyin Keyword Search.";
        }
        context.SetVarValue("extensionId", extensionId);

        Directory.CreateDirectory(outputDir);
        var videos = new List<Dictionary<string, string>>();
        foreach (string keyword in keywords)
        {
            DateTime startedAt = DateTime.Now;
            string url = $"chrome-extension://{extensionId}/trigger.html?keyword={Uri.EscapeDataString(keyword)}&downloadVideos=0&active=0&closeTab=1";
            OpenEdge(url);
            string jsonPath = WaitForExportJson(startedAt, keyword, TimeSpan.FromSeconds(120));
            string raw = File.ReadAllText(jsonPath, Encoding.UTF8);

            foreach (var item in ExtractItems(raw, keyword).Take(maxPerKeyword))
            {
                item["sourceJsonPath"] = jsonPath;
                if (!videos.Any(existing => Get(existing, "videoId") == Get(item, "videoId")))
                {
                    videos.Add(item);
                }
            }
        }

        if (videos.Count == 0)
        {
            return "ERROR: 未抓取到可下载的视频结果。";
        }

        List<Dictionary<string, string>> selected = ShowVideoSelection(videos);
        if (selected == null)
        {
            return "CANCELLED";
        }
        if (selected.Count == 0)
        {
            return "ERROR: 未选择要下载的视频。";
        }

        var downloaded = new List<string>();
        var progress = CreateProgressWindow(selected.Count);
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                string name = BuildBaseFileName(Get(item, "sourceKeyword"), item);
                string videoPath = Path.Combine(outputDir, name + ".mp4");
                UpdateProgress(progress, i, selected.Count, name);
                DownloadVideo(Get(item, "videoUrl"), Get(item, "detailUrl"), videoPath,
                    (received, total) => UpdateProgressValue(progress, i, selected.Count, name, received, total));
                WriteMetadataJson(item, Path.Combine(outputDir, name + ".json"));
                downloaded.Add(videoPath);
                UpdateProgress(progress, i + 1, selected.Count, name);
            }
        }
        finally
        {
            CloseProgressWindow(progress);
        }

        string summary = $"OK: keywords={keywords.Count}; videos={selected.Count}; downloaded={downloaded.Count}";
        context.SetVarValue("rtn", summary);
        context.SetVarValue("text", string.Join(Environment.NewLine, downloaded));

        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show($"已下载 {downloaded.Count} 个视频。\n目录：{outputDir}", "AI抖音选题下载", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        return summary;
    }
    catch (Exception ex)
    {
        context.SetVarValue("errMessage", ex.ToString());
        return "ERROR: " + ex.Message;
    }
}

static Dictionary<string, string> ShowSetupDialog(string apiUrl, string apiKey, string model, string prompt, string script, string outputDir, int maxPerKeyword)
{
    Dictionary<string, string> result = null;
    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window
        {
            Title = "AI抖音选题下载",
            Width = 760,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            FontSize = 14
        };

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "输入脚本，AI 生成搜索关键词，再选择封面下载视频",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var apiUrlBox = new TextBox { Text = apiUrl ?? "" };
        root.Children.Add(FieldRow("API 地址", apiUrlBox, 1));

        var apiKeyBox = new PasswordBox { Password = apiKey ?? "" };
        root.Children.Add(FieldRow("API 密钥", apiKeyBox, 2));

        var modelBox = new TextBox { Text = model ?? "" };
        root.Children.Add(FieldRow("模型 ID", modelBox, 3));

        var promptBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(prompt) ? DefaultPrompt() : prompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10)
        };
        root.Children.Add(TextArea("系统提示词", promptBox, 4));

        var scriptBox = new TextBox
        {
            Text = script ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10)
        };
        root.Children.Add(TextArea("脚本内容", scriptBox, 5));

        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var settings = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var outputBox = new TextBox { Text = outputDir ?? @"D:\chajian\downloads", Width = 310, Height = 32, Margin = new Thickness(0, 0, 12, 0) };
        var maxBox = new TextBox { Text = Math.Max(1, maxPerKeyword).ToString(), Width = 70, Height = 32 };
        settings.Children.Add(new TextBlock { Text = "下载目录", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        settings.Children.Add(outputBox);
        settings.Children.Add(new TextBlock { Text = "每词结果", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        settings.Children.Add(maxBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "取消", Width = 88, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, __) => window.Close();
        var ok = new Button
        {
            Content = "生成关键词",
            Width = 112,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(255, 92, 53)),
            Foreground = Brushes.White
        };
        ok.Click += (_, __) =>
        {
            result = new Dictionary<string, string>
            {
                ["apiUrl"] = apiUrlBox.Text ?? "",
                ["apiKey"] = apiKeyBox.Password ?? "",
                ["model"] = modelBox.Text ?? "",
                ["prompt"] = promptBox.Text ?? "",
                ["script"] = scriptBox.Text ?? "",
                ["outputDir"] = outputBox.Text ?? "",
                ["maxPerKeyword"] = maxBox.Text ?? ""
            };
            window.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Grid.SetColumn(settings, 0);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(settings);
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 6);
        root.Children.Add(bottom);

        window.Content = root;
        window.Loaded += (_, __) =>
        {
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
            scriptBox.Focus();
        };
        window.ShowDialog();
    });
    return result;
}

static Grid FieldRow(string labelText, Control input, int row)
{
    var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var label = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center };
    input.Height = 34;
    input.Padding = new Thickness(10, 4, 10, 4);
    Grid.SetColumn(label, 0);
    Grid.SetColumn(input, 1);
    grid.Children.Add(label);
    grid.Children.Add(input);
    Grid.SetRow(grid, row);
    return grid;
}

static Grid TextArea(string labelText, TextBox box, int row)
{
    var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    var label = new TextBlock { Text = labelText, Margin = new Thickness(0, 0, 0, 6) };
    Grid.SetRow(label, 0);
    Grid.SetRow(box, 1);
    grid.Children.Add(label);
    grid.Children.Add(box);
    Grid.SetRow(grid, row);
    return grid;
}

static List<string> ShowKeywordSelection(List<string> keywords)
{
    List<string> result = null;
    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window
        {
            Title = "选择搜索关键词",
            Width = 420,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            FontSize = 14
        };
        var root = new DockPanel { Margin = new Thickness(20) };
        var title = new TextBlock { Text = "AI 返回以下关键词，请勾选要抓取抖音结果的关键词：", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var checks = new List<CheckBox>();
        var stack = new StackPanel();
        foreach (string keyword in keywords)
        {
            var check = new CheckBox { Content = keyword, IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
            checks.Add(check);
            stack.Children.Add(check);
        }
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var all = new Button { Content = "全选", Width = 72, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        all.Click += (_, __) => checks.ForEach(c => c.IsChecked = true);
        var cancel = new Button { Content = "取消", Width = 72, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, __) => window.Close();
        var ok = new Button { Content = "抓取结果", Width = 96, Height = 32, Background = new SolidColorBrush(Color.FromRgb(255, 92, 53)), Foreground = Brushes.White };
        ok.Click += (_, __) =>
        {
            result = checks.Where(c => c.IsChecked == true).Select(c => Convert.ToString(c.Content)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            window.Close();
        };
        buttons.Children.Add(all);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        window.Content = root;
        window.ShowDialog();
    });
    return result;
}

static List<Dictionary<string, string>> ShowVideoSelection(List<Dictionary<string, string>> items)
{
    List<Dictionary<string, string>> result = null;
    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window
        {
            Title = "选择要下载的视频",
            Width = 980,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            FontSize = 13
        };
        var root = new DockPanel { Margin = new Thickness(18) };
        var title = new TextBlock { Text = $"共抓取到 {items.Count} 个视频结果，请勾选封面后下载：", FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var checks = new List<Tuple<CheckBox, Dictionary<string, string>>>();
        var panel = new WrapPanel();
        foreach (var item in items)
        {
            var check = new CheckBox { IsChecked = false, Margin = new Thickness(0, 2, 6, 0) };
            checks.Add(Tuple.Create(check, item));
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(check);
            header.Children.Add(new TextBlock { Text = "选择", VerticalAlignment = VerticalAlignment.Center });

            var image = new Image { Width = 180, Height = 240, Stretch = Stretch.UniformToFill, Source = CreateBitmap(Get(item, "cover")) };
            var titleText = new TextBlock { Text = Short(Get(item, "title"), 54), TextWrapping = TextWrapping.Wrap, Height = 48, Margin = new Thickness(0, 8, 0, 4) };
            var meta = new TextBlock { Text = $"{Get(item, "sourceKeyword")} · {Get(item, "author")}", TextWrapping = TextWrapping.Wrap, Height = 36, Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)) };
            var card = new StackPanel();
            card.Children.Add(header);
            card.Children.Add(image);
            card.Children.Add(titleText);
            card.Children.Add(meta);

            var border = new Border
            {
                Child = card,
                Width = 212,
                Margin = new Thickness(0, 0, 14, 14),
                Padding = new Thickness(12),
                BorderBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 250))
            };
            border.MouseLeftButtonUp += (_, __) => check.IsChecked = !(check.IsChecked == true);
            panel.Children.Add(border);
        }
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var all = new Button { Content = "全选", Width = 78, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        all.Click += (_, __) => checks.ForEach(t => t.Item1.IsChecked = true);
        var clear = new Button { Content = "清空", Width = 78, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, __) => checks.ForEach(t => t.Item1.IsChecked = false);
        var cancel = new Button { Content = "取消", Width = 78, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, __) => window.Close();
        var ok = new Button { Content = "下载选中", Width = 104, Height = 34, Background = new SolidColorBrush(Color.FromRgb(255, 92, 53)), Foreground = Brushes.White };
        ok.Click += (_, __) =>
        {
            result = checks.Where(t => t.Item1.IsChecked == true).Select(t => t.Item2).ToList();
            window.Close();
        };
        buttons.Children.Add(all);
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        window.Content = root;
        window.ShowDialog();
    });
    return result;
}

static ImageSource CreateBitmap(string url)
{
    if (string.IsNullOrWhiteSpace(url))
    {
        return null;
    }
    try
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(url, UriKind.Absolute);
        bitmap.DecodePixelWidth = 240;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    catch
    {
        return null;
    }
}

static List<string> CallAiForKeywords(string apiUrl, string apiKey, string model, string prompt, string script)
{
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    string body = "{\"model\":\"" + EscapeJson(model) + "\",\"temperature\":0.3,\"messages\":[{\"role\":\"system\",\"content\":\"" + EscapeJson(prompt) + "\"},{\"role\":\"user\",\"content\":\"" + EscapeJson(script) + "\"}]}";
    byte[] payload = Encoding.UTF8.GetBytes(body);
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(apiUrl);
    request.Method = "POST";
    request.ContentType = "application/json";
    request.Accept = "application/json";
    request.Headers["Authorization"] = "Bearer " + apiKey;
    request.Timeout = 60000;
    request.ReadWriteTimeout = 60000;
    request.ContentLength = payload.Length;
    using (Stream stream = request.GetRequestStream())
    {
        stream.Write(payload, 0, payload.Length);
    }

    string responseText;
    try
    {
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream responseStream = response.GetResponseStream())
        using (var reader = new StreamReader(responseStream, Encoding.UTF8))
        {
            responseText = reader.ReadToEnd();
        }
    }
    catch (WebException ex)
    {
        string errorText = "";
        if (ex.Response != null)
        {
            using (Stream responseStream = ex.Response.GetResponseStream())
            using (var reader = new StreamReader(responseStream, Encoding.UTF8))
            {
                errorText = reader.ReadToEnd();
            }
        }
        throw new InvalidOperationException("AI 请求失败：" + ex.Message + (string.IsNullOrWhiteSpace(errorText) ? "" : "\n" + errorText));
    }

    List<string> keywords = ParseKeywords(ExtractAiContent(responseText));
    if (keywords.Count == 0)
    {
        throw new InvalidOperationException("AI 没有返回可解析的关键词。原始响应：" + responseText);
    }
    return keywords;
}

static string ExtractAiContent(string responseText)
{
    Match match = Regex.Match(responseText ?? "", "\"content\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline);
    return match.Success ? UnescapeJson(match.Groups["value"].Value) : responseText ?? "";
}

static List<string> ParseKeywords(string text)
{
    var result = new List<string>();
    string source = (text ?? "").Trim();
    Match array = Regex.Match(source, "\\[[\\s\\S]*?\\]");
    if (array.Success)
    {
        foreach (Match match in Regex.Matches(array.Value, "\"(?<value>(?:\\\\.|[^\"])*)\""))
        {
            AddKeyword(result, UnescapeJson(match.Groups["value"].Value));
        }
    }
    if (result.Count == 0)
    {
        foreach (string part in Regex.Split(source, "[\\r\\n,，;；、]+"))
        {
            AddKeyword(result, Regex.Replace(part, "^\\s*[-*\\d.、)）]+\\s*", ""));
        }
    }
    return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
}

static void AddKeyword(List<string> result, string keyword)
{
    string cleaned = Regex.Replace(keyword ?? "", "\\s+", " ").Trim().Trim('"', '\'', '“', '”', '[', ']');
    if (cleaned.Length > 24)
    {
        cleaned = cleaned.Substring(0, 24);
    }
    if (!string.IsNullOrWhiteSpace(cleaned) && !result.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
    {
        result.Add(cleaned);
    }
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
    MatchCollection pathMatches = Regex.Matches(raw ?? "", "\"path\"\\s*:\\s*\"(?<path>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline);
    foreach (Match pathMatch in pathMatches)
    {
        string extensionPath = pathMatch.Groups["path"].Value.Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\\"", "\"").Trim();
        if (!IsDouyinExtensionPath(extensionPath))
        {
            continue;
        }
        string beforePath = raw.Substring(0, pathMatch.Index);
        MatchCollection idMatches = Regex.Matches(beforePath, "\"(?<id>[a-p]{32})\"\\s*:\\s*\\{", RegexOptions.Singleline);
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
        Process.Start(new ProcessStartInfo { FileName = edgePath, Arguments = $"\"{url}\"", UseShellExecute = true });
        return;
    }
    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}

static string WaitForExportJson(DateTime triggerTime, string keyword, TimeSpan timeout)
{
    List<string> dirs = GetCandidateDownloadDirectories();
    DateTime deadline = DateTime.Now.Add(timeout);
    while (DateTime.Now < deadline)
    {
        FileInfo latest = dirs
            .Where(Directory.Exists)
            .SelectMany(dir =>
            {
                try { return new DirectoryInfo(dir).GetFiles("*.json"); }
                catch { return Array.Empty<FileInfo>(); }
            })
            .Where(file => file.LastWriteTime >= triggerTime)
            .OrderByDescending(file => file.LastWriteTime)
            .FirstOrDefault(file =>
            {
                try
                {
                    string raw = File.ReadAllText(file.FullName, Encoding.UTF8);
                    return raw.Contains("\"videoUrl\"") && (raw.Contains("\"keyword\": \"" + keyword + "\"") || raw.Contains("\"keyword\":\"" + keyword + "\""));
                }
                catch { return false; }
            });
        if (latest != null)
        {
            return latest.FullName;
        }
        System.Threading.Thread.Sleep(1000);
    }
    throw new TimeoutException("Timed out waiting for export json: " + keyword);
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
        if (!dirs.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            dirs.Add(normalized);
        }
        string jsonDir = Path.Combine(normalized, "douyin-json");
        if (!dirs.Any(x => string.Equals(x, jsonDir, StringComparison.OrdinalIgnoreCase)))
        {
            dirs.Add(jsonDir);
        }
    }

    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    AddDir(Path.Combine(userProfile, "Downloads"));
    AddDir(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    foreach (string prefsPath in GetBrowserPreferencePaths().Where(File.Exists))
    {
        string raw = File.ReadAllText(prefsPath, Encoding.UTF8);
        foreach (Match match in Regex.Matches(raw, "\"default_directory\"\\s*:\\s*\"(?<path>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline))
        {
            AddDir(match.Groups["path"].Value.Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\\"", "\""));
        }
    }
    return dirs;
}

static List<Dictionary<string, string>> ExtractItems(string rawText, string keyword)
{
    var items = new List<Dictionary<string, string>>();
    MatchCollection matches = Regex.Matches(rawText ?? "", "\"videoId\"\\s*:\\s*\"(?<videoId>\\d+)\"");
    for (int i = 0; i < matches.Count; i++)
    {
        int start = matches[i].Index;
        int end = i + 1 < matches.Count ? matches[i + 1].Index : rawText.Length;
        string block = rawText.Substring(start, end - start);
        string videoId = ExtractField(block, "videoId");
        string videoUrl = ExtractField(block, "videoUrl");
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(videoUrl))
        {
            continue;
        }
        items.Add(new Dictionary<string, string>
        {
            ["videoId"] = videoId,
            ["detailUrl"] = ExtractField(block, "detailUrl"),
            ["videoUrl"] = videoUrl,
            ["author"] = ExtractField(block, "author"),
            ["title"] = ExtractField(block, "title"),
            ["cover"] = ExtractField(block, "cover"),
            ["sourceKeyword"] = keyword,
            ["rawObject"] = block.Trim()
        });
    }
    return items;
}

static string ExtractField(string rawObject, string fieldName)
{
    Match match = Regex.Match(rawObject ?? "", $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Singleline);
    return match.Success ? UnescapeJson(match.Groups["value"].Value) : "";
}

static Dictionary<string, object> CreateProgressWindow(int total)
{
    var state = new Dictionary<string, object>();
    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window { Title = "下载进度", Width = 560, Height = 190, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brushes.White, Topmost = true };
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { Text = $"准备下载 0 / {total}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        var detail = new TextBlock { Text = "等待开始...", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 22, Value = 0 };
        Grid.SetRow(title, 0);
        Grid.SetRow(detail, 1);
        Grid.SetRow(bar, 2);
        root.Children.Add(title);
        root.Children.Add(detail);
        root.Children.Add(bar);
        window.Content = root;
        window.Show();
        state["window"] = window;
        state["title"] = title;
        state["detail"] = detail;
        state["bar"] = bar;
    });
    return state;
}

static void UpdateProgress(Dictionary<string, object> progress, int done, int total, string name)
{
    if (progress == null) return;
    Application.Current.Dispatcher.Invoke(() =>
    {
        var title = progress["title"] as TextBlock;
        var detail = progress["detail"] as TextBlock;
        var bar = progress["bar"] as ProgressBar;
        if (title != null) title.Text = $"下载进度 {done} / {total}";
        if (detail != null) detail.Text = string.IsNullOrWhiteSpace(name) ? "正在准备下载..." : $"当前文件：{name}";
        if (bar != null) bar.Value = total <= 0 ? 0 : Math.Min(100, done * 100.0 / total);
    });
}

static void UpdateProgressValue(Dictionary<string, object> progress, int index, int total, string name, long received, long totalBytes)
{
    if (progress == null) return;
    Application.Current.Dispatcher.Invoke(() =>
    {
        var title = progress["title"] as TextBlock;
        var detail = progress["detail"] as TextBlock;
        var bar = progress["bar"] as ProgressBar;
        double ratio = totalBytes > 0 ? Math.Min(1.0, received / (double)totalBytes) : 0;
        if (title != null) title.Text = $"下载进度 {index + 1} / {total}";
        if (detail != null) detail.Text = $"当前文件：{name}\n已下载：{FormatBytes(received)}" + (totalBytes > 0 ? $" / {FormatBytes(totalBytes)}" : "");
        if (bar != null) bar.Value = total <= 0 ? 0 : Math.Min(100, Math.Max(0, ((index + ratio) / total) * 100));
    });
}

static void CloseProgressWindow(Dictionary<string, object> progress)
{
    if (progress == null) return;
    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = progress.ContainsKey("window") ? progress["window"] as Window : null;
        if (window != null) window.Close();
    });
}

static void DownloadVideo(string url, string referer, string destination, Action<long, long> progress)
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
    using (Stream source = response.GetResponseStream())
    using (FileStream target = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        long total = response.ContentLength > 0 ? response.ContentLength : -1;
        long received = 0;
        byte[] buffer = new byte[1024 * 256];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);
            received += read;
            progress?.Invoke(received, total);
        }
    }
}

static string BuildBaseFileName(string keyword, Dictionary<string, string> item)
{
    string text = $"{keyword}-{Get(item, "title")}";
    if (string.IsNullOrWhiteSpace(Get(item, "title"))) text = $"{keyword}-{Get(item, "videoId")}";
    string name = text.Normalize(NormalizationForm.FormKC);
    foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    name = Regex.Replace(name, "\\s+", "_").Trim('_');
    if (string.IsNullOrWhiteSpace(name)) name = Get(item, "videoId");
    return name.Length > 90 ? name.Substring(0, 90) : name;
}

static void WriteMetadataJson(Dictionary<string, string> item, string destination)
{
    string raw = IsJsonObject(Get(item, "rawObject")) ? Get(item, "rawObject") : "{}";
    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"sourceKeyword\": \"" + EscapeJson(Get(item, "sourceKeyword")) + "\",");
    sb.AppendLine("  \"sourceExportJson\": \"" + EscapeJson(Get(item, "sourceJsonPath")) + "\",");
    sb.AppendLine("  \"savedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",");
    sb.AppendLine("  \"videoId\": \"" + EscapeJson(Get(item, "videoId")) + "\",");
    sb.AppendLine("  \"title\": \"" + EscapeJson(Get(item, "title")) + "\",");
    sb.AppendLine("  \"author\": \"" + EscapeJson(Get(item, "author")) + "\",");
    sb.AppendLine("  \"cover\": \"" + EscapeJson(Get(item, "cover")) + "\",");
    sb.AppendLine("  \"detailUrl\": \"" + EscapeJson(Get(item, "detailUrl")) + "\",");
    sb.AppendLine("  \"videoUrl\": \"" + EscapeJson(Get(item, "videoUrl")) + "\",");
    sb.AppendLine("  \"originalRecord\": " + raw);
    sb.AppendLine("}");
    File.WriteAllText(destination, sb.ToString(), new UTF8Encoding(false));
}

static bool IsJsonObject(string text)
{
    string value = (text ?? "").Trim();
    return value.StartsWith("{") && value.EndsWith("}");
}

static string ReadVar(IStepContext context, string key, string fallback)
{
    string value = context.GetVarValue(key) as string;
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static int ToInt(object value, int fallback)
{
    if (value == null) return fallback;
    int parsed;
    return int.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
}

static string Get(Dictionary<string, string> item, string key)
{
    return item != null && item.ContainsKey(key) ? item[key] ?? "" : "";
}

static string Short(string value, int max)
{
    string text = string.IsNullOrWhiteSpace(value) ? "无标题" : value.Trim();
    return text.Length <= max ? text : text.Substring(0, max) + "...";
}

static string FormatBytes(long bytes)
{
    string[] units = new[] { "B", "KB", "MB", "GB" };
    double value = Math.Max(0, bytes);
    int index = 0;
    while (value >= 1024 && index < units.Length - 1)
    {
        value /= 1024;
        index++;
    }
    return $"{value:0.##} {units[index]}";
}

static string EscapeJson(string value)
{
    if (string.IsNullOrEmpty(value)) return "";
    var sb = new StringBuilder();
    foreach (char c in value)
    {
        if (c == '\\') sb.Append("\\\\");
        else if (c == '"') sb.Append("\\\"");
        else if (c == '\r') sb.Append("\\r");
        else if (c == '\n') sb.Append("\\n");
        else if (c == '\t') sb.Append("\\t");
        else if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
        else sb.Append(c);
    }
    return sb.ToString();
}

static string UnescapeJson(string value)
{
    if (string.IsNullOrEmpty(value)) return "";
    return Regex.Unescape(value).Replace("\\/", "/").Replace("\\\"", "\"").Replace("\\\\", "\\");
}

static string DefaultPrompt()
{
    return "你是短视频搜索选题助手。请根据用户脚本提炼适合在抖音搜索素材的中文关键词。要求：1. 返回 6 到 12 个关键词；2. 每个关键词 2 到 12 个中文字符，尽量贴近真实用户搜索；3. 不要解释；4. 只返回 JSON 字符串数组，例如 [\"糖尿病饮食\",\"血糖控制\"]。";
}
