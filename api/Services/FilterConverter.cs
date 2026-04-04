namespace AFHSync.Api.Services;

using AFHSync.Api.DTOs;
using Microsoft.Extensions.Logging;

public class FilterConverter : IFilterConverter
{
    private readonly ILogger<FilterConverter> _logger;

    public FilterConverter(ILogger<FilterConverter> logger)
    {
        _logger = logger;
    }

    public FilterConversionResult Convert(string opathFilter)
    {
        throw new NotImplementedException();
    }

    public string ToPlainLanguage(string opathFilter)
    {
        throw new NotImplementedException();
    }
}
