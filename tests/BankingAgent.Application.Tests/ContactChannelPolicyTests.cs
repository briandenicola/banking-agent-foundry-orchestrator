using BankingAgent.Application;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// A remembered preference like "only contact me by SMS" used to travel to the
/// agents as bare text, which left every agent free to agree to it. Nothing in
/// the system knew whether an SMS could actually be sent, so the failure was a
/// promise to a customer that the service could not keep.
///
/// These tests pin the two halves of the fix separately, because they have
/// different standards of correctness. Recognising a channel is a maintained
/// alias list and will never be complete, so it is allowed to miss. Deciding
/// whether a recognised channel can be serviced is deployment fact and must
/// never be a guess.
/// </summary>
public sealed class ContactChannelPolicyTests
{
    [Fact]
    public void A_channel_a_bank_would_offer_but_this_deployment_lacks_is_refused_as_not_yet()
    {
        var assessment = ContactChannelPolicy.Default.Assess(
            ["Prefers to be contacted by SMS only"],
            suppressLightTone: false);

        Assert.Equal(ContactChannelStatus.KnownUnavailable, assessment.Status);
        Assert.Equal("sms", assessment.Channel);
        Assert.Contains("cannot send updates that way", assessment.Guidance);
    }

    [Fact]
    public void A_channel_the_deployment_has_is_confirmed_rather_than_refused()
    {
        var assessment = ContactChannelPolicy.Default.Assess(
            ["Please contact me by secure message"],
            suppressLightTone: false);

        Assert.Equal(ContactChannelStatus.Supported, assessment.Status);
        Assert.Equal("secure_message", assessment.Channel);
    }

    [Fact]
    public void A_channel_no_bank_offers_is_still_answered_rather_than_ignored()
    {
        // The point of the Unsupported case: the customer said something
        // specific, and pretending not to have heard it is its own discourtesy.
        var assessment = ContactChannelPolicy.Default.Assess(
            ["Only contact me on TikTok"],
            suppressLightTone: false);

        Assert.Equal(ContactChannelStatus.Unsupported, assessment.Status);
        Assert.Contains("cannot use", assessment.Guidance);
    }

    [Fact]
    public void The_lighter_wording_is_withheld_from_disputes_and_suspected_fraud()
    {
        var playful = ContactChannelPolicy.Default.Assess(
            ["Only contact me on TikTok"],
            suppressLightTone: false);
        var plain = ContactChannelPolicy.Default.Assess(
            ["Only contact me on TikTok"],
            suppressLightTone: true);

        Assert.Contains("self-deprecating", playful.Guidance);
        Assert.DoesNotContain("self-deprecating", plain.Guidance);
        Assert.Contains("do not be playful", plain.Guidance);
    }

    [Fact]
    public void A_customer_who_never_said_how_to_reach_them_changes_nothing()
    {
        var assessment = ContactChannelPolicy.Default.Assess(
            ["Needs large-print statements", "Travels often"],
            suppressLightTone: false);

        Assert.Equal(ContactChannelStatus.NoneStated, assessment.Status);
        Assert.False(assessment.HasGuidance);
    }

    [Fact]
    public void No_stored_preferences_at_all_changes_nothing()
    {
        var assessment = ContactChannelPolicy.Default.Assess([], suppressLightTone: false);

        Assert.Equal(ContactChannelStatus.NoneStated, assessment.Status);
    }

    [Theory]
    [InlineData("Statements go to my postal code on file")]
    [InlineData("Explain the context of each charge")]
    public void An_incidental_word_does_not_become_a_contact_preference(string preference)
    {
        // "post" sits inside "postal code" and "text" inside "context".
        // Substring matching would attach a channel to a preference that never
        // named one, and the customer would be refused something they never
        // asked for.
        var assessment = ContactChannelPolicy.Default.Assess([preference], suppressLightTone: false);

        Assert.Equal(ContactChannelStatus.NoneStated, assessment.Status);
    }

    [Fact]
    public void A_deployment_can_declare_a_channel_it_has_wired_up()
    {
        var policy = ContactChannelPolicy.FromConfiguration(
            """
            [
              { "channel": "sms", "status": "Supported", "aliases": ["sms", "text message"] }
            ]
            """,
            out var error);

        Assert.Null(error);
        var assessment = policy.Assess(["Contact me by SMS"], suppressLightTone: false);
        Assert.Equal(ContactChannelStatus.Supported, assessment.Status);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""[{ "channel": "", "status": "Supported", "aliases": [] }]""")]
    [InlineData("""[{ "channel": "sms", "status": "Telepathy", "aliases": ["sms"] }]""")]
    public void Bad_configuration_falls_back_to_the_safe_default_rather_than_failing_the_service(
        string configuration)
    {
        // A typo in an environment variable must not take the orchestrator
        // down, and must not silently make an unavailable channel look
        // available. Falling back to the conservative default does neither.
        var policy = ContactChannelPolicy.FromConfiguration(configuration, out var error);

        Assert.NotNull(error);
        Assert.Equal(
            ContactChannelStatus.KnownUnavailable,
            policy.Assess(["Contact me by SMS"], suppressLightTone: false).Status);
    }

    [Fact]
    public void Absent_configuration_is_not_an_error()
    {
        var policy = ContactChannelPolicy.FromConfiguration(null, out var error);

        Assert.Null(error);
        Assert.Equal(
            ContactChannelStatus.KnownUnavailable,
            policy.Assess(["Contact me by SMS"], suppressLightTone: false).Status);
    }

    [Fact]
    public void Every_status_has_a_snake_case_wire_name_the_agents_can_match_on()
    {
        // The Python fallback path branches on these literals. ToString() would
        // give PascalCase and the branch would silently never fire.
        Assert.Equal("none_stated", ContactChannelStatus.NoneStated.ToWireValue());
        Assert.Equal("supported", ContactChannelStatus.Supported.ToWireValue());
        Assert.Equal("known_unavailable", ContactChannelStatus.KnownUnavailable.ToWireValue());
        Assert.Equal("unsupported", ContactChannelStatus.Unsupported.ToWireValue());
    }
}
