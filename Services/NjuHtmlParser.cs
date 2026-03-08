using System.Globalization;
using System.Text.RegularExpressions;
using NjuPrepaidStatus.Models;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using HtmlNode = HtmlAgilityPack.HtmlNode;

namespace NjuPrepaidStatus.Services;

public sealed class NjuHtmlParser
{
    private static readonly Regex NumberRegex = new(@"\d+(?:[.,]\d{1,2})?", RegexOptions.Compiled);

    public BalanceStatus ParseBalanceStatus(string rawHtml)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(rawHtml);

        var periodEnd = doc.DocumentNode
            .SelectSingleNode("//div[contains(@class,'small-comment') and contains(@class,'mobile-text-right') and contains(@class,'tablet-text-right')]")
            ?.InnerText.Trim()
            ?? string.Empty;

        var offerTitle = doc.DocumentNode
            .SelectSingleNode("//div[contains(@class,'four') and contains(@class,'columns')]//strong")
            ?.InnerText.Trim()
            ?? string.Empty;

        var usageNode = doc.DocumentNode.SelectSingleNode("//p[contains(@class,'text-right')]");
        var values = usageNode is null ? [] : ParseNumericValues(usageNode.InnerText).ToArray();
        if (values.Length < 2)
        {
            var promoMb = ParsePromotionalDomesticMb(doc.DocumentNode);
            if (promoMb <= 0)
            {
                throw new InvalidOperationException("Could not parse domestic internet usage values.");
            }

            var promoGb = promoMb / 1024m;
            return new BalanceStatus(
                periodEnd,
                string.IsNullOrWhiteSpace(offerTitle) ? "promocyjny pakiet danych" : offerTitle,
                new InternetUsage(promoGb, promoGb),
                new InternetUsage(0m, 0m));
        }

        var roamingUsage = values.Length >= 4
            ? new InternetUsage(values[2], values[3])
            : new InternetUsage(0m, 0m);

        return new BalanceStatus(
            periodEnd,
            offerTitle,
            new InternetUsage(values[0], values[1]),
            roamingUsage);
    }

    private static IEnumerable<decimal> ParseNumericValues(string input)
    {
        return NumberRegex
            .Matches(input)
            .Select(m => m.Value.Replace(',', '.'))
            .Select(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1m)
            .Where(v => v >= 0);
    }

    private static decimal ParsePromotionalDomesticMb(HtmlNode root)
    {
        var promoOfferNodes = root.SelectNodes("//div[contains(@class,'four') and contains(@class,'columns')]//strong");
        if (promoOfferNodes is null || promoOfferNodes.Count == 0)
        {
            return 0m;
        }

        decimal totalMb = 0m;
        foreach (var offerNode in promoOfferNodes)
        {
            if (!offerNode.InnerText.Contains("promocyjny pakiet danych", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var offerContainer = offerNode.ParentNode;
            if (offerContainer is null)
            {
                continue;
            }

            var infoNode = offerContainer.SelectSingleNode(
                "following::div[contains(@class,'small-comment') and contains(@class,'mobile-text-right') and contains(@class,'tablet-text-right')][1]");
            if (infoNode is null)
            {
                continue;
            }

            var parsed = ParseNumericValues(infoNode.InnerText).FirstOrDefault();
            if (parsed > 0)
            {
                totalMb += parsed;
            }
        }

        return totalMb;
    }
}
