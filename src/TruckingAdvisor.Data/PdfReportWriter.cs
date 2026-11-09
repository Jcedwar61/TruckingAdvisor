using System.Globalization;
using System.Text;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Data;

/// <summary>
/// Writes simple text PDFs (PDF 1.4, built-in Helvetica fonts, US Letter) without any library.
/// Used for the delivery report and the trip-history summary.
/// </summary>
public static class PdfReportWriter
{
    private const double PageWidth = 612, PageHeight = 792, Margin = 54, LineHeight = 15;

    /// <summary>One line of the document. Bold lines use Helvetica-Bold; Size is in points.</summary>
    public sealed record Line(string Text, bool Bold = false, double Size = 11);

    public static byte[] DeliveryReport(DeliveryTripRecord trip)
    {
        var lines = new List<Line> { new("TruckingAdvisor - Delivery Report", true, 18), new("") };
        foreach (var text in TruckingAdvisorDatabase.ExportDeliveryReportText(trip).Split('\n').Skip(2))
        {
            string t = text.TrimEnd('\r');
            lines.Add(new Line(t, t.StartsWith("FINAL") || t.StartsWith("RATING"), t.StartsWith("FINAL") ? 14 : 11));
        }
        lines.Add(new(""));
        lines.Add(new("Score breakdown", true, 13));
        var b = trip.ScoreBreakdown;
        foreach (var (name, value) in new[]
        {
            ("Speed compliance", b.SpeedCompliance), ("Braking smoothness", b.BrakingSmoothness),
            ("Acceleration smoothness", b.AccelerationSmoothness), ("Fuel efficiency", b.FuelEfficiency),
            ("Engine management", b.EngineManagement), ("Truck damage", b.TruckDamageScore),
            ("Trailer damage", b.TrailerDamageScore), ("Delivery completion", b.DeliveryCompletion),
            ("Punctuality", b.Punctuality), ("Consistency", b.Consistency)
        })
            lines.Add(new($"{name,-26} {value.ToString("F0", CultureInfo.InvariantCulture),5}"));
        lines.Add(new(""));
        lines.Add(new("Recorded by TruckingAdvisor from game telemetry. Not affiliated with SCS Software.", false, 8));
        return Write(lines);
    }

    public static byte[] TripHistory(IEnumerable<DeliveryTripRecord> trips, string driver, string company)
    {
        var list = trips.OrderByDescending(t => t.StartedAt).ToList();
        var done = list.Where(t => t.State == DeliveryState.Completed).ToList();
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<Line>
        {
            new("TruckingAdvisor - Trip History", true, 18),
            new($"{company} - driver {driver} - {DateTime.Now.ToString("yyyy-MM-dd", inv)}"),
            new(""),
            new($"{done.Count} completed deliveries, {done.Sum(t => t.DistanceMiles).ToString("N0", inv)} miles, " +
                $"average score {(done.Count > 0 ? done.Average(t => t.ScoreBreakdown.FinalScore) : 0).ToString("F0", inv)}"),
            new(""),
            new("Date        Cargo                 Route                               Miles   MPG  Score  Result", true, 9)
        };
        foreach (var t in list)
        {
            string route = $"{t.Origin} -> {t.Destination}";
            lines.Add(new(string.Create(inv,
                $"{t.StartedAt:yyyy-MM-dd}  {Fit(t.Cargo, 20),-20}  {Fit(route, 34),-34}  {t.DistanceMiles,5:F0}  {(t.AverageMpg > 0 ? t.AverageMpg.ToString("F1", inv) : "--"),4}  {t.ScoreBreakdown.FinalScore,5}  {t.State}"), false, 9));
        }
        return Write(lines, monospaceSmall: true);
    }

    private static string Fit(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

    /// <summary>Lays lines out on as many pages as needed and returns the PDF bytes.</summary>
    public static byte[] Write(IReadOnlyList<Line> lines, bool monospaceSmall = false)
    {
        // Split into pages.
        var pages = new List<List<(Line Line, double Y)>>();
        var page = new List<(Line, double)>();
        double y = PageHeight - Margin;
        foreach (var line in lines)
        {
            double h = Math.Max(LineHeight, line.Size + 4);
            if (y - h < Margin) { pages.Add(page); page = new(); y = PageHeight - Margin; }
            y -= h;
            page.Add((line, y));
        }
        pages.Add(page);

        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; } // 1-based object numbers

        int catalog = Add("");             // filled in below
        int pagesObj = Add("");
        string regular = monospaceSmall ? "Courier" : "Helvetica";
        string bold = monospaceSmall ? "Courier-Bold" : "Helvetica-Bold";
        int font = Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{regular} /Encoding /WinAnsiEncoding >>");
        int fontBold = Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{bold} /Encoding /WinAnsiEncoding >>");

        var pageIds = new List<int>();
        foreach (var p in pages)
        {
            var content = new StringBuilder();
            foreach (var (line, ly) in p)
            {
                if (line.Text.Length == 0) continue;
                content.Append(CultureInfo.InvariantCulture,
                    $"BT /{(line.Bold ? "F2" : "F1")} {line.Size:0.#} Tf {Margin:0.#} {ly:0.#} Td ({Escape(line.Text)}) Tj ET\n");
            }
            string stream = content.ToString();
            int contentId = Add($"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream");
            pageIds.Add(Add($"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {PageWidth:0} {PageHeight:0}] " +
                            $"/Resources << /Font << /F1 {font} 0 R /F2 {fontBold} 0 R >> >> /Contents {contentId} 0 R >>"));
        }
        objects[catalog - 1] = $"<< /Type /Catalog /Pages {pagesObj} 0 R >>";
        objects[pagesObj - 1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>";

        // Serialize with an exact cross-reference table.
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>PDF string escaping; characters outside Latin-1 become '?'.</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '(': sb.Append(@"\("); break;
                case ')': sb.Append(@"\)"); break;
                default: sb.Append(c is >= ' ' and <= 'ÿ' ? c : '?'); break;
            }
        }
        return sb.ToString();
    }
}
