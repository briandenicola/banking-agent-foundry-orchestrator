using BankingAgent.WebUi;

namespace BankingAgent.WebUi.Tests;

/// <summary>
/// Supplies a fixed identity to page models under test. Defaults to anonymous,
/// which is the behaviour of a deployment without user authentication.
/// </summary>
internal sealed class StubSignedInCustomerAccessor(SignedInCustomer? customer = null)
    : ISignedInCustomerAccessor
{
    public SignedInCustomer Current { get; } = customer ?? SignedInCustomer.Anonymous;
}
