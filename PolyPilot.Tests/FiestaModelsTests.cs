using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class FiestaModelsTests
{
    [Fact]
    public void FiestaOrganizationState_DefaultGroup_IsPresent()
    {
        var state = new FiestaOrganizationState();

        var group = Assert.Single(state.Groups);
        Assert.Equal(FiestaGroup.DefaultId, group.Id);
        Assert.Equal(FiestaGroup.DefaultName, group.Name);
        Assert.Equal(0, group.SortOrder);
    }

    [Fact]
    public void FiestaStateStore_LegacyJsonWithoutWorkerFields_DeserializesWithDefaults()
    {
        const string legacyJson = """
        {
          "Rooms": [
            {
              "Id": "room-1",
              "Name": "Legacy Fiesta",
              "OrganizerInstanceId": "org-1",
              "OrganizerMachineName": "Legacy-Mac"
            }
          ]
        }
        """;

        var state = JsonSerializer.Deserialize<FiestaStateStore>(legacyJson);

        Assert.NotNull(state);
        Assert.Single(state!.Rooms);
        Assert.NotNull(state.RegisteredWorkers);
        Assert.Empty(state.RegisteredWorkers);
        Assert.NotNull(state.TrustedOrganizers);
        Assert.Empty(state.TrustedOrganizers);
        Assert.NotNull(state.TrustedOrganizerRecords);
        Assert.Empty(state.TrustedOrganizerRecords);
        Assert.NotNull(state.Organization);
        Assert.Contains(state.Organization.Groups, g => g.Id == FiestaGroup.DefaultId);
    }

    [Fact]
    public void FiestaJoinRequest_Defaults_ToPendingAndNotAutoApproved()
    {
        var request = new FiestaJoinRequest();

        Assert.Equal(FiestaJoinState.Pending, request.Status);
        Assert.False(request.AutoApproved);
    }

    [Fact]
    public void FiestaStateStore_TrustedOrganizerRecords_RoundTrip()
    {
        var state = new FiestaStateStore
        {
            TrustedOrganizerRecords = new List<FiestaTrustedOrganizer>
            {
                new()
                {
                    OrganizerInstanceId = "org-1",
                    TrustToken = "token-1"
                }
            }
        };

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<FiestaStateStore>(json);

        Assert.NotNull(restored);
        var record = Assert.Single(restored!.TrustedOrganizerRecords);
        Assert.Equal("org-1", record.OrganizerInstanceId);
        Assert.Equal("token-1", record.TrustToken);
    }
}
