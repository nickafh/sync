using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class TargetScopeValidationTests
{
    [Fact]
    public void AllUsers_IsValid()
        => Assert.Null(TargetScopeValidation.Validate(null, null));

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"\", \"   \"]")]
    [InlineData("null")]
    public void EmptyEmails_IsRejected(string json)
        => Assert.Equal(TargetScopeValidation.EmptyUsersMessage, TargetScopeValidation.Validate(json, null));

    [Fact]
    public void NonEmptyEmails_IsValid()
        => Assert.Null(TargetScopeValidation.Validate("[\"a@contoso.com\"]", null));

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"emails\":[]}")]
    public void UnparseableEmails_IsRejected(string json)
        => Assert.Equal(TargetScopeValidation.InvalidEmailsJsonMessage, TargetScopeValidation.Validate(json, null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyGroupId_IsRejected(string groupId)
        => Assert.Equal(TargetScopeValidation.EmptyGroupMessage, TargetScopeValidation.Validate(null, groupId));

    [Fact]
    public void GroupId_IsValid()
        => Assert.Null(TargetScopeValidation.Validate(null, "11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void BothScopes_IsRejected()
        => Assert.Equal(TargetScopeValidation.BothScopesMessage,
            TargetScopeValidation.Validate("[\"a@contoso.com\"]", "11111111-2222-3333-4444-555555555555"));
}
