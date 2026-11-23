using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UniversalNotifier.NET.Tests;

public sealed record ReceivedMail(string From, IReadOnlyList<string> Recipients, string Data);

/// <summary>A tiny in-process SMTP server so the SMTP provider is tested end to end, including the real protocol dialogue.</summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ReceivedMail> _mails = [];
    private readonly Task _acceptLoop;

    public FakeSmtpServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>When set, AUTH PLAIN is offered and only these credentials are accepted.</summary>
    public (string User, string Password)? RequiredLogin { get; set; }

    /// <summary>Returns a full SMTP reply line (for example <c>550 5.1.1 no such user</c>) to refuse a recipient, or null to accept.</summary>
    public Func<string, string?>? RejectRecipient { get; set; }

    public IReadOnlyList<ReceivedMail> Mails
    {
        get
        {
            lock (_mails)
            {
                return [.. _mails];
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // listener shutdown
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (Exception)
        {
            // stopped
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var tcp = client;
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        var from = string.Empty;
        var recipients = new List<string>();

        try
        {
            await writer.WriteLineAsync("220 fake ESMTP ready");
            while (await reader.ReadLineAsync() is { } line)
            {
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO", StringComparison.Ordinal) || upper.StartsWith("HELO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-fake");
                    if (RequiredLogin is not null)
                    {
                        await writer.WriteLineAsync("250-AUTH PLAIN");
                    }

                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (upper.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    var encoded = line[10..].Trim();
                    if (encoded.Length == 0)
                    {
                        await writer.WriteLineAsync("334 ");
                        encoded = (await reader.ReadLineAsync() ?? string.Empty).Trim();
                    }

                    var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                    var ok = RequiredLogin is { } login && parts.Length == 3 && parts[1] == login.User && parts[2] == login.Password;
                    await writer.WriteLineAsync(ok ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication failed");
                }
                else if (upper.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                {
                    from = Between(line);
                    await writer.WriteLineAsync("250 2.1.0 Ok");
                }
                else if (upper.StartsWith("RCPT TO:", StringComparison.Ordinal))
                {
                    var address = Between(line);
                    if (RejectRecipient?.Invoke(address) is { } refusal)
                    {
                        await writer.WriteLineAsync(refusal);
                    }
                    else
                    {
                        recipients.Add(address);
                        await writer.WriteLineAsync("250 2.1.5 Ok");
                    }
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                    {
                        data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                    }

                    lock (_mails)
                    {
                        _mails.Add(new ReceivedMail(from, [.. recipients], data.ToString()));
                    }

                    recipients.Clear();
                    await writer.WriteLineAsync("250 2.0.0 Ok: queued as " + Guid.NewGuid().ToString("N")[..8]);
                }
                else if (upper == "RSET")
                {
                    recipients.Clear();
                    await writer.WriteLineAsync("250 2.0.0 Ok");
                }
                else if (upper == "NOOP")
                {
                    await writer.WriteLineAsync("250 2.0.0 Ok");
                }
                else if (upper == "QUIT")
                {
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("502 5.5.2 Command not recognized");
                }
            }
        }
        catch (IOException)
        {
            // client closed the connection
        }
    }

    private static string Between(string line)
    {
        var start = line.IndexOf('<', StringComparison.Ordinal);
        var end = line.IndexOf('>', StringComparison.Ordinal);
        return start >= 0 && end > start ? line[(start + 1)..end] : string.Empty;
    }
}
