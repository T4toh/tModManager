using System.Net;

namespace NexusMods.Abstractions.NexusWebApi;

/// <summary>
/// The website download endpoint needs a logged-in nexusmods.com session in Firefox and there is none
/// (no cookies, or Nexus answered as to an anonymous visitor). Every other file will fail the same way,
/// so a batch should stop instead of trying them one by one.
/// </summary>
/// <remarks>A 403 <see cref="HttpRequestException"/>, so callers that fall back on Forbidden keep working.</remarks>
public sealed class NexusWebsiteSessionRequiredException()
    : HttpRequestException("No hay sesión de nexusmods.com en Firefox", inner: null, HttpStatusCode.Forbidden);
