using JustRemotePhone.RemotePhoneService;
using SMS_Bridge.SmsProviders;
using Xunit;

namespace SMS_Bridge.Tests;

public class PhoneLinkRuleTests
{
    [Theory]
    [InlineData(ApplicationState.Connected, PhoneState.Idle, true)]
    [InlineData(ApplicationState.Connected, PhoneState.Ringing, true)]
    [InlineData(ApplicationState.Connected, PhoneState.OffHook, true)]
    [InlineData(ApplicationState.Connected, PhoneState.Unknown, false)]
    [InlineData(ApplicationState.Disconnected, PhoneState.Idle, false)]
    [InlineData(ApplicationState.Connecting, PhoneState.Unknown, false)]
    public void Up_only_when_connected_and_the_phone_is_known(ApplicationState application, PhoneState phone, bool up)
    {
        Assert.Equal(up, PhoneLink.IsUp(application, phone));
    }

    [Theory]
    [InlineData(ApplicationState.Connected, PhoneState.Unknown, 179, true)]
    [InlineData(ApplicationState.Connected, PhoneState.Unknown, 181, false)]
    [InlineData(ApplicationState.Connected, PhoneState.Idle, 0, true)]
    [InlineData(ApplicationState.Connecting, PhoneState.Unknown, 10, false)]
    public void Phone_status_reports_a_brief_unknown_as_up(ApplicationState application, PhoneState phone, int unknownSeconds, bool up)
    {
        Assert.Equal(up, PhoneLink.ReportsUp(application, phone, TimeSpan.FromSeconds(unknownSeconds)));
    }
}
