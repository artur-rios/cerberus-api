using System.Reflection;
using ArturRios.Cerberus.Data.Erasure;

namespace ArturRios.Cerberus.Data.Tests;

public sealed class RetentionExpiryBoundaryTests
{
    // Exercise the shared production policy with fixed UTC values; a live database
    // clock cannot remain at an exact microsecond while a client statement travels.
    private static bool Due(DateTimeOffset? start,DateTimeOffset? end,DateTimeOffset now)
    {
        var policy=typeof(TrashExpiryHandler).Assembly.GetType("ArturRios.Cerberus.Data.Erasure.RetentionExpiry",true)!;
        return (bool)policy.GetMethod("Due",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[start,end,now])!;
    }
    [UnitTheory][InlineData(-10L,false)][InlineData(0L,true)][InlineData(10L,true)]
    public void GivenOriginalThirtyDayDeadline_WhenCheckingEachBoundary_ThenExpiryBeginsAtDeadline(long offset,bool expected)
    {
        var start=new DateTimeOffset(2026,9,15,12,0,0,TimeSpan.Zero);var deadline=new DateTimeOffset(2026,10,15,12,0,0,TimeSpan.Zero);
        Assert.Equal(expected,Due(start,deadline,deadline.AddTicks(offset)));
    }
    [UnitTheory][InlineData("missingStart")][InlineData("missingEnd")][InlineData("short")][InlineData("long")]
    [InlineData("nonUtc")][InlineData("precision")][InlineData("beforeEpoch")]
    public void GivenMalformedOriginalRetentionWindow_WhenCheckingExpiry_ThenFailClosed(string damage)
    {
        DateTimeOffset? start=new DateTimeOffset(2026,9,15,12,0,0,TimeSpan.Zero);DateTimeOffset? end=new DateTimeOffset(2026,10,15,12,0,0,TimeSpan.Zero);var now=end.Value.AddDays(100);
        if(damage=="missingStart")start=null;if(damage=="missingEnd")end=null;
        if(damage=="short")start=start!.Value.AddHours(1);if(damage=="long")start=start!.Value.AddHours(-1);
        if(damage=="nonUtc")start=start!.Value.ToOffset(TimeSpan.FromHours(1));
        if(damage=="precision"){start=start!.Value.AddTicks(1);end=end!.Value.AddTicks(1);}
        if(damage=="beforeEpoch"){start=DateTimeOffset.UnixEpoch.AddDays(-1);end=DateTimeOffset.UnixEpoch.AddDays(29);}
        Assert.False(Due(start,end,now));
    }
}
