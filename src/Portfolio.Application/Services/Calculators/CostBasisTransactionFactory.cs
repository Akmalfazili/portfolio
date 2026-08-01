using Portfolio.Application.Abstractions;
using Portfolio.Domain.Entities;

namespace Portfolio.Application.Services.Calculators;

/// <summary>
/// Converts a native-currency <see cref="Transaction"/> into a USD-denominated
/// <see cref="CostBasisTransaction"/>, using the historical FX rate for that transaction's own
/// trade date (never today's rate) via <see cref="FxRateResolver"/>. Shared by every caller that
/// feeds <see cref="ICostBasisCalculator"/> so the USD-conversion rule — and the reporting-currency
/// constant — live in exactly one place.
/// </summary>
public static class CostBasisTransactionFactory
{
    public const string ReportingCurrency = "USD";

    /// <summary>
    /// <paramref name="fxRatesAscending"/> is ignored when <paramref name="assetCurrency"/> is
    /// already <see cref="ReportingCurrency"/> — pass an empty list in that case.
    /// </summary>
    public static CostBasisTransaction ToUsd(
        Transaction transaction, string assetCurrency, IReadOnlyList<FxRate> fxRatesAscending)
    {
        var grossNative = transaction.Quantity * transaction.PricePerUnit;

        if (assetCurrency == ReportingCurrency)
        {
            return new CostBasisTransaction(
                transaction.TradeDate, transaction.Type, transaction.Quantity, grossNative, transaction.Fees);
        }

        var rate = FxRateResolver.Resolve(fxRatesAscending, transaction.TradeDate);
        return new CostBasisTransaction(
            transaction.TradeDate, transaction.Type, transaction.Quantity, grossNative / rate, transaction.Fees / rate);
    }
}
