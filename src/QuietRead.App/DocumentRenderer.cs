using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuietRead.Core;

namespace QuietRead;

internal sealed record PreparedImages(IReadOnlyDictionary<string, ImageSource> Images, int Skipped);

internal static class DocumentRenderer
{
    public static int[] BuildSegments(ParsedChapter chapter)
    {
        var starts = new List<int> { 0 };
        int characters = 0, count = 0, inlines = 0;
        for (int i = 0; i < chapter.Blocks.Count; i++)
        {
            int length = chapter.Blocks[i].Inlines.Sum(x => x.Text.Length);
            if (count > 0 && (count >= ReaderLimits.BlocksPerView || characters + length > 40_000 || inlines + chapter.Blocks[i].Inlines.Count > 2500))
            { starts.Add(i); count = 0; characters = 0; inlines = 0; }
            characters += length; count++; inlines += chapter.Blocks[i].Inlines.Count;
        }
        return starts.ToArray();
    }

    public static PreparedImages PrepareImages(EpubBook book, ParsedChapter chapter, int start, int end, CancellationToken token)
    {
        var images = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        var considered = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0, count = 0;
        long decodedPixels = 0, sourceBytes = 0;
        for (int i = start; i < end; i++)
        {
            token.ThrowIfCancellationRequested();
            BookBlock block = chapter.Blocks[i];
            if (block.Kind != BlockKind.Image) continue;
            if (block.ImagePath == null) { skipped++; continue; }
            if (!considered.Add(block.ImagePath)) continue;
            if (++count > 24 || sourceBytes >= 32 * 1024 * 1024) { skipped++; continue; }
            try
            {
                RasterData data = book.ReadImage(block.ImagePath, token);
                sourceBytes += data.Bytes.Length;
                if (sourceBytes > 32 * 1024 * 1024) { skipped++; continue; }
                int width = Math.Min(1200, data.Info.Width);
                int height = (int)Math.Ceiling((double)data.Info.Height * width / data.Info.Width);
                long pixels = (long)width * height;
                if (decodedPixels + pixels > ReaderLimits.ImageViewPixels) { skipped++; continue; }
                var bitmap = new BitmapImage();
                using var stream = new MemoryStream(data.Bytes, false);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.StreamSource = stream;
                bitmap.DecodePixelWidth = width;
                bitmap.DecodePixelHeight = height;
                token.ThrowIfCancellationRequested();
                bitmap.EndInit();
                token.ThrowIfCancellationRequested();
                if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.PixelWidth > 16384 ||
                    bitmap.PixelHeight > 16384 || (long)bitmap.PixelWidth * bitmap.PixelHeight > ReaderLimits.ImageViewPixels - decodedPixels)
                { skipped++; continue; }
                // Copy into a bounded, detached bitmap. The document must not retain
                // BitmapImage.StreamSource (and the full compressed image buffers).
                int stride = checked(bitmap.PixelWidth * 4);
                byte[] pixelsData = new byte[checked(stride * bitmap.PixelHeight)];
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                converted.CopyPixels(pixelsData, stride, 0);
                token.ThrowIfCancellationRequested();
                BitmapSource detached = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96,
                    PixelFormats.Pbgra32, null, pixelsData, stride);
                detached.Freeze();
                decodedPixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                images.Add(block.ImagePath, detached);
            }
            catch (Exception exception) when (exception is EpubException or IOException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException)
            { skipped++; }
        }
        return new PreparedImages(images, skipped);
    }

    public static FlowDocument Create(ParsedChapter chapter, int start, int end, PreparedImages images,
        ReaderPreferences preferences, string highlight, Action<LocalLink> followLink, out Dictionary<int, Block> blocks)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(32, 20, 32, 44),
            ColumnWidth = double.PositiveInfinity,
            IsColumnWidthFlexible = true,
            TextAlignment = TextAlignment.Left,
            FlowDirection = chapter.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            FontFamily = Font(preferences.Font),
            FontSize = preferences.FontSize,
            LineHeight = preferences.FontSize * preferences.LineSpacing
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        document.SetResourceReference(FlowDocument.BackgroundProperty, "ReaderBrush");
        blocks = [];
        int highlightsLeft = 1000;
        for (int i = start; i < end; i++)
        {
            BookBlock block = chapter.Blocks[i];
            Block rendered;
            if (block.Kind == BlockKind.Image)
            {
                FrameworkElement content;
                if (block.ImagePath != null && images.Images.TryGetValue(block.ImagePath, out ImageSource? image))
                {
                    content = new Image
                    {
                        Source = image,
                        Stretch = Stretch.Uniform,
                        MaxHeight = 960,
                        MaxWidth = preferences.TextWidth - 64,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    System.Windows.Automation.AutomationProperties.SetName(content, block.Alt ?? "书内图片");
                }
                else
                {
                    var label = new TextBlock
                    {
                        Text = block.Alt + "\n（图片未显示）",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Padding = new Thickness(16),
                        TextAlignment = TextAlignment.Center
                    };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
                    var frame = new Border { Child = label, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
                    frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                    content = frame;
                }
                rendered = new BlockUIContainer(content) { Margin = new Thickness(0, 16, 0, 24) };
            }
            else if (block.Kind == BlockKind.Rule)
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 14) };
                line.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                rendered = new BlockUIContainer(line);
            }
            else
            {
                var paragraph = new Paragraph();
                foreach (BookInline inline in block.Inlines)
                {
                    Span span = inline.Link == null ? new Span() : new Hyperlink();
                    if (span is Hyperlink hyperlink)
                    {
                        LocalLink target = inline.Link!;
                        // No NavigateUri and no shell/browser launch path.
                        hyperlink.Click += (_, _) => followLink(target);
                        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                    }
                    if (inline.Style.HasFlag(TextStyle.Bold)) span.FontWeight = FontWeights.SemiBold;
                    if (inline.Style.HasFlag(TextStyle.Italic)) span.FontStyle = FontStyles.Italic;
                    if (inline.Style.HasFlag(TextStyle.Code))
                    { span.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"); span.FontSize = preferences.FontSize * 0.88; }
                    if (inline.Style.HasFlag(TextStyle.Superscript)) span.BaselineAlignment = BaselineAlignment.Superscript;
                    if (inline.Style.HasFlag(TextStyle.Subscript)) span.BaselineAlignment = BaselineAlignment.Subscript;
                    AddRuns(span, inline.Text, highlight, ref highlightsLeft);
                    paragraph.Inlines.Add(span);
                }
                ApplyParagraph(paragraph, block, preferences);
                rendered = paragraph;
            }
            blocks[i] = rendered;
            document.Blocks.Add(rendered);
        }
        return document;
    }

    private static void AddRuns(Span span, string text, string query, ref int highlightsLeft)
    {
        if (string.IsNullOrEmpty(query)) { span.Inlines.Add(new Run(text)); return; }
        int start = 0, highlighted = 0;
        while (start < text.Length)
        {
            int at = highlighted < 128 && highlightsLeft > 0 ? text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase) : -1;
            if (at < 0) { span.Inlines.Add(new Run(text[start..])); break; }
            if (at > start) span.Inlines.Add(new Run(text[start..at]));
            var run = new Run(text.Substring(at, query.Length));
            run.SetResourceReference(TextElement.BackgroundProperty, "HighlightBrush");
            run.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            span.Inlines.Add(run); start = at + query.Length; highlighted++; highlightsLeft--;
        }
    }

    public static FontFamily Font(string choice) => new(choice switch
    {
        "Sans" => "Microsoft YaHei UI, Segoe UI",
        "Georgia" => "Georgia, SimSun",
        _ => "SimSun, Georgia"
    });

    public static void ApplyParagraph(Paragraph paragraph, BookBlock block, ReaderPreferences preferences)
    {
        double size = preferences.FontSize;
        paragraph.FontSize = block.Kind == BlockKind.Heading ? size * (block.Level <= 1 ? 1.55 : block.Level == 2 ? 1.30 : 1.1) : size;
        paragraph.LineHeight = paragraph.FontSize * (block.Kind == BlockKind.Heading ? 1.4 : preferences.LineSpacing);
        paragraph.Margin = block.Kind == BlockKind.Heading ? new Thickness(0, size, 0, size * 0.8) : new Thickness(0, 0, 0, size * 0.8);
        if (block.Kind == BlockKind.Heading) paragraph.FontWeight = FontWeights.SemiBold;
        if (block.Kind == BlockKind.Quote)
        {
            paragraph.Padding = new Thickness(18, 4, 4, 4);
            paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
            paragraph.SetResourceReference(Block.BorderBrushProperty, "AccentSoftBrush");
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "SecondaryBrush");
        }
        if (block.Kind == BlockKind.Code)
        {
            paragraph.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            paragraph.FontSize = size * 0.86; paragraph.LineHeight = paragraph.FontSize * 1.6;
            paragraph.Padding = new Thickness(14); paragraph.SetResourceReference(TextElement.BackgroundProperty, "CodeBrush");
        }
        if (block.Kind == BlockKind.ListItem) paragraph.Margin = new Thickness(16 + block.Level * 14d, 0, 0, size * 0.55);
        if (block.Kind == BlockKind.TableRow) { paragraph.FontSize = size * 0.9; paragraph.LineHeight = paragraph.FontSize * 1.6; }
    }
}
