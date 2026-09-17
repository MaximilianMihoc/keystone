using Hubix.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// What the outbox sender says out loud, and what it keeps to the file.
/// </summary>
/// <remarks>
/// Every bearer token this portal issues travels in an email body — staff sign-in links, hold,
/// waitlist and reservation-confirmation links. Logging the body put all of them in the
/// application log, where they are kept longer, copied further and read by more people than a
/// mailbox (issue #53).
/// </remarks>
public class OutboxSenderTests : IDisposable
{
    private readonly string _outbox = Path.Combine(
        Path.GetTempPath(), $"hubix-outbox-{Guid.NewGuid():N}");

    /// <summary>A logger that keeps everything it was told, rendered and structured.</summary>
    private sealed class RecordingLogger : ILogger<LoggingEmailSender>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));

            // Also the values behind the template: a sink may render those instead, so a body
            // smuggled in as a structured field would still reach the log.
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Lines.AddRange(values.Select(v => v.Value?.ToString() ?? ""));
            }
        }
    }

    private const string Token = "wQ7nJ4mX2bK9pL5tR8vY3zA6cF1hD0gS";

    private static EmailMessage SignInEmail() =>
        new("staff@hubix.invalid",
            "Your Hubix sign-in link",
            $"""
             Hello,

             Sign in here: https://portal.hubix.invalid/admin/login/verify?token={Token}

             It works once and expires in 15 minutes.
             """);

    private async Task<RecordingLogger> SendAsync(EmailMessage message)
    {
        var logger = new RecordingLogger();

        await new LoggingEmailSender(logger, _outbox).SendAsync(message, CancellationToken.None);

        return logger;
    }

    [Fact]
    public async Task No_token_reaches_the_log()
    {
        var logger = await SendAsync(SignInEmail());

        // Fifteen minutes of admin for anyone who can read the log, otherwise.
        logger.Lines.ShouldAllBe(line => !line.Contains(Token));
    }

    [Fact]
    public async Task The_body_does_not_reach_the_log_at_all()
    {
        var logger = await SendAsync(SignInEmail());

        // Bodies also carry names, phone numbers and which apartment somebody is buying.
        logger.Lines.ShouldAllBe(line => !line.Contains("expires in 15 minutes"));
    }

    [Fact]
    public async Task The_recipient_and_subject_are_still_logged()
    {
        // "Did that email go out, and where" has to stay answerable from the log.
        var logger = await SendAsync(SignInEmail());

        logger.Lines.ShouldContain(line => line.Contains("staff@hubix.invalid"));
        logger.Lines.ShouldContain(line => line.Contains("Your Hubix sign-in link"));
    }

    [Fact]
    public async Task The_outbox_file_keeps_the_whole_message()
    {
        // The outbox is the development substitute for an inbox: it is gitignored, local, and
        // the only place the link can now be read.
        await SendAsync(SignInEmail());

        var written = await File.ReadAllTextAsync(Directory.GetFiles(_outbox).ShouldHaveSingleItem());

        written.ShouldContain(Token);
        written.ShouldContain("To:      staff@hubix.invalid");
        written.ShouldContain("Subject: Your Hubix sign-in link");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outbox))
        {
            Directory.Delete(_outbox, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
