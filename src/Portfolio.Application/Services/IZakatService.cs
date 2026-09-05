using Portfolio.Application.Common;
using Portfolio.Application.Dtos;

namespace Portfolio.Application.Services;

/// <summary>See <see cref="ZakatService"/>.</summary>
public interface IZakatService
{
    /// <summary>
    /// Computes the zakat-on-shares report fresh — nothing is persisted. <paramref name="asOf"/>
    /// overrides "today" as the report's valuation reference date (used for crypto valuation and as
    /// the point each stock's own fiscal year end is resolved backward from); null means the
    /// current date from the injected <c>TimeProvider</c>.
    /// </summary>
    Task<ZakatReportDto> GetReportAsync(DateOnly? asOf, CancellationToken cancellationToken);

    /// <summary>History of actual zakat payments, newest <see cref="ZakatPaymentDto.PaidOn"/> first.</summary>
    Task<IReadOnlyList<ZakatPaymentDto>> ListPaymentsAsync(CancellationToken cancellationToken);

    Task<ServiceResult<ZakatPaymentDto>> CreatePaymentAsync(CreateZakatPaymentRequest request, CancellationToken cancellationToken);

    Task<ServiceResult<ZakatPaymentDto>> UpdatePaymentAsync(int id, UpdateZakatPaymentRequest request, CancellationToken cancellationToken);

    Task<bool> DeletePaymentAsync(int id, CancellationToken cancellationToken);
}
