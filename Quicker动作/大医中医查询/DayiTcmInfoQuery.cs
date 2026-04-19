using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Quicker.Public;

public static string Exec(IStepContext context)
{
    try
    {
        ServicePointManager.SecurityProtocol =
            (SecurityProtocolType)3072 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

        string defaultKeyword = (context.GetVarValue("keyword") as string ?? "").Trim();
        if (string.IsNullOrWhiteSpace(defaultKeyword))
        {
            defaultKeyword = "\u7cd6\u5c3f\u75c5";
        }

        string defaultCategory = NormalizeCategory(context.GetVarValue("category") as string);
        string history = context.GetVarValue("history") as string ?? "";
        Dictionary<string, string> formats = LoadOutputFormats(context);
        string lastOutput = "";

        while (true)
        {
            Dictionary<string, string> query = ShowQueryDialog(defaultKeyword, defaultCategory, history, formats);
            if (query == null)
            {
                return string.IsNullOrWhiteSpace(lastOutput) ? "CANCELLED" : lastOutput;
            }

            string keyword = (query["keyword"] ?? "").Trim();
            string category = NormalizeCategory(query["category"]);
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return "ERROR: keyword is required.";
            }

            context.SetVarValue("keyword", keyword);
            context.SetVarValue("category", category);
            formats["disease"] = query["diseaseFormat"];
            formats["medical"] = query["medicalFormat"];
            formats["cmedical"] = query["cmedicalFormat"];
            SaveOutputFormats(context, formats);
            string outputFormat = GetOutputFormatForCategory(category, formats);

            string searchUrl = "https://www.dayi.org.cn/search?keyword=" + Uri.EscapeDataString(keyword) + "&type=" + GetSearchType(category);
            string searchHtml = HttpGet(searchUrl);
            Dictionary<string, string> result = FindSearchResult(searchHtml, keyword, category);
            if (result == null)
            {
                string message = "ERROR: no " + GetCategoryLabel(category) + " result found for " + keyword;
                context.SetVarValue("errMessage", message);
                return message;
            }

            string detailUrl = MakeAbsoluteUrl(result["href"]);
            string detailHtml = HttpGet(detailUrl);
            Dictionary<string, string> extracted = ExtractContentByCategory(detailHtml, keyword, category);

            string output = BuildOutput(outputFormat, keyword, category, searchUrl, detailUrl, result["title"], extracted);
            context.SetVarValue("rtn", output);
            context.SetVarValue("text", output);
            context.SetVarValue("rawContent", extracted["rawContent"]);
            context.SetVarValue("searchUrl", searchUrl);
            context.SetVarValue("detailUrl", detailUrl);
            context.SetVarValue("matchedTitle", result["title"]);
            history = UpdateHistory(history, keyword, category, extracted["content"]);
            context.SetVarValue("history", history);
            context.SetVarValue("errMessage", "");

            Application.Current.Dispatcher.Invoke(() =>
            {
                ShowResultWindow(output, extracted["rawContent"]);
            });

            defaultKeyword = keyword;
            defaultCategory = category;
            lastOutput = output;
        }
    }
    catch (Exception ex)
    {
        context.SetVarValue("errMessage", ex.ToString());
        return "ERROR: " + ex.Message;
    }
}

static List<Dictionary<string, string>> ParseHistory(string history)
{
    var entries = new List<Dictionary<string, string>>();
    foreach (string line in Regex.Split(history ?? "", "\\r?\\n"))
    {
        string trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            continue;
        }

        string[] parts = trimmed.Split(new char[] { '\t' }, 3);
        if (parts.Length < 2)
        {
            continue;
        }

        string category = NormalizeCategory(parts[0]);
        string keyword = (parts[1] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            continue;
        }

        var item = new Dictionary<string, string>();
        item["category"] = category;
        item["keyword"] = keyword;
        item["content"] = parts.Length >= 3 ? (parts[2] ?? "").Trim() : "";
        entries.Add(item);
    }

    return entries;
}

static string UpdateHistory(string history, string keyword, string category, string content)
{
    string cleanKeyword = CleanHistoryValue(keyword);
    string cleanCategory = NormalizeCategory(category);
    string cleanContent = CleanHistoryValue(content);
    if (string.IsNullOrWhiteSpace(cleanKeyword))
    {
        return history ?? "";
    }

    var entries = ParseHistory(history)
        .Where(item => !(string.Equals(item["keyword"], cleanKeyword, StringComparison.OrdinalIgnoreCase)
            && item["category"] == cleanCategory))
        .ToList();

    var first = new Dictionary<string, string>();
    first["category"] = cleanCategory;
    first["keyword"] = cleanKeyword;
    first["content"] = cleanContent;
    entries.Insert(0, first);

    return string.Join(Environment.NewLine, entries
        .Take(50)
        .Select(item => item["category"] + "\t" + CleanHistoryValue(item["keyword"]) + "\t" + CleanHistoryValue(GetDictValue(item, "content"))));
}

static string CleanHistoryValue(string value)
{
    return Regex.Replace(value ?? "", "[\\t\\r\\n]+", " ").Trim();
}

static string GetDictValue(Dictionary<string, string> dict, string key)
{
    return dict != null && dict.ContainsKey(key) ? dict[key] ?? "" : "";
}

static string MakeHistoryDisplayText(Dictionary<string, string> item)
{
    string text = GetCategoryLabel(item["category"]) + " | " + item["keyword"];
    string content = GetDictValue(item, "content");
    if (!string.IsNullOrWhiteSpace(content))
    {
        text += " | " + TruncateText(content, 42);
    }
    return text;
}

static string TruncateText(string text, int maxLength)
{
    string value = Regex.Replace(text ?? "", "\\s+", " ").Trim();
    if (value.Length <= maxLength)
    {
        return value;
    }
    return value.Substring(0, maxLength) + "...";
}

static Dictionary<string, string> LoadOutputFormats(IStepContext context)
{
    var formats = new Dictionary<string, string>();
    formats["disease"] = (context.GetVarValue("diseaseFormat") as string ?? "").Trim();
    formats["medical"] = (context.GetVarValue("medicalFormat") as string ?? "").Trim();
    formats["cmedical"] = (context.GetVarValue("cmedicalFormat") as string ?? "").Trim();

    string oldFormat = (context.GetVarValue("outputFormat") as string ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(oldFormat) && !IsBuiltInDefaultFormat(oldFormat))
    {
        string category = NormalizeCategory(context.GetVarValue("category") as string);
        if (string.IsNullOrWhiteSpace(formats[category]))
        {
            formats[category] = oldFormat;
        }
    }

    if (string.IsNullOrWhiteSpace(formats["disease"])) formats["disease"] = GetDefaultOutputFormat("disease");
    if (string.IsNullOrWhiteSpace(formats["medical"])) formats["medical"] = GetDefaultOutputFormat("medical");
    if (string.IsNullOrWhiteSpace(formats["cmedical"])) formats["cmedical"] = GetDefaultOutputFormat("cmedical");
    return formats;
}

static void SaveOutputFormats(IStepContext context, Dictionary<string, string> formats)
{
    context.SetVarValue("diseaseFormat", formats["disease"] ?? GetDefaultOutputFormat("disease"));
    context.SetVarValue("medicalFormat", formats["medical"] ?? GetDefaultOutputFormat("medical"));
    context.SetVarValue("cmedicalFormat", formats["cmedical"] ?? GetDefaultOutputFormat("cmedical"));
    context.SetVarValue("outputFormat", "");
}

static string GetOutputFormatForCategory(string category, Dictionary<string, string> formats)
{
    string key = NormalizeCategory(category);
    if (formats != null && formats.ContainsKey(key) && !string.IsNullOrWhiteSpace(formats[key]))
    {
        return formats[key];
    }
    return GetDefaultOutputFormat(key);
}

static Dictionary<string, string> ShowQueryDialog(string defaultKeyword, string defaultCategory, string history, Dictionary<string, string> formats)
{
    Dictionary<string, string> result = null;
    List<Dictionary<string, string>> historyEntries = ParseHistory(history);
    Dictionary<string, string> localFormats = new Dictionary<string, string>();
    localFormats["disease"] = formats["disease"];
    localFormats["medical"] = formats["medical"];
    localFormats["cmedical"] = formats["cmedical"];

    Application.Current.Dispatcher.Invoke(() =>
    {
        var window = new Window
        {
            Title = "\u4e2d\u533b\u836f\u4fe1\u606f\u67e5\u8be2",
            Width = 520,
            Height = 292,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            FontSize = 14
        };

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "\u8f93\u5165\u5173\u952e\u8bcd\uff0c\u9009\u62e9\u5206\u7c7b\u540e\u63d0\u53d6\u5bf9\u5e94\u5185\u5bb9",
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var keywordBox = new TextBox
        {
            Text = defaultKeyword ?? "",
            Height = 34,
            Padding = new Thickness(10, 4, 10, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(keywordBox, 1);
        root.Children.Add(keywordBox);

        var categoryPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        categoryPanel.Children.Add(new TextBlock
        {
            Text = "\u5206\u7c7b\uff1a",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var categoryBox = new ComboBox
        {
            Width = 160,
            Height = 34,
            IsEditable = false
        };
        categoryBox.Items.Add("\u75be\u75c5");
        categoryBox.Items.Add("\u836f\u54c1");
        categoryBox.Items.Add("\u4e2d\u836f\u6750");
        categoryBox.SelectedItem = GetCategoryLabel(defaultCategory);
        categoryPanel.Children.Add(categoryBox);
        Grid.SetRow(categoryPanel, 2);
        root.Children.Add(categoryPanel);

        var historyPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        historyPanel.Children.Add(new TextBlock
        {
            Text = "\u5386\u53f2\uff1a",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var historyBox = new ComboBox
        {
            Width = 390,
            Height = 34,
            IsEditable = false
        };
        foreach (Dictionary<string, string> item in historyEntries)
        {
            historyBox.Items.Add(MakeHistoryDisplayText(item));
        }
        historyBox.SelectionChanged += (_, __) =>
        {
            int index = historyBox.SelectedIndex;
            if (index >= 0 && index < historyEntries.Count)
            {
                Dictionary<string, string> item = historyEntries[index];
                keywordBox.Text = item["keyword"];
                categoryBox.SelectedItem = GetCategoryLabel(item["category"]);
            }
        };
        historyPanel.Children.Add(historyBox);
        Grid.SetRow(historyPanel, 3);
        root.Children.Add(historyPanel);

        Action submit = () =>
        {
            result = new Dictionary<string, string>();
            result["keyword"] = keywordBox.Text ?? "";
            result["category"] = NormalizeCategory(categoryBox.SelectedItem as string);
            result["diseaseFormat"] = localFormats["disease"];
            result["medicalFormat"] = localFormats["medical"];
            result["cmedicalFormat"] = localFormats["cmedical"];
            window.Close();
        };

        keywordBox.KeyDown += (sender, args) =>
        {
            if (args.Key == Key.Enter)
            {
                submit();
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "\u53d6\u6d88",
            Width = 88,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelButton.Click += (_, __) => window.Close();

        var configButton = new Button
        {
            Content = "\u6a21\u677f\u914d\u7f6e",
            Width = 100,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0)
        };
        configButton.Click += (_, __) => ShowTemplateConfigWindow(localFormats);

        var okButton = new Button
        {
            Content = "\u5f00\u59cb\u67e5\u8be2",
            Width = 100,
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(32, 126, 89)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(32, 126, 89))
        };
        okButton.Click += (_, __) => submit();

        buttons.Children.Add(configButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, __) =>
        {
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
            keywordBox.Focus();
            keywordBox.SelectAll();
        };
        window.ShowDialog();
    });

    return result;
}

static void ShowTemplateConfigWindow(Dictionary<string, string> formats)
{
    var window = new Window
    {
        Title = "\u8f93\u51fa\u6a21\u677f\u914d\u7f6e",
        Width = 760,
        Height = 520,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Background = Brushes.White,
        FontSize = 14
    };

    var root = new Grid { Margin = new Thickness(16) };
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var hint = new TextBlock
    {
        Text = "\u53ef\u7528\u5360\u4f4d\u7b26\uff1a{keyword}, {category}, {title}, {field_name}, {content}, {raw_content}, {items}, {items_with_index}, {search_url}, {detail_url}, {newline}",
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12)
    };
    Grid.SetRow(hint, 0);
    root.Children.Add(hint);

    var panel = new Grid();
    panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    var diseaseBox = AddTemplateEditor(panel, 0, "\u75be\u75c5\u6a21\u677f", formats["disease"]);
    var medicalBox = AddTemplateEditor(panel, 2, "\u836f\u54c1\u6a21\u677f", formats["medical"]);
    var cmedicalBox = AddTemplateEditor(panel, 4, "\u4e2d\u836f\u6750\u6a21\u677f", formats["cmedical"]);

    Grid.SetRow(panel, 1);
    root.Children.Add(panel);

    var buttons = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 12, 0, 0)
    };

    var resetButton = new Button
    {
        Content = "\u6062\u590d\u9ed8\u8ba4",
        Width = 100,
        Height = 34,
        Margin = new Thickness(0, 0, 10, 0)
    };
    resetButton.Click += (_, __) =>
    {
        diseaseBox.Text = GetDefaultOutputFormat("disease");
        medicalBox.Text = GetDefaultOutputFormat("medical");
        cmedicalBox.Text = GetDefaultOutputFormat("cmedical");
    };

    var cancelButton = new Button
    {
        Content = "\u53d6\u6d88",
        Width = 88,
        Height = 34,
        Margin = new Thickness(0, 0, 10, 0)
    };
    cancelButton.Click += (_, __) => window.Close();

    var saveButton = new Button
    {
        Content = "\u4fdd\u5b58",
        Width = 88,
        Height = 34,
        Background = new SolidColorBrush(Color.FromRgb(32, 126, 89)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(32, 126, 89))
    };
    saveButton.Click += (_, __) =>
    {
        formats["disease"] = string.IsNullOrWhiteSpace(diseaseBox.Text) ? GetDefaultOutputFormat("disease") : diseaseBox.Text.Trim();
        formats["medical"] = string.IsNullOrWhiteSpace(medicalBox.Text) ? GetDefaultOutputFormat("medical") : medicalBox.Text.Trim();
        formats["cmedical"] = string.IsNullOrWhiteSpace(cmedicalBox.Text) ? GetDefaultOutputFormat("cmedical") : cmedicalBox.Text.Trim();
        window.Close();
    };

    buttons.Children.Add(resetButton);
    buttons.Children.Add(cancelButton);
    buttons.Children.Add(saveButton);
    Grid.SetRow(buttons, 2);
    root.Children.Add(buttons);

    window.Content = root;
    window.Loaded += (_, __) =>
    {
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    };
    window.ShowDialog();
}

static TextBox AddTemplateEditor(Grid panel, int row, string label, string text)
{
    var title = new TextBlock
    {
        Text = label,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 6)
    };
    Grid.SetRow(title, row);
    panel.Children.Add(title);

    var box = new TextBox
    {
        Text = text ?? "",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(8),
        MinHeight = 72
    };
    Grid.SetRow(box, row + 1);
    panel.Children.Add(box);
    return box;
}

static void ShowResultWindow(string output, string rawContent)
{
    var window = new Window
    {
        Title = "\u67e5\u8be2\u7ed3\u679c",
        Width = 720,
        Height = 430,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Background = Brushes.White,
        FontSize = 14
    };

    var root = new Grid { Margin = new Thickness(16) };
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var resultBox = new TextBox
    {
        Text = output ?? "",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsReadOnly = false,
        Padding = new Thickness(10)
    };
    Grid.SetRow(resultBox, 0);
    root.Children.Add(resultBox);

    var buttons = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 12, 0, 0)
    };

    var copyButton = new Button
    {
        Content = "\u590d\u5236\u7ed3\u679c",
        Width = 100,
        Height = 34,
        Margin = new Thickness(0, 0, 10, 0)
    };
    copyButton.Click += (_, __) =>
    {
        CopyTextBoxContent(copyButton, resultBox, "\u590d\u5236\u7ed3\u679c");
    };

    var rawButton = new Button
    {
        Content = "\u539f\u59cb\u5185\u5bb9",
        Width = 100,
        Height = 34,
        Margin = new Thickness(0, 0, 10, 0)
    };
    rawButton.Click += (_, __) => ShowRawWindow(rawContent ?? "");

    var closeButton = new Button
    {
        Content = "\u5173\u95ed",
        Width = 88,
        Height = 34
    };
    closeButton.Click += (_, __) => window.Close();

    buttons.Children.Add(copyButton);
    buttons.Children.Add(rawButton);
    buttons.Children.Add(closeButton);
    Grid.SetRow(buttons, 1);
    root.Children.Add(buttons);

    window.Content = root;
    window.Loaded += (_, __) =>
    {
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        resultBox.Focus();
        resultBox.SelectAll();
    };
    window.ShowDialog();
}

static void ShowRawWindow(string rawContent)
{
    var window = new Window
    {
        Title = "\u539f\u59cb\u5185\u5bb9",
        Width = 760,
        Height = 500,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Background = Brushes.White,
        FontSize = 14
    };

    var root = new Grid { Margin = new Thickness(16) };
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    var rawBox = new TextBox
    {
        Text = rawContent ?? "",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsReadOnly = false,
        Padding = new Thickness(10)
    };
    Grid.SetRow(rawBox, 0);
    root.Children.Add(rawBox);

    var buttons = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 12, 0, 0)
    };

    var copyButton = new Button
    {
        Content = "\u590d\u5236\u539f\u6587",
        Width = 100,
        Height = 34,
        Margin = new Thickness(0, 0, 10, 0)
    };
    copyButton.Click += (_, __) =>
    {
        CopyTextBoxContent(copyButton, rawBox, "\u590d\u5236\u539f\u6587");
    };

    var closeButton = new Button
    {
        Content = "\u5173\u95ed",
        Width = 88,
        Height = 34
    };
    closeButton.Click += (_, __) => window.Close();

    buttons.Children.Add(copyButton);
    buttons.Children.Add(closeButton);
    Grid.SetRow(buttons, 1);
    root.Children.Add(buttons);

    window.Content = root;
    window.Loaded += (_, __) =>
    {
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        rawBox.Focus();
        rawBox.SelectAll();
    };
    window.ShowDialog();
}

static void CopyTextBoxContent(Button button, TextBox textBox, string normalText)
{
    try
    {
        textBox.Focus();
        textBox.SelectAll();
        textBox.Copy();
        button.Content = "\u5df2\u590d\u5236";
    }
    catch
    {
        button.Content = "\u590d\u5236\u5931\u8d25";
    }

    var resetTimer = new System.Windows.Threading.DispatcherTimer();
    resetTimer.Interval = TimeSpan.FromMilliseconds(1200);
    resetTimer.Tick += (sender, args) =>
    {
        resetTimer.Stop();
        button.Content = normalText;
    };
    resetTimer.Start();
}

static string HttpGet(string url)
{
    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
    request.Method = "GET";
    request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
    request.Headers["Cache-Control"] = "no-cache";
    request.Timeout = 30000;
    request.ReadWriteTimeout = 30000;

    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
    using (Stream stream = response.GetResponseStream())
    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
    {
        return reader.ReadToEnd();
    }
}

static Dictionary<string, string> FindSearchResult(string html, string keyword, string category)
{
    var candidates = new List<Dictionary<string, string>>();
    string pattern = "<a\\s+href=\"(?<href>/(?:disease|drug|cmedical)/\\d+\\.html)\"[^>]*>(?<title>.*?)</a>\\s*<span[^>]*>\\s*-\\s*(?<label>.*?)\\s*</span>";
    string pathPrefix = GetPathPrefix(category);

    foreach (Match match in Regex.Matches(html ?? "", pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase))
    {
        string title = CleanHtml(match.Groups["title"].Value);
        string href = match.Groups["href"].Value;
        string label = CleanHtml(match.Groups["label"].Value).TrimStart('-').Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href))
        {
            continue;
        }
        if (!href.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) || !IsExpectedResultLabel(category, label))
        {
            continue;
        }

        var item = new Dictionary<string, string>();
        item["title"] = title;
        item["href"] = href;
        item["label"] = label;
        candidates.Add(item);
    }

    Dictionary<string, string> exact = candidates
        .FirstOrDefault(item => string.Equals(item["title"], keyword, StringComparison.OrdinalIgnoreCase));
    if (exact != null)
    {
        return exact;
    }

    return candidates.FirstOrDefault();
}

static Dictionary<string, string> ExtractContentByCategory(string html, string keyword, string category)
{
    if (category == "medical")
    {
        string raw = ExtractFieldTextById(html, "indication");
        return MakeExtracted("\u9002\u5e94\u75c7", raw, raw, "", "");
    }

    if (category == "cmedical")
    {
        string raw = ExtractFieldTextById(html, "processing");
        string selected = SelectProcessingLine(raw, keyword);
        return MakeExtracted("\u70ae\u5236\u65b9\u6cd5", selected, raw, "", "");
    }

    List<string> lines = ExtractParagraphLines(ExtractFieldHtmlById(html, "differentialDiagnosis"));
    var titles = new List<string>();
    foreach (string line in lines)
    {
        string title = ExtractNumberedTitle(line);
        if (!string.IsNullOrWhiteSpace(title) && !titles.Contains(title))
        {
            titles.Add(title);
        }
    }

    string rawContent = string.Join(Environment.NewLine, lines);
    string items = string.Join("\u3001", titles);
    var indexed = new List<string>();
    for (int i = 0; i < titles.Count; i++)
    {
        indexed.Add((i + 1) + "\u3001" + titles[i]);
    }

    return MakeExtracted("\u9274\u522b\u8bca\u65ad", items, rawContent, items, string.Join(Environment.NewLine, indexed));
}

static Dictionary<string, string> MakeExtracted(string fieldName, string content, string rawContent, string items, string itemsWithIndex)
{
    var dict = new Dictionary<string, string>();
    dict["fieldName"] = fieldName ?? "";
    dict["content"] = content ?? "";
    dict["rawContent"] = rawContent ?? "";
    dict["items"] = items ?? "";
    dict["items_with_index"] = itemsWithIndex ?? "";
    return dict;
}

static string ExtractFieldTextById(string html, string id)
{
    return string.Join(Environment.NewLine, ExtractParagraphLines(ExtractFieldHtmlById(html, id)));
}

static string ExtractFieldHtmlById(string html, string id)
{
    if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(id))
    {
        return "";
    }

    int start = html.IndexOf("id=\"" + id + "\"", StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
        start = html.IndexOf("id='" + id + "'", StringComparison.OrdinalIgnoreCase);
    }
    if (start < 0)
    {
        return "";
    }

    int contentStart = html.IndexOf("field-content", start, StringComparison.OrdinalIgnoreCase);
    if (contentStart < 0)
    {
        return "";
    }

    int nextField = html.IndexOf("field-container", contentStart + 20, StringComparison.OrdinalIgnoreCase);
    int nextTwoTitle = html.IndexOf("two-title", contentStart + 20, StringComparison.OrdinalIgnoreCase);
    int nextLongTitle = html.IndexOf("<h2", contentStart + 20, StringComparison.OrdinalIgnoreCase);
    int end = MinPositive(nextField, nextTwoTitle, nextLongTitle);
    if (end < 0)
    {
        end = Math.Min(html.Length, contentStart + 10000);
    }

    return html.Substring(contentStart, end - contentStart);
}

static int MinPositive(params int[] values)
{
    int min = -1;
    foreach (int value in values)
    {
        if (value >= 0 && (min < 0 || value < min))
        {
            min = value;
        }
    }
    return min;
}

static List<string> ExtractParagraphLines(string html)
{
    var lines = new List<string>();
    foreach (Match match in Regex.Matches(html ?? "", "<p[^>]*>(?<line>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
    {
        string line = CleanHtml(match.Groups["line"].Value);
        if (!string.IsNullOrWhiteSpace(line))
        {
            lines.Add(line);
        }
    }

    if (lines.Count == 0)
    {
        string text = CleanHtml(html ?? "");
        if (!string.IsNullOrWhiteSpace(text))
        {
            lines.Add(text);
        }
    }

    return lines;
}

static string ExtractNumberedTitle(string line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return "";
    }

    Match match = Regex.Match(line.Trim(), "^\\s*(?:\\(?\\d+\\)?|[\\uff08]\\d+[\\uff09])\\s*[\\u3001\\.\\uff0e]?\\s*(?<title>[^\\u3002\\uff1b;\\uff0c,\\uff1a:]+)");
    return match.Success ? match.Groups["title"].Value.Trim() : "";
}

static string SelectProcessingLine(string raw, string keyword)
{
    var lines = new List<string>();
    foreach (string line in Regex.Split(raw ?? "", "\\r?\\n"))
    {
        string trimmed = line.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            lines.Add(trimmed);
        }
    }

    if (lines.Count == 0)
    {
        return "";
    }

    string selected = lines.First();
    selected = Regex.Replace(selected, "^\\s*\\d+[\\u3001\\.\\uff0e]\\s*", "").Trim();

    string name = (keyword ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(name))
    {
        selected = Regex.Replace(selected, "^" + Regex.Escape(name) + "\\s*[\\uff1a:]\\s*", "").Trim();
    }

    int chineseColon = selected.IndexOf('\uff1a');
    int englishColon = selected.IndexOf(':');
    int colon = MinPositive(chineseColon, englishColon);
    if (colon >= 0 && colon + 1 < selected.Length)
    {
        selected = selected.Substring(colon + 1).Trim();
    }

    return selected;
}

static string BuildOutput(string outputFormat, string keyword, string category, string searchUrl, string detailUrl, string title, Dictionary<string, string> extracted)
{
    string template = string.IsNullOrWhiteSpace(outputFormat) ? GetDefaultOutputFormat(category) : outputFormat;
    string items = extracted.ContainsKey("items") && !string.IsNullOrWhiteSpace(extracted["items"])
        ? extracted["items"]
        : extracted["content"];

    return template
        .Replace("{keyword}", keyword ?? "")
        .Replace("{category}", GetCategoryLabel(category))
        .Replace("{title}", title ?? "")
        .Replace("{field_name}", extracted["fieldName"] ?? "")
        .Replace("{content}", extracted["content"] ?? "")
        .Replace("{raw}", extracted["content"] ?? "")
        .Replace("{raw_content}", extracted["rawContent"] ?? "")
        .Replace("{items}", items ?? "")
        .Replace("{items_with_index}", extracted["items_with_index"] ?? "")
        .Replace("{search_url}", searchUrl ?? "")
        .Replace("{detail_url}", detailUrl ?? "")
        .Replace("{newline}", Environment.NewLine)
        .Trim();
}

static string NormalizeCategory(string value)
{
    string v = (value ?? "").Trim().ToLowerInvariant();
    if (v == "medical" || v == "\u836f\u54c1" || v == "\u897f\u836f")
    {
        return "medical";
    }
    if (v == "cmedical" || v == "\u4e2d\u836f\u6750")
    {
        return "cmedical";
    }
    return "disease";
}

static string GetSearchType(string category)
{
    if (category == "medical") return "medical";
    if (category == "cmedical") return "cmedical";
    return "disease";
}

static string GetPathPrefix(string category)
{
    if (category == "medical") return "/drug/";
    if (category == "cmedical") return "/cmedical/";
    return "/disease/";
}

static string GetCategoryLabel(string category)
{
    if (category == "medical") return "\u836f\u54c1";
    if (category == "cmedical") return "\u4e2d\u836f\u6750";
    return "\u75be\u75c5";
}

static bool IsExpectedResultLabel(string category, string label)
{
    string value = (label ?? "").Trim();
    if (category == "medical")
    {
        return value == "\u897f\u836f" || value == "\u4e2d\u6210\u836f" || value == "\u836f\u54c1";
    }
    if (category == "cmedical")
    {
        return value == "\u4e2d\u836f\u6750";
    }
    return value == "\u75be\u75c5";
}

static string GetDefaultOutputFormat(string category)
{
    if (category == "medical")
    {
        return "{keyword}\u9002\u5e94\u75c7\uff1a{content}";
    }
    if (category == "cmedical")
    {
        return "{keyword}\u70ae\u5236\uff1a{content}";
    }
    return "{keyword}\u9274\u522b\u8bca\u65ad\uff1a\u9700\u4e0e{items}\u76f8\u9274\u522b";
}

static bool IsBuiltInDefaultFormat(string value)
{
    string v = (value ?? "").Trim();
    return v == "{keyword}\u9274\u522b\u8bca\u65ad\uff1a\u9700\u4e0e{items}\u76f8\u9274\u522b"
        || v == "{keyword}\u9002\u5e94\u75c7\uff1a{content}"
        || v == "{keyword}\u70ae\u5236\u65b9\u6cd5\uff1a{content}"
        || v == "{keyword}\u70ae\u5236\uff1a{content}";
}

static string MakeAbsoluteUrl(string href)
{
    if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return href;
    }

    if (!href.StartsWith("/"))
    {
        href = "/" + href;
    }

    return "https://www.dayi.org.cn" + href;
}

static string CleanHtml(string html)
{
    if (string.IsNullOrWhiteSpace(html))
    {
        return "";
    }

    string text = html
        .Replace("\\u003C", "<")
        .Replace("\\u003E", ">")
        .Replace("\\u002F", "/");
    text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
    text = Regex.Replace(text, "</p>\\s*<p[^>]*>", "\n", RegexOptions.IgnoreCase);
    text = Regex.Replace(text, "<.*?>", "", RegexOptions.Singleline);
    text = WebUtility.HtmlDecode(text);
    text = Regex.Replace(text, "[ \\t\\x0B\\f]+", " ").Trim();
    text = Regex.Replace(text, "\\s*\\r?\\n\\s*", Environment.NewLine).Trim();
    return text;
}
