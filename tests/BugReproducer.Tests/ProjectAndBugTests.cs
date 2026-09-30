using System.Net;
using System.Net.Http.Json;
using BugReproducer.Api.Dtos;

namespace BugReproducer.Tests;

public class ProjectAndBugTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    [Fact]
    public async Task CreateProject_ThenList_ReturnsIt()
    {
        var client = await factory.CreateAuthenticatedClientAsync("projects-list@example.com");

        var created = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Demo Cart App"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);

        var page = await client.GetFromJsonAsync<PagedResult<ProjectResponse>>("/api/projects");
        Assert.Equal(1, page!.TotalCount);
        Assert.Equal("Demo Cart App", page.Items.Single().Name);
    }

    [Fact]
    public async Task CreateBug_ThenGetById_ReturnsIt()
    {
        var client = await factory.CreateAuthenticatedClientAsync("bug-get@example.com");
        var project = await CreateProjectAsync(client, "Cart App");

        var created = await client.PostAsJsonAsync("/api/bugs",
            new CreateBugReportRequest(project.Id, "Cart crashes", "500 when removing an item after re-login."));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var bug = await created.Content.ReadFromJsonAsync<BugReportResponse>();
        var fetched = await client.GetFromJsonAsync<BugReportResponse>($"/api/bugs/{bug!.Id}");

        Assert.Equal(bug.Id, fetched!.Id);
        Assert.Equal("Cart crashes", fetched.Title);
    }

    [Fact]
    public async Task CreateBug_InUnknownProject_ReturnsNotFound()
    {
        var client = await factory.CreateAuthenticatedClientAsync("bug-unknown-project@example.com");

        var response = await client.PostAsJsonAsync("/api/bugs",
            new CreateBugReportRequest(9999, "Title", "Description"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bugs_CanBeFilteredByProject()
    {
        var client = await factory.CreateAuthenticatedClientAsync("bug-filter@example.com");
        var projectA = await CreateProjectAsync(client, "Project A");
        var projectB = await CreateProjectAsync(client, "Project B");

        await client.PostAsJsonAsync("/api/bugs", new CreateBugReportRequest(projectA.Id, "In A", "..."));
        await client.PostAsJsonAsync("/api/bugs", new CreateBugReportRequest(projectB.Id, "In B", "..."));

        var page = await client.GetFromJsonAsync<PagedResult<BugReportResponse>>($"/api/bugs?projectId={projectA.Id}");

        Assert.Equal(1, page!.TotalCount);
        Assert.Equal("In A", page.Items.Single().Title);
    }

    [Fact]
    public async Task Projects_AreInvisibleToOtherUsers()
    {
        var alice = await factory.CreateAuthenticatedClientAsync("alice-isolation@example.com");
        var bob = await factory.CreateAuthenticatedClientAsync("bob-isolation@example.com");
        var project = await CreateProjectAsync(alice, "Alice's project");

        var bobsList = await bob.GetFromJsonAsync<PagedResult<ProjectResponse>>("/api/projects");
        Assert.Equal(0, bobsList!.TotalCount);

        var bobsRead = await bob.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bobsRead.StatusCode);
    }

    [Fact]
    public async Task Bugs_AreInvisibleToOtherUsers()
    {
        var alice = await factory.CreateAuthenticatedClientAsync("alice-bug-isolation@example.com");
        var bob = await factory.CreateAuthenticatedClientAsync("bob-bug-isolation@example.com");
        var project = await CreateProjectAsync(alice, "Alice's project");

        var created = await alice.PostAsJsonAsync("/api/bugs",
            new CreateBugReportRequest(project.Id, "Alice's bug", "..."));
        var bug = await created.Content.ReadFromJsonAsync<BugReportResponse>();

        var bobsRead = await bob.GetAsync($"/api/bugs/{bug!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bobsRead.StatusCode);

        var bobsWrite = await bob.PostAsJsonAsync("/api/bugs",
            new CreateBugReportRequest(project.Id, "Sneaky", "..."));
        Assert.Equal(HttpStatusCode.NotFound, bobsWrite.StatusCode);
    }

    [Fact]
    public async Task List_ClampsPageSizeToMaximum()
    {
        var client = await factory.CreateAuthenticatedClientAsync("page-size@example.com");

        var page = await client.GetFromJsonAsync<PagedResult<ProjectResponse>>("/api/projects?pageSize=5000");

        Assert.Equal(100, page!.PageSize);
    }

    private static async Task<ProjectResponse> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }
}
