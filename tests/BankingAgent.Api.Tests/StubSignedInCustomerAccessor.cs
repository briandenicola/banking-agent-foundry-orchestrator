using BankingAgent.WebUi;

namespace BankingAgent.Api.Tests;

/// <summary>Anonymous-by-default identity for page models under test.</summary>
internal sealed class StubSignedInCustomerAccessor(SignedInCustomer? customer = null)
    : ISignedInCustomerAccessor
{
    public SignedInCustomer Current { get; } = customer ?? SignedInCustomer.Anonymous;
}
