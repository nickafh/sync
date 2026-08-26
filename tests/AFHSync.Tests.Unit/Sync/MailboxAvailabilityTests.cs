using AFHSync.Worker.Services;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Tests.Unit.Sync;

public class MailboxAvailabilityTests
{
    [Fact]
    public void IsUnavailable_TrueForODataErrorCode()
    {
        var ex = new ODataError { Error = new MainError { Code = "MailboxNotEnabledForRESTAPI", Message = "REST API is not yet supported for this mailbox." } };
        Assert.True(MailboxAvailability.IsUnavailable(ex));
    }

    [Fact]
    public void IsUnavailable_TrueForODataErrorMessageFragment()
    {
        var ex = new ODataError { Error = new MainError { Code = "ErrorItemNotFound", Message = "The mailbox is either inactive, soft-deleted, or is hosted on-premise." } };
        Assert.True(MailboxAvailability.IsUnavailable(ex));
    }

    [Fact]
    public void IsUnavailable_TrueForPlainExceptionWithFragment()
    {
        var ex = new InvalidOperationException("Graph said: the mailbox is either INACTIVE, SOFT-DELETED, OR IS HOSTED ON-PREMISE.");
        Assert.True(MailboxAvailability.IsUnavailable(ex));
    }

    [Fact]
    public void IsUnavailable_FalseForOtherErrors()
    {
        Assert.False(MailboxAvailability.IsUnavailable(new ODataError { Error = new MainError { Code = "ErrorAccessDenied", Message = "Access is denied." } }));
        Assert.False(MailboxAvailability.IsUnavailable(new InvalidOperationException("boom")));
    }
}
