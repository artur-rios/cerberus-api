using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Tests;

public class AccountDeletionTests
{
    [UnitTheory][InlineData(0,false)][InlineData(-1,false)][InlineData(1,true)]
    [InlineData(9007199254740991,true)][InlineData(9007199254740992,false)]
    public void GivenDeletionRevision_WhenValidating_ThenRequirePositiveSafeInteger(long revision,bool valid)
        =>Assert.Equal(valid,new AccountDeletionInput(revision).IsValid());

    [UnitTheory][InlineData("{}")][InlineData("{\"expectedRevision\":\"1\"}")]
    [InlineData("{\"expectedRevision\":1,\"owner\":\"forged\"}")]
    public void GivenInvalidDeletionJson_WhenReading_ThenRejectMissingOrForgedInput(string json)
        =>Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<AccountDeletionInput>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [UnitTheory][InlineData("completed",true)][InlineData("pending",true)][InlineData("active",false)]
    [InlineData("id",false)][InlineData("offset",false)][InlineData("precision",false)][InlineData("epoch",false)]
    public void GivenErasureResult_WhenValidating_ThenRequireTypedIdentityMinimalStateAndUtcTime(string kind,bool valid)
    {
        var time=DateTimeOffset.Parse("2026-10-15T04:00:00Z");
        var value=new AccountDeletionDetails(Guid.NewGuid(),time,kind is "pending"?"pending":"completed");
        value=kind switch{"active"=>value with{State="active"},"id"=>value with{AccountId=Guid.Empty},
            "offset"=>value with{DeletedAt=time.ToOffset(TimeSpan.FromHours(1))},"precision"=>value with{DeletedAt=time.AddTicks(1)},
            "epoch"=>value with{DeletedAt=DateTimeOffset.UnixEpoch.AddTicks(-10)},_=>value};
        Assert.Equal(valid,value.IsValid());
    }
}
