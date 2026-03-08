namespace NjuPrepaidStatus.Models;

public sealed record InternetUsage(decimal AvailableGb, decimal MaxGb);

public sealed record BalanceStatus(
    string PeriodEnd,
    string OfferTitle,
    InternetUsage DomesticUsage,
    InternetUsage RoamingUsage);
