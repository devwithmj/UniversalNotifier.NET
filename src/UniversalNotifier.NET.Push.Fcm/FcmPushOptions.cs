using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Push.Fcm;

/// <summary>Settings for <see cref="FcmPushNotifier"/>.</summary>
public sealed class FcmPushOptions
{
    /// <summary>The service account key as JSON. Keep it in a secret store. Takes precedence over <see cref="CredentialPath"/>.</summary>
    public string? CredentialJson { get; set; }

    /// <summary>The path to a service account key file.</summary>
    public string? CredentialPath { get; set; }

    /// <summary>The Firebase project id. Optional when the credentials contain it.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Without <see cref="CredentialJson"/> or <see cref="CredentialPath"/>, Google Application Default Credentials are
    /// used (the <c>GOOGLE_APPLICATION_CREDENTIALS</c> variable, or the identity of the Google Cloud host).
    /// </summary>
    public override string ToString() => $"FcmPushOptions {{ ProjectId = {ProjectId} }}";

    internal static string? Validate(FcmPushOptions options) =>
        !string.IsNullOrWhiteSpace(options.CredentialJson) && !string.IsNullOrWhiteSpace(options.CredentialPath)
            ? "Set either CredentialJson or CredentialPath, not both."
            : null;
}

internal sealed class FcmPushOptionsValidator : IValidateOptions<FcmPushOptions>
{
    public ValidateOptionsResult Validate(string? name, FcmPushOptions options) =>
        FcmPushOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
