using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuke.Common.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

public class Converter
{
    private readonly AbsolutePath sourcePath;
    private readonly AbsolutePath outputPath;
    const int Workers = 4;

    public Converter(AbsolutePath sourceDir, AbsolutePath outputDir)
    {
        sourcePath = sourceDir;
        outputPath = outputDir;
    }

    public int Convert()
    {
        if (!Directory.Exists(sourcePath))
        {
            Console.Error.WriteLine($"ERROR: Source directory not found: {sourcePath}");
            return 1;
        }

        var pdfs = Directory.EnumerateFiles(sourcePath, "*.pdf")
            .Select(Path.GetFullPath)
            .OrderBy(p => p)
            .ToList();

        if (pdfs.Count == 0)
        {
            Console.Error.WriteLine("No PDF files found.");
            return 1;
        }

        outputPath.CreateOrCleanDirectory();

        Console.WriteLine($"Converting {pdfs.Count} PDF(s) -> {outputPath}");
        var sw = Stopwatch.StartNew();

        var succeeded = 0;
        var failed = 0;
        var lockObj = new object();

        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Workers };

        Parallel.ForEach(
            pdfs,
            parallelOpts,
            pdfPath =>
            {
                if (ConvertInternal(pdfPath))
                {
                    System.Threading.Interlocked.Increment(ref succeeded);
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref failed);
                }
            });

        Console.WriteLine(
            $"\nDone: {succeeded}/{pdfs.Count} succeeded, {failed} failed in {sw.Elapsed.TotalSeconds:F1}s.");
        return failed > 0 ? 1 : 0;
    }

    bool ConvertInternal(AbsolutePath pdfPath)
    {
        var title = pdfPath.NameWithoutExtension;
        var markdownName = title.Replace(" - ", "-").Replace(" ", "-");
        markdownName = Regex.Replace(markdownName, @"[\(\)<>:""/\\|?*]", "");
        var outDir = outputPath / markdownName;
        var imgDir = outDir / "images";

        try
        {
            Directory.CreateDirectory(imgDir);

            using var doc = PdfDocument.Open(pdfPath);
            var pageCount = doc.NumberOfPages;
            var md = new StringBuilder();
            md.Append(CultureInfo.InvariantCulture, $"# {title}");

            var totalImages = 0;

            for (var i = 0; i < pageCount; i++)
            {
                var page = doc.GetPage(i + 1);
                var pageNo = i + 1;
                var prefix = $"page-{pageNo:D3}";
                var blocks = new List<IBlock>();

                var images = page.GetImages();
                foreach (var (img, idx) in images.Select((img, idx) => (img, idx)))
                {
                    var (bytes, ext) = SaveImage(img, imgDir, prefix, idx + 1);
                    if (bytes > 0)
                    {
                        totalImages++;
                        blocks.Add(new ImageBlock($"{prefix}-img-{idx + 1:D2}.{ext}", img.BoundingBox.Bottom));
                        // imageRefs.Add($"{prefix}-img-{idx + 1:D2}.{ext}");
                    }
                }

                blocks.AddRange(
                    page.Letters
                        .GroupBy(x => x.EndBaseLine.Y)
                        .Select(x => new LineBlock(string.Concat(x.Select(y => y.Value)).Trim(), x.Key)));

                foreach (var block in blocks.OrderByDescending(x => x.Offset))
                {
                    block.Append(md);
                }
            }

            var mdPath = outDir / $"{markdownName}.md";

            var mdText = md.ToString();
            mdText = Replace(mdText, Bullet);
            mdText = Replace(mdText, Bullet2);
            mdText = Replace(mdText, Note);
            mdText = Replace(mdText, Torque);
            mdText = Replace(mdText, Torque2);
            mdText = Replace(mdText, Period);

            File.WriteAllText(mdPath, mdText, Encoding.UTF8);

            Console.WriteLine($"  OK {markdownName}: {pageCount} page(s), {totalImages} image(s)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAIL {markdownName}: {ex.Message}");
            return false;
        }

        return true;
    }

    private (Regex, string) Bullet = (new Regex(@"[◾•]\s*"), "* ");
    private (Regex, string) Bullet2 = (new Regex(@"[◦]\s*"), "  * ");
    private (Regex, string) Note = (new Regex(@"(^[A-Z ]+:)", RegexOptions.Multiline), "**$1**");
    private (Regex, string) Torque = (new Regex(@"(\d+(\.\d+)?) N·m"), "**$1 N·m**");
    private (Regex, string) Torque2 = (new Regex(@"\n([A-Z].+:)\n(\*\*\d)"), "* $1 $2");
    private (Regex, string) Period = (new Regex(@"\s+\."), ".");

    private static string Replace(string input, (Regex, string) replacement)
    {
        return replacement.Item1.Replace(input, replacement.Item2);
    }

    private static (int bytes, string ext) SaveImage(
        UglyToad.PdfPig.Content.IPdfImage img,
        string imgDir,
        string prefix,
        int idx)
    {
        var imgNameBase = $"{prefix}-img-{idx:D2}";

        if (img.TryGetPng(out var pngBytes))
        {
            var path = Path.Combine(imgDir, $"{imgNameBase}.png");
            File.WriteAllBytes(path, pngBytes);
            return (pngBytes.Length, "png");
        }

        var dict = img.ImageDictionary;
        if (dict.TryGet(NameToken.Filter, out var filter))
        {
            var filterStr = filter.ToString();
            if (filterStr == "/DCTDecode")
            {
                var path = Path.Combine(imgDir, $"{imgNameBase}.jpg");
                File.WriteAllBytes(path, img.RawBytes);
                return (img.RawBytes.Length, "jpg");
            }
        }

        return (0, "");
    }
}

public interface IBlock
{
    void Append(StringBuilder builder);

    double Offset { get; }
}

public record LineBlock(string Text, double Offset) : IBlock
{
    public void Append(StringBuilder builder)
    {
        if (Regex.IsMatch(Text, @"^(Page \d|\d\d\/\d\d)"))
            return;
        builder.AppendLine();
        if (Regex.IsMatch(Text, @"^[A-Z]"))
            builder.AppendLine();
        builder.Append(Text);
    }
}

public record ImageBlock(string Ref, double Offset) : IBlock
{
    public void Append(StringBuilder builder)
    {
        builder.Append($"\n\n![Image {Ref}](images/{Ref})");
    }
}
