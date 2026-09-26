using NexusMods.Abstractions.FileExtractor;
using NexusMods.Sdk.FileExtractor;

namespace NexusMods.Backend.FileExtractor.FileSignatures;

/// <inheritdoc />
internal class SignatureCheckerFactory : ISignatureCheckerFactory
{
    /// <inheritdoc />
    public ISignatureChecker Create(params FileType[] fileTypes) => new SignatureChecker(fileTypes);
}
