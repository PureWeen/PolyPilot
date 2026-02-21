using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class RemoteRepoTests
{
    [Fact]
    public void BridgeMessageTypes_RepoConstants_Defined()
    {
        Assert.Equal("add_repo", BridgeMessageTypes.AddRepo);
        Assert.Equal("remove_repo", BridgeMessageTypes.RemoveRepo);
        Assert.Equal("list_repos", BridgeMessageTypes.ListRepos);
        Assert.Equal("repos_list", BridgeMessageTypes.ReposList);
        Assert.Equal("repo_added", BridgeMessageTypes.RepoAdded);
        Assert.Equal("repo_progress", BridgeMessageTypes.RepoProgress);
        Assert.Equal("repo_error", BridgeMessageTypes.RepoError);
    }

    [Fact]
    public void AddRepoPayload_RoundTrip()
    {
        var payload = new AddRepoPayload { Url = "https://github.com/PureWeen/PolyPilot", RequestId = "abc123" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.AddRepo, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(BridgeMessageTypes.AddRepo, restored!.Type);

        var p = restored.GetPayload<AddRepoPayload>();
        Assert.NotNull(p);
        Assert.Equal("https://github.com/PureWeen/PolyPilot", p!.Url);
        Assert.Equal("abc123", p.RequestId);
    }

    [Fact]
    public void RemoveRepoPayload_RoundTrip()
    {
        var payload = new RemoveRepoPayload { RepoId = "PureWeen-PolyPilot", DeleteFromDisk = true, GroupId = "group1" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.RemoveRepo, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        var p = restored!.GetPayload<RemoveRepoPayload>();
        Assert.NotNull(p);
        Assert.Equal("PureWeen-PolyPilot", p!.RepoId);
        Assert.True(p.DeleteFromDisk);
        Assert.Equal("group1", p.GroupId);
    }

    [Fact]
    public void ReposListPayload_RoundTrip()
    {
        var payload = new ReposListPayload
        {
            RequestId = "req1",
            Repos = new()
            {
                new RepoSummary { Id = "PureWeen-PolyPilot", Name = "PolyPilot", Url = "https://github.com/PureWeen/PolyPilot" },
                new RepoSummary { Id = "dotnet-maui", Name = "maui", Url = "https://github.com/dotnet/maui" }
            }
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ReposList, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        var p = restored!.GetPayload<ReposListPayload>();
        Assert.NotNull(p);
        Assert.Equal(2, p!.Repos.Count);
        Assert.Equal("PureWeen-PolyPilot", p.Repos[0].Id);
        Assert.Equal("maui", p.Repos[1].Name);
    }

    [Fact]
    public void RepoAddedPayload_RoundTrip()
    {
        var payload = new RepoAddedPayload
        {
            RequestId = "req1",
            RepoId = "PureWeen-PolyPilot",
            RepoName = "PolyPilot",
            Url = "https://github.com/PureWeen/PolyPilot"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.RepoAdded, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        var p = restored!.GetPayload<RepoAddedPayload>();
        Assert.NotNull(p);
        Assert.Equal("req1", p!.RequestId);
        Assert.Equal("PureWeen-PolyPilot", p.RepoId);
        Assert.Equal("PolyPilot", p.RepoName);
    }

    [Fact]
    public void RepoProgressPayload_RoundTrip()
    {
        var payload = new RepoProgressPayload { RequestId = "req1", Message = "Cloning 45%..." };
        var msg = BridgeMessage.Create(BridgeMessageTypes.RepoProgress, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        var p = restored!.GetPayload<RepoProgressPayload>();
        Assert.NotNull(p);
        Assert.Equal("req1", p!.RequestId);
        Assert.Equal("Cloning 45%...", p.Message);
    }

    [Fact]
    public void RepoErrorPayload_RoundTrip()
    {
        var payload = new RepoErrorPayload { RequestId = "req1", Error = "Authentication failed" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.RepoError, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        var p = restored!.GetPayload<RepoErrorPayload>();
        Assert.NotNull(p);
        Assert.Equal("req1", p!.RequestId);
        Assert.Equal("Authentication failed", p.Error);
    }

    [Fact]
    public void ListReposPayload_RoundTrip()
    {
        var payload = new ListReposPayload { RequestId = "req1" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.ListRepos, payload);
        var json = msg.Serialize();
        var restored = BridgeMessage.Deserialize(json);

        Assert.Equal(BridgeMessageTypes.ListRepos, restored!.Type);
        var p = restored.GetPayload<ListReposPayload>();
        Assert.NotNull(p);
        Assert.Equal("req1", p!.RequestId);
    }

    [Fact]
    public void RepoSummary_DefaultValues()
    {
        var summary = new RepoSummary();
        Assert.Equal("", summary.Id);
        Assert.Equal("", summary.Name);
        Assert.Equal("", summary.Url);
    }

    [Fact]
    public void StubBridgeClient_AddRepo_TracksCall()
    {
        var stub = new StubWsBridgeClient();
        var result = stub.AddRepoAsync("https://github.com/dotnet/maui").Result;

        Assert.Equal(1, stub.AddRepoCallCount);
        Assert.Equal("https://github.com/dotnet/maui", stub.LastAddedRepoUrl);
        Assert.Equal("maui", result.RepoId);
    }

    [Fact]
    public void StubBridgeClient_RemoveRepo_TracksCall()
    {
        var stub = new StubWsBridgeClient();
        stub.RemoveRepoAsync("dotnet-maui", true, "group1").Wait();

        Assert.Equal(1, stub.RemoveRepoCallCount);
        Assert.Equal("dotnet-maui", stub.LastRemovedRepoId);
    }

    [Fact]
    public void StubBridgeClient_RequestRepos_TracksCall()
    {
        var stub = new StubWsBridgeClient();
        stub.RequestReposAsync().Wait();

        Assert.Equal(1, stub.RequestReposCallCount);
    }
}
