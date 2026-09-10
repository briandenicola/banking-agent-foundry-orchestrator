using System.Text.Json;

namespace BankingAgent.Application;

/// <summary>
/// What the deployment can do about a contact channel a customer asked for.
/// </summary>
public enum ContactChannelStatus
{
    /// <summary>Nothing stored says how the customer wants to be contacted.</summary>
    NoneStated,

    /// <summary>A channel this deployment can service.</summary>
    Supported,

    /// <summary>
    /// A channel a bank would ordinarily offer that this deployment has not
    /// wired up. Distinguished from <see cref="Unsupported"/> because the honest
    /// answer is different: "not yet" rather than "not a thing we do".
    /// </summary>
    KnownUnavailable,

    /// <summary>A channel this deployment will not service.</summary>
    Unsupported
}

/// <summary>
/// One contact channel and what the deployment can do about it.
/// </summary>
/// <param name="Channel">Stable identifier, for example <c>sms</c>.</param>
/// <param name="Status">Whether this deployment can service it.</param>
/// <param name="Aliases">
/// What a customer might call it. This is the part that is unavoidably a
/// judgement call, so it is configuration rather than code: extending it is a
/// deployment change, not a release.
/// </param>
public sealed record ContactChannelDefinition(
    string Channel,
    ContactChannelStatus Status,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Wire form of <see cref="ContactChannelStatus"/>. The agent contract is
/// snake_case throughout, and the Python fallback path branches on these exact
/// values, so the name is spelled once here rather than left to ToString().
/// </summary>
public static class ContactChannelStatusNames
{
    public static string ToWireValue(this ContactChannelStatus status) => status switch
    {
        ContactChannelStatus.NoneStated => "none_stated",
        ContactChannelStatus.Supported => "supported",
        ContactChannelStatus.KnownUnavailable => "known_unavailable",
        ContactChannelStatus.Unsupported => "unsupported",
        _ => "none_stated"
    };
}

/// <summary>
/// The outcome of reading a remembered contact preference against what the
/// deployment can actually do.
/// </summary>
/// <param name="Status">What the deployment can do about it.</param>
/// <param name="Channel">The matched channel, or null when nothing matched.</param>
/// <param name="Guidance">
/// A sentence for the agents. The preference text itself already travels in
/// <c>customer_preferences</c>, so this says what to do about it rather than
/// restating it -- which is the part that was missing.
/// </param>
public sealed record ContactChannelAssessment(
    ContactChannelStatus Status,
    string? Channel,
    string? Guidance)
{
    public static readonly ContactChannelAssessment NoneStated =
        new(ContactChannelStatus.NoneStated, null, null);

    /// <summary>Whether there is anything worth telling the agents.</summary>
    public bool HasGuidance => !string.IsNullOrWhiteSpace(Guidance);
}

/// <summary>
/// Decides what the deployment can do about a remembered contact preference.
///
/// Two things are deliberately kept apart here. Recognising that a preference
/// names a channel is language, and it is done with a maintained alias list that
/// will never be complete. Deciding whether the channel can be serviced is
/// deployment fact, and it is a lookup -- never an inference -- because a model
/// asked whether the bank can send an SMS will happily say yes.
///
/// The failure mode is chosen rather than accepted. An unrecognised preference
/// resolves to <see cref="ContactChannelStatus.NoneStated"/> and the workflow
/// behaves exactly as it does today; a recognised but unserviceable one produces
/// an explicit refusal. Neither path can produce a promise.
/// </summary>
public sealed class ContactChannelPolicy
{
    /// <summary>
    /// Words that mark a preference as being about contact at all. Without
    /// this, a preference naming an unserviceable channel is indistinguishable
    /// from a customer who simply never said how to reach them, and the
    /// difference decides whether the agent owes them an answer.
    /// </summary>
    private static readonly string[] ContactIntentTerms =
    [
        "contact",
        "contacted",
        "reach me",
        "reached",
        "notify",
        "notified",
        "notification",
        "update me",
        "updates",
        "get in touch",
        "message me",
        "let me know"
    ];

    private readonly IReadOnlyList<ContactChannelDefinition> _channels;

    public ContactChannelPolicy(IReadOnlyList<ContactChannelDefinition> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
    }

    /// <summary>
    /// The channels this deployment services when nothing is configured.
    /// Conservative on purpose: only the two the application actually has a
    /// route for are supported, and the channels a bank would normally offer are
    /// named as unavailable rather than left to fall through to "unsupported",
    /// because "we cannot do that yet" and "we do not do that" are different
    /// answers and a customer can tell.
    /// </summary>
    public static ContactChannelPolicy Default { get; } = new(
    [
        new ContactChannelDefinition(
            "secure_message",
            ContactChannelStatus.Supported,
            ["secure message", "secure messaging", "message centre", "message center", "in-app", "in app"]),
        new ContactChannelDefinition(
            "email",
            ContactChannelStatus.Supported,
            ["email", "e-mail", "emailed", "mail me"]),
        new ContactChannelDefinition(
            "sms",
            ContactChannelStatus.KnownUnavailable,
            ["sms", "text", "texts", "text message", "text messages", "texting", "txt"]),
        new ContactChannelDefinition(
            "phone",
            ContactChannelStatus.KnownUnavailable,
            ["phone", "phone call", "call me", "telephone", "ring me"]),
        new ContactChannelDefinition(
            "post",
            ContactChannelStatus.KnownUnavailable,
            ["post", "letter", "by mail", "paper statement", "large-print statement", "large print"])
    ]);

    /// <summary>
    /// Builds a policy from configuration, falling back to
    /// <see cref="Default"/> when nothing is configured or the value cannot be
    /// read. A malformed channel list must not take the workflow down: the
    /// consequence of falling back is less tailored wording, and the
    /// consequence of throwing is a customer with no answer at all.
    /// </summary>
    public static ContactChannelPolicy FromConfiguration(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            var definitions = JsonSerializer.Deserialize<List<ConfiguredChannel>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (definitions is null || definitions.Count == 0)
            {
                error = "The contact channel configuration was empty.";
                return Default;
            }

            var channels = new List<ContactChannelDefinition>();
            foreach (var definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.Channel)
                    || !Enum.TryParse<ContactChannelStatus>(definition.Status, ignoreCase: true, out var status)
                    || status == ContactChannelStatus.NoneStated)
                {
                    error = $"Ignored a contact channel with a missing name or unrecognised status: '{definition.Status}'.";
                    continue;
                }

                channels.Add(new ContactChannelDefinition(
                    definition.Channel.Trim(),
                    status,
                    definition.Aliases ?? []));
            }

            return channels.Count > 0 ? new ContactChannelPolicy(channels) : Default;
        }
        catch (JsonException exception)
        {
            error = $"The contact channel configuration could not be read: {exception.Message}";
            return Default;
        }
    }

    /// <summary>
    /// Reads the remembered preferences and decides what can be done about the
    /// contact channel they name, if they name one.
    /// </summary>
    /// <param name="preferences">Remembered preference text, as stored.</param>
    /// <param name="suppressLightTone">
    /// True on the dispute and suspicious-activity routes. A customer whose card
    /// was just stolen is owed a plain answer, not a light one.
    /// </param>
    public ContactChannelAssessment Assess(
        IReadOnlyList<string> preferences,
        bool suppressLightTone)
    {
        if (preferences is null || preferences.Count == 0)
        {
            return ContactChannelAssessment.NoneStated;
        }

        var mentionsContact = false;

        foreach (var preference in preferences)
        {
            if (string.IsNullOrWhiteSpace(preference))
            {
                continue;
            }

            var normalized = Normalize(preference);
            mentionsContact |= ContactIntentTerms.Any(term => ContainsTerm(normalized, term));

            foreach (var channel in _channels)
            {
                if (channel.Aliases.Any(alias => ContainsTerm(normalized, Normalize(alias))))
                {
                    return new ContactChannelAssessment(
                        channel.Status,
                        channel.Channel,
                        BuildGuidance(channel.Status, channel.Channel, suppressLightTone));
                }
            }
        }

        // A preference that is plainly about being contacted but matches no
        // configured channel is the TikTok case: they named something, and it is
        // not something this deployment does.
        return mentionsContact
            ? new ContactChannelAssessment(
                ContactChannelStatus.Unsupported,
                null,
                BuildGuidance(ContactChannelStatus.Unsupported, null, suppressLightTone))
            : ContactChannelAssessment.NoneStated;
    }

    private static string BuildGuidance(
        ContactChannelStatus status,
        string? channel,
        bool suppressLightTone) => status switch
        {
            ContactChannelStatus.Supported =>
                $"The customer's stored contact preference ({channel}) is one this assistant can use. "
                + "Confirm briefly that updates will go there. Do not offer any other channel.",

            ContactChannelStatus.KnownUnavailable =>
                $"The customer's stored contact preference ({channel}) is NOT available in this service yet. "
                + "Acknowledge the preference, state plainly that you cannot send updates that way, "
                + "and say what will happen instead: updates appear in this conversation. "
                + "Do not promise to use it, and do not imply it may happen later.",

            ContactChannelStatus.Unsupported when suppressLightTone =>
                "The customer's stored contact preference names a channel this assistant cannot use. "
                + "Acknowledge it plainly, do not promise it, and say that updates appear in this "
                + "conversation instead. Keep the tone straightforward: this request is a dispute or a "
                + "suspected fraud, so do not be playful about it.",

            ContactChannelStatus.Unsupported =>
                "The customer's stored contact preference names a channel this assistant cannot use. "
                + "Acknowledge it, and you may note the mismatch with one light, self-deprecating clause "
                + "about the assistant's own limits -- never at the customer's expense. "
                + "Then say plainly that updates appear in this conversation instead. "
                + "Do not promise the channel, and keep it to one sentence.",

            _ => null
        } ?? string.Empty;

    private static string Normalize(string value) =>
        value.ToLowerInvariant().Replace('\u2019', '\'');

    /// <summary>
    /// Whole-word containment. Substring matching would find "post" inside
    /// "postal code" and "text" inside "context", either of which would attach a
    /// contact channel to a preference that never mentioned one.
    /// </summary>
    private static bool ContainsTerm(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return false;
        }

        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            var startsCleanly = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var endIndex = index + needle.Length;
            var endsCleanly = endIndex == haystack.Length || !char.IsLetterOrDigit(haystack[endIndex]);

            if (startsCleanly && endsCleanly)
            {
                return true;
            }

            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private sealed record ConfiguredChannel(
        string? Channel,
        string? Status,
        List<string>? Aliases);
}
