using Microsoft.Extensions.Options;

namespace UniversalNotifier.NET.Telegram;

/// <summary>Settings for <see cref="TelegramNotifier"/>.</summary>
public sealed class TelegramOptions
{
    /// <summary>The bot token from @BotFather. Keep it in a secret store: anyone with it controls the bot.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>The Bot API address. Change it only to use a self-hosted Bot API server.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.telegram.org");

    /// <summary>How long a single API call may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many chats of one message are sent in parallel.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Never prints the bot token.</summary>
    public override string ToString() => "TelegramOptions";

    internal static string? Validate(TelegramOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BotToken) || options.BotToken.AsSpan().IndexOfAny('/', ' ') >= 0)
        {
            return "Telegram BotToken is required (the token from @BotFather, for example 123456:ABC-DEF).";
        }

        if (options.BaseAddress is null || options.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            return "Telegram BaseAddress must be an https address.";
        }

        if (options.MaxConcurrency < 1)
        {
            return "Telegram MaxConcurrency must be at least 1.";
        }

        return options.Timeout > TimeSpan.Zero ? null : "Telegram Timeout must be positive.";
    }
}

internal sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options) =>
        TelegramOptions.Validate(options) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
