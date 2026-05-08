using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IFileRuleValidator
{
    FileValidationResult Validate(string filePath, OrderData? orderData);
}
