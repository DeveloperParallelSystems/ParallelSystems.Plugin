using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;

namespace ParallelSystemPlugin.UI
{
    public partial class AboutDialog : Window
    {
        private string _manualPath;
        private string _changelogPath;
        private string _devNotesPath;

        public AboutDialog()
        {
            InitializeComponent();
            ResolveDocPaths();
            LoadDocs();
            LoadVersion();
        }

        private void ResolveDocPaths()
        {
            string baseDir = Path.GetDirectoryName(typeof(ParallelSystemsPlugin.App).Assembly.Location);
            string docsDir = Path.Combine(baseDir, "Docs");
            _manualPath = Path.Combine(docsDir, "UserManual.md");
            _changelogPath = Path.Combine(docsDir, "CHANGELOG.md");
            _devNotesPath = Path.Combine(docsDir, "DEVELOPERS.md");
        }

        private void LoadDocs()
        {
            string manual = SafeRead(_manualPath, "User Manual not found");
            string changelog = SafeRead(_changelogPath, "Change log not found");

            ManualViewer.Document = BuildDocFromMarkdown(manual);
            ChangelogViewer.Document = BuildDocFromMarkdown(changelog);
        }

        private FlowDocument BuildDocFromMarkdown(string text)
        {
            var doc = new FlowDocument();
            doc.FontFamily = this.FontFamily;
            doc.FontSize = this.FontSize;
            doc.Foreground = (Brush)TryFindResource(SystemColors.ControlTextBrushKey);
            doc.Background = Brushes.Transparent;
            doc.PagePadding = new Thickness(8, 4, 8, 12);  // breathing room
            doc.TextAlignment = TextAlignment.Left;

            // theme brushes
            var accent = (Brush)TryFindResource(SystemColors.HighlightBrushKey) ?? Brushes.SteelBlue;
            var track = (Brush)TryFindResource(SystemColors.ControlLightBrushKey) ?? Brushes.Gainsboro;

            // very small markdown subset: #, ##, ###, lists, code fences, paragraphs
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inCode = false;
            var codeBuf = new System.Text.StringBuilder();
            List list = null; // current list block

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // code fences
                if (line.StartsWith("```"))
                {
                    if (!inCode)
                    {
                        inCode = true; codeBuf.Clear();
                    }
                    else
                    {
                        // flush code block
                        inCode = false;
                        var para = new Paragraph(new Run(codeBuf.ToString()));
                        para.FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace");
                        para.FontSize = this.FontSize - 1;
                        para.Margin = new Thickness(0, 6, 0, 12);
                        para.Background = track;
                        para.Padding = new Thickness(8);
                        para.BorderBrush = accent;
                        para.BorderThickness = new Thickness(1);
                        para.TextAlignment = TextAlignment.Left;
                        doc.Blocks.Add(para);
                    }
                    continue;
                }
                if (inCode)
                {
                    codeBuf.AppendLine(line);
                    continue;
                }

                // headings
                if (line.StartsWith("### "))
                {
                    doc.Blocks.Add(MakeHeading(line.Substring(4), 15, accent));
                    list = null; // close list
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    doc.Blocks.Add(MakeHeading(line.Substring(3), 16, accent));
                    list = null;
                    continue;
                }
                if (line.StartsWith("# "))
                {
                    doc.Blocks.Add(MakeHeading(line.Substring(2), 18, accent));
                    list = null;
                    continue;
                }

                // lists
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    if (list == null || list.MarkerStyle != TextMarkerStyle.Disc)
                    {
                        list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 4, 0, 8) };
                        doc.Blocks.Add(list);
                    }
                    var li = new ListItem(BuildParagraph(trimmed.Substring(2).Trim()));
                    list.ListItems.Add(li);
                    continue;
                }
                // ordered list: "1. "
                int dot = trimmed.IndexOf(". ");
                if (dot > 0)
                {
                    bool isNum = true;
                    for (int k = 0; k < dot; k++) if (!char.IsDigit(trimmed[k])) { isNum = false; break; }
                    if (isNum)
                    {
                        if (list == null || list.MarkerStyle != TextMarkerStyle.Decimal)
                        {
                            list = new List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(0, 4, 0, 8) };
                            doc.Blocks.Add(list);
                        }
                        var li = new ListItem(BuildParagraph(trimmed.Substring(dot + 2).Trim()));
                        list.ListItems.Add(li);
                        continue;
                    }
                }

                // blank line closes list
                if (string.IsNullOrWhiteSpace(line))
                {
                    list = null;
                    continue;
                }

                // paragraph (with inline formatting)
                doc.Blocks.Add(BuildParagraph(line.Trim()));
            }

            return doc;
        }

        private Block MakeHeading(string text, double size, Brush accent)
        {
            // Heading as a styled paragraph; also supports inline tokens
            var para = BuildParagraph(text.Trim());
            para.FontWeight = FontWeights.SemiBold;
            para.FontSize = size;
            para.Margin = new Thickness(0, 8, 0, 6);
            return para;
        }

        // ---------- Inline formatting helpers ----------
        // Supports:
        //   **bold**
        //   `italics`         (backticks render as italics per your spec)
        //   [text](https://)  (clickable hyperlink)
        private Paragraph BuildParagraph(string text)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(p.Inlines, text ?? string.Empty);
            return p;
        }

        private void AddInlines(InlineCollection inlines, string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // Link: [text](url)
                if (text[i] == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    if (closeBracket > i + 1 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket + 2)
                        {
                            string linkText = text.Substring(i + 1, closeBracket - (i + 1));
                            string url = text.Substring(closeBracket + 2, closeParen - (closeBracket + 2));

                            var hyperlink = new Hyperlink();
                            AddInlines(hyperlink.Inlines, linkText); // parse inner text too
                            try
                            {
                                hyperlink.NavigateUri = new Uri(url, UriKind.Absolute);
                                hyperlink.RequestNavigate += (s, e) =>
                                {
                                    ShellOpen(e.Uri.AbsoluteUri);
                                    e.Handled = true;
                                };
                            }
                            catch
                            {
                                // if URL invalid, render as plain text below
                            }

                            inlines.Add(hyperlink);
                            i = closeParen + 1;
                            continue;
                        }
                    }
                }

                // Bold: **text**
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        string inner = text.Substring(i + 2, end - (i + 2));
                        var b = new Bold();
                        AddInlines(b.Inlines, inner);
                        inlines.Add(b);
                        i = end + 2;
                        continue;
                    }
                }

                // Italic via backtick: `text`
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        string inner = text.Substring(i + 1, end - (i + 1));
                        var it = new Italic();
                        AddInlines(it.Inlines, inner);
                        inlines.Add(it);
                        i = end + 1;
                        continue;
                    }
                }

                // Plain run until next token
                int next = NextTokenIndex(text, i);
                string chunk = text.Substring(i, next - i);
                inlines.Add(new Run(chunk));
                i = next;
            }
        }

        private int NextTokenIndex(string s, int start)
        {
            int n1 = s.IndexOf('[', start);
            int n2 = s.IndexOf("**", start, StringComparison.Ordinal);
            int n3 = s.IndexOf('`', start);

            int next = s.Length;
            if (n1 >= 0 && n1 < next) next = n1;
            if (n2 >= 0 && n2 < next) next = n2;
            if (n3 >= 0 && n3 < next) next = n3;
            return next;
        }
        // ---------- end inline helpers ----------

        private static string SafeRead(string path, string fallback)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : fallback; }
            catch { return fallback; }
        }

        private void LoadVersion()
        {
            var asm = Assembly.GetExecutingAssembly();

            string info = GetAttr<AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
            {
                info = GetAttr<AssemblyFileVersionAttribute>(asm)?.Version
                    ?? asm.GetName().Version?.ToString();
            }

            VersionLabel.Text = NormalizeDisplayVersion(info);

            try
            {
                var fi = new FileInfo(asm.Location);
                BuildDateLabel.Text = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                BuildDateLabel.Text = "—";
            }
        }

        private static string NormalizeDisplayVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return "0.0.0";

            string normalized = version.Trim();

            // Defensively hide SDK-generated source revision suffixes such as
            // 1.17.7+e21376d. Directory.Build.props also disables that suffix.
            int sourceRevisionIndex = normalized.IndexOf('+');
            if (sourceRevisionIndex >= 0)
                normalized = normalized.Substring(0, sourceRevisionIndex);

            // Assembly/File versions normally contain four fields. The public
            // plugin version follows the three-part changelog format.
            string[] parts = normalized.Split('.');
            if (parts.Length == 4 && parts[3] == "0")
                normalized = string.Join(".", parts, 0, 3);

            return string.IsNullOrWhiteSpace(normalized)
                ? "0.0.0"
                : normalized;
        }

        private static T GetAttr<T>(Assembly asm) where T : Attribute
        {
            object[] attrs = asm.GetCustomAttributes(typeof(T), false);
            return attrs != null && attrs.Length > 0 ? (T)attrs[0] : null;
        }

        public void ShowModal(IntPtr owner)
        {
            try { new WindowInteropHelper(this) { Owner = owner }; } catch { }
            this.ShowDialog();
        }

        private void OnOpenManual(object sender, RoutedEventArgs e) => ShellOpen(_manualPath);
        private void OnOpenChangelog(object sender, RoutedEventArgs e) => ShellOpen(_changelogPath);
        private void OnOpenDevelopers(object sender, RoutedEventArgs e) => ShellOpen(_devNotesPath);
        private void OnVisitSite(object sender, RoutedEventArgs e) => ShellOpen("https://www.parallelsystems.com.au/");
        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnCopyManualPath(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_manualPath ?? string.Empty); } catch { }
        }

        private void OnLink(object sender, RequestNavigateEventArgs e)
        {
            ShellOpen(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private static void ShellOpen(string pathOrUrl)
        {
            try
            {
                var psi = new ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
                Process.Start(psi);
            }
            catch { }
        }
    }
}
