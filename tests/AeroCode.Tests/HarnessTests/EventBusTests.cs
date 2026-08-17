// Copyright (c) AeroCode V3.0
// EventBus tests.
using AeroCode.Harness.EventBus;
using Xunit;

namespace AeroCode.Tests.HarnessTests;

public class EventBusTests
{
    [Fact]
    public void Publish_DeliversToSubscriber()
    {
        var bus = new EventBus();
        ToolCallEvent? received = null;
        bus.Subscribe<ToolCallEvent>(e => received = e);
        bus.Publish(new ToolCallEvent("test_tool", new Dictionary<string, object?>(), DateTime.UtcNow));
        Assert.NotNull(received);
        Assert.Equal("test_tool", received!.ToolName);
    }

    [Fact]
    public void Publish_NoSubscriber_NoError()
    {
        var bus = new EventBus();
        bus.Publish(new ToolCallEvent("test", new Dictionary<string, object?>(), DateTime.UtcNow));
        // Just verify no exception.
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var bus = new EventBus();
        var count = 0;
        var unsub = bus.Subscribe<ToolCallEvent>(_ => count++);
        bus.Publish(new ToolCallEvent("a", null, DateTime.UtcNow));
        Assert.Equal(1, count);
        unsub();
        bus.Publish(new ToolCallEvent("b", null, DateTime.UtcNow));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Publish_HandlerThrows_DoesNotBreakOthers()
    {
        var bus = new EventBus();
        var count = 0;
        bus.Subscribe<ToolCallEvent>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<ToolCallEvent>(_ => count++);
        bus.Publish(new ToolCallEvent("a", null, DateTime.UtcNow));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Publish_MultipleSubscribers_AllReceive()
    {
        var bus = new EventBus();
        var a = 0; var b = 0;
        bus.Subscribe<ToolCallEvent>(_ => a++);
        bus.Subscribe<ToolCallEvent>(_ => b++);
        bus.Publish(new ToolCallEvent("x", null, DateTime.UtcNow));
        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }
}
