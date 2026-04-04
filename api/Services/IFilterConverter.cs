namespace AFHSync.Api.Services;

using AFHSync.Api.DTOs;

public interface IFilterConverter
{
    FilterConversionResult Convert(string opathFilter);
    string ToPlainLanguage(string opathFilter);
}
