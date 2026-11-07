using Keystone.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resend;

namespace Keystone.Infrastructure.Email;

/// <summary>
/// Everything the application needs in order to send mail, and the only place that names a
/// provider.
/// </summary>
/// <remarks>
/// Moving to Postmark is: a <c>PostmarkEmailSender</c> beside
/// <see cref="ResendEmailSender"/>, its options bound here instead, and the one line below that
/// picks the live sender. Nothing above <see cref="IEmailSender"/> — no page, notifier or job —
/// mentions a provider, so nothing above it has to change.
/// </remarks>
public static class EmailRegistration
{
    /// <summary>
    /// Whether a message can actually leave the machine, provider and sender address both
    /// being needed.
    /// </summary>
    /// <remarks>
    /// The one definition, used by the registration below to decide whether there is a live
    /// sender at all and by the settings screen to say so. Two copies of this would drift, and
    /// a screen that says mail cannot go out while it is going out is the worst of the two.
    /// </remarks>
    public static bool CanSend(ResendOptions provider, EmailOptions email) =>
        provider.IsConfigured && !string.IsNullOrWhiteSpace(email.From);

    public static IServiceCollection AddKeystoneEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        string outboxPath)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.Section));

        // Saved settings win over the file, and apply to the next message rather than the next
        // restart. Scoped, so it is only ever reached through IOptionsSnapshot.
        services.AddScoped<IPostConfigureOptions<EmailOptions>, EmailSettingsFromDatabase>();

        var provider = configuration.GetSection(ResendOptions.Section).Get<ResendOptions>()
            ?? new ResendOptions();

        services.AddResend(options =>
        {
            options.ApiToken = provider.ApiKey;

            // Failures arrive as exceptions rather than as an unexamined result. Every caller
            // already catches and logs around a send, because a message that did not go must
            // not roll back the hold or the enquiry that caused it.
            options.ThrowExceptions = true;
        });

        services.AddScoped<ResendEmailSender>();

        services.AddSingleton(sp => new LoggingEmailSender(
            sp.GetRequiredService<ILogger<LoggingEmailSender>>(), outboxPath));

        // Registered concretely as well as behind the interface. Everything in the application
        // asks for IEmailSender; the settings screen asks for this one, because reporting where
        // a test message went needs the verdict only the guard itself can give.
        services.AddScoped<SafeEmailSender>(sp =>
        {
            var email = sp.GetRequiredService<IOptionsSnapshot<EmailOptions>>();

            return new SafeEmailSender(
                outbox: sp.GetRequiredService<LoggingEmailSender>(),

                // The snapshot, not IOptions: it is rebuilt per scope, so the guards a settings
                // screen changed are the guards the next message is checked against.
                options: email,
                logger: sp.GetRequiredService<ILogger<SafeEmailSender>>(),

                // The only way out of the building, and still behind every guard above. Without
                // a key or a sender address there is no live sender at all, so an unconfigured
                // environment keeps writing to the outbox instead of failing on every message.
                live: CanSend(provider, email.Value)
                    ? sp.GetRequiredService<ResendEmailSender>()
                    : null);
        });

        services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<SafeEmailSender>());

        return services;
    }
}
