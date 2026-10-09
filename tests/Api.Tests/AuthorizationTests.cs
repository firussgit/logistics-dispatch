using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using LogisticsDispatch.Core.Enums;
using LogisticsDispatch.Core.Models;

namespace LogisticsDispatch.Api.Tests;

/// <summary>What each role may and may not do. 401 = not signed in, 403 = wrong role, 404 = not yours.</summary>
public class AuthorizationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpStatusCode> Send(HttpClient c, HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        return (await c.SendAsync(req)).StatusCode;
    }

    private static readonly Guid Anything = Guid.NewGuid();

    /// <summary>The human driver is shared by the tests in this class; hand back anything a previous test left them holding.</summary>
    private async Task FreeHumanDriverAsync(HttpClient dispatcher)
    {
        var human = (await factory.GetDriversAsync(dispatcher)).Single(d => !d.IsAutomated);
        var jobs = await dispatcher.GetFromJsonAsync<List<JobDto>>("/api/jobs", ApiFactory.Json);
        foreach (var j in jobs!.Where(j => j.DriverId == human.Id))
        {
            if (j.Status == JobStatus.Assigned) await dispatcher.PostAsync($"/api/jobs/{j.Id}/cancel", null);
            else if (j.Status == JobStatus.InTransit) await dispatcher.PostAsync($"/api/jobs/{j.Id}/complete", null);
        }
    }

    // ---------------------------------------------------------------- customers

    [Fact]
    public async Task Customers_are_kept_out_of_dispatcher_and_driver_endpoints()
    {
        var (customer, _) = await factory.RegisterCustomerAsync();
        var job = await factory.CreateJobAsync(customer);

        foreach (var url in new[] { "/api/drivers", "/api/status", "/api/offers", "/api/routing" })
            Assert.Equal(HttpStatusCode.Forbidden, await Send(customer, HttpMethod.Get, url));

        Assert.Equal(HttpStatusCode.Forbidden, await Send(customer, HttpMethod.Post, $"/api/jobs/{job.Id}/assign", new { driverId = Anything }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(customer, HttpMethod.Post, $"/api/jobs/{job.Id}/offer", new { driverId = Anything }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(customer, HttpMethod.Post, $"/api/jobs/{job.Id}/accept"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(customer, HttpMethod.Post, $"/api/jobs/{job.Id}/complete"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(customer, HttpMethod.Post, $"/api/offers/{Anything}/accept"));
    }

    [Fact]
    public async Task A_customers_order_is_always_in_their_own_name_and_owned_by_them()
    {
        var (customer, me) = await factory.RegisterCustomerAsync("Real Name");

        var res = await customer.PostAsJsonAsync("/api/jobs", ApiFactory.NewJobBody("Someone Else Entirely"));
        var job = await res.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("Real Name", job!.CustomerName);
        Assert.Equal(me.Id, job.CustomerId);
        Assert.Matches("^[0-9a-f]{32}$", job.TrackingToken!);
    }

    [Fact]
    public async Task A_customer_sees_only_their_own_orders_and_cannot_reach_anyone_elses()
    {
        var (alice, _) = await factory.RegisterCustomerAsync("Alice");
        var (bob, _) = await factory.RegisterCustomerAsync("Bob");
        var alices = await factory.CreateJobAsync(alice);
        var bobs = await factory.CreateJobAsync(bob);

        var aliceList = await alice.GetFromJsonAsync<List<JobDto>>("/api/jobs", ApiFactory.Json);
        Assert.Contains(aliceList!, j => j.Id == alices.Id);
        Assert.DoesNotContain(aliceList!, j => j.Id == bobs.Id);
        Assert.All(aliceList!, j => Assert.Equal("Alice", j.CustomerName));

        // Bob's order is a 404 for Alice, on every route, so ids can't be probed
        Assert.Equal(HttpStatusCode.NotFound, await Send(alice, HttpMethod.Get, $"/api/jobs/{bobs.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(alice, HttpMethod.Get, $"/api/jobs/{bobs.Id}/route"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(alice, HttpMethod.Post, $"/api/jobs/{bobs.Id}/cancel"));

        // and her own are fine
        Assert.Equal(HttpStatusCode.OK, await Send(alice, HttpMethod.Get, $"/api/jobs/{alices.Id}"));
        Assert.Equal(HttpStatusCode.OK, await Send(alice, HttpMethod.Get, $"/api/jobs/{alices.Id}/route"));
    }

    [Fact]
    public async Task A_customer_can_cancel_their_own_open_order_but_not_a_delivered_one()
    {
        var (customer, _) = await factory.RegisterCustomerAsync();
        var open = await factory.CreateJobAsync(customer);
        Assert.Equal(HttpStatusCode.OK, await Send(customer, HttpMethod.Post, $"/api/jobs/{open.Id}/cancel"));

        var dispatcher = factory.DispatcherClient();
        var delivered = await factory.CreateJobAsync(customer);
        var driver = (await factory.GetDriversAsync(dispatcher)).First(d => d.Status == DriverStatus.Idle);
        await dispatcher.PostAsJsonAsync($"/api/jobs/{delivered.Id}/assign", new { driverId = driver.Id });
        await dispatcher.PostAsync($"/api/jobs/{delivered.Id}/accept", null);
        await dispatcher.PostAsync($"/api/jobs/{delivered.Id}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, await Send(customer, HttpMethod.Post, $"/api/jobs/{delivered.Id}/cancel"));
    }

    [Fact]
    public async Task Dispatchers_see_every_customers_orders()
    {
        var (customer, _) = await factory.RegisterCustomerAsync("Visible");
        var job = await factory.CreateJobAsync(customer);

        var all = await factory.DispatcherClient().GetFromJsonAsync<List<JobDto>>("/api/jobs", ApiFactory.Json);

        Assert.Contains(all!, j => j.Id == job.Id);
    }

    [Fact]
    public async Task A_dispatcher_must_name_the_customer_when_booking_on_their_behalf()
    {
        var dispatcher = factory.DispatcherClient();
        var res = await dispatcher.PostAsJsonAsync("/api/jobs", new
        {
            pickup = new { lat = 40.7128, lng = -74.0060 },
            dropoff = new { lat = 40.7580, lng = -73.9855 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var job = await factory.CreateJobAsync(dispatcher, "Phone Order");
        Assert.Null(job.CustomerId); // booked by staff, owned by nobody
    }

    // ------------------------------------------------------------------ drivers

    [Fact]
    public async Task Drivers_cannot_book_assign_or_see_the_board()
    {
        var driver = factory.DriverClient();
        var dispatcher = factory.DispatcherClient();
        await FreeHumanDriverAsync(dispatcher);
        var job = await factory.CreateJobAsync(dispatcher);

        Assert.Equal(HttpStatusCode.Forbidden, await Send(driver, HttpMethod.Post, "/api/jobs", ApiFactory.NewJobBody()));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(driver, HttpMethod.Get, "/api/drivers"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(driver, HttpMethod.Get, "/api/status"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(driver, HttpMethod.Get, "/api/routing"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(driver, HttpMethod.Post, $"/api/jobs/{job.Id}/assign", new { driverId = Anything }));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(driver, HttpMethod.Post, $"/api/jobs/{job.Id}/cancel"));
    }

    [Fact]
    public async Task A_driver_only_sees_their_own_offers_even_if_they_ask_for_someone_elses()
    {
        var dispatcher = factory.DispatcherClient();
        await FreeHumanDriverAsync(dispatcher);
        var drivers = await factory.GetDriversAsync(dispatcher);
        var me = drivers.Single(d => !d.IsAutomated);
        var other = drivers.First(d => d.IsAutomated && d.Status == DriverStatus.Idle);

        var forOther = await factory.CreateJobAsync(dispatcher, "Other");
        var forMe = await factory.CreateJobAsync(dispatcher, "Mine");
        await dispatcher.PostAsJsonAsync($"/api/jobs/{forOther.Id}/offer", new { driverId = other.Id });
        await dispatcher.PostAsJsonAsync($"/api/jobs/{forMe.Id}/offer", new { driverId = me.Id });

        var driver = factory.DriverClient();
        var seen = await driver.GetFromJsonAsync<List<OfferDto>>($"/api/offers?status=all&driverId={other.Id}", ApiFactory.Json);

        Assert.Contains(seen!, o => o.JobId == forMe.Id);
        Assert.DoesNotContain(seen!, o => o.JobId == forOther.Id);
        Assert.All(seen!, o => Assert.Equal(me.Id, o.DriverId));
    }

    [Fact]
    public async Task A_driver_answers_their_own_offer_but_not_another_drivers()
    {
        var dispatcher = factory.DispatcherClient();
        await FreeHumanDriverAsync(dispatcher);
        var drivers = await factory.GetDriversAsync(dispatcher);
        var me = drivers.Single(d => !d.IsAutomated);
        var other = drivers.First(d => d.IsAutomated && d.Status == DriverStatus.Idle);

        var jobOther = await factory.CreateJobAsync(dispatcher, "Not yours");
        var otherOffer = await (await dispatcher.PostAsJsonAsync($"/api/jobs/{jobOther.Id}/offer", new { driverId = other.Id })).Content.ReadFromJsonAsync<OfferDto>(ApiFactory.Json);
        var jobMine = await factory.CreateJobAsync(dispatcher, "Yours");
        var myOffer = await (await dispatcher.PostAsJsonAsync($"/api/jobs/{jobMine.Id}/offer", new { driverId = me.Id })).Content.ReadFromJsonAsync<OfferDto>(ApiFactory.Json);

        var driver = factory.DriverClient();
        Assert.Equal(HttpStatusCode.NotFound, await Send(driver, HttpMethod.Post, $"/api/offers/{otherOffer!.Id}/accept"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(driver, HttpMethod.Post, $"/api/offers/{otherOffer.Id}/decline"));
        Assert.Equal(HttpStatusCode.OK, await Send(driver, HttpMethod.Post, $"/api/offers/{myOffer!.Id}/accept"));

        // the other driver's offer was left untouched
        var stillOpen = await dispatcher.GetFromJsonAsync<List<OfferDto>>("/api/offers", ApiFactory.Json);
        Assert.Contains(stillOpen!, o => o.Id == otherOffer.Id && o.Status == OfferStatus.Pending);
    }

    [Fact]
    public async Task A_driver_sees_and_works_only_the_jobs_assigned_to_them()
    {
        var dispatcher = factory.DispatcherClient();
        await FreeHumanDriverAsync(dispatcher);
        var me = (await factory.GetDriversAsync(dispatcher)).Single(d => !d.IsAutomated);
        // make sure the human driver is free
        var mine = await factory.CreateJobAsync(dispatcher, "Mine");
        var notMine = await factory.CreateJobAsync(dispatcher, "Not mine");
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{mine.Id}/assign", new { driverId = me.Id })).EnsureSuccessStatusCode();

        var driver = factory.DriverClient();
        var list = await driver.GetFromJsonAsync<List<JobDto>>("/api/jobs", ApiFactory.Json);
        Assert.Contains(list!, j => j.Id == mine.Id);
        Assert.DoesNotContain(list!, j => j.Id == notMine.Id);

        Assert.Equal(HttpStatusCode.NotFound, await Send(driver, HttpMethod.Get, $"/api/jobs/{notMine.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(driver, HttpMethod.Post, $"/api/jobs/{notMine.Id}/accept"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(driver, HttpMethod.Post, $"/api/jobs/{notMine.Id}/complete"));

        Assert.Equal(HttpStatusCode.OK, await Send(driver, HttpMethod.Get, $"/api/jobs/{mine.Id}"));
        Assert.Equal(HttpStatusCode.OK, await Send(driver, HttpMethod.Post, $"/api/jobs/{mine.Id}/accept"));
        Assert.Equal(HttpStatusCode.OK, await Send(driver, HttpMethod.Post, $"/api/jobs/{mine.Id}/complete"));
    }

    [Fact]
    public async Task A_different_driver_account_cannot_touch_the_first_drivers_job()
    {
        var dispatcher = factory.DispatcherClient();
        await FreeHumanDriverAsync(dispatcher);
        var drivers = await factory.GetDriversAsync(dispatcher);
        var human = drivers.Single(d => !d.IsAutomated);
        var otherDriver = drivers.First(d => d.IsAutomated && d.Status == DriverStatus.Idle);
        var email = await factory.CreateDriverAccountAsync(otherDriver.Id);
        var intruder = factory.LoginClient(email);

        var job = await factory.CreateJobAsync(dispatcher, "Humans only");
        (await dispatcher.PostAsJsonAsync($"/api/jobs/{job.Id}/assign", new { driverId = human.Id })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, await Send(intruder, HttpMethod.Get, $"/api/jobs/{job.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(intruder, HttpMethod.Post, $"/api/jobs/{job.Id}/accept"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(intruder, HttpMethod.Post, $"/api/jobs/{job.Id}/complete"));
    }

    // --------------------------------------------------------------- public tracking

    [Fact]
    public async Task Anyone_with_the_token_can_track_a_delivery_without_an_account()
    {
        var (customer, _) = await factory.RegisterCustomerAsync("Tracked Person");
        var job = await factory.CreateJobAsync(customer);
        var anonymous = factory.CreateClient();

        var res = await anonymous.GetAsync($"/api/track/{job.TrackingToken}");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var tracking = System.Text.Json.JsonSerializer.Deserialize<TrackingDto>(body, ApiFactory.Json)!;
        Assert.Equal(job.Reference, tracking.Reference);
        Assert.Equal(JobStatus.Pending, tracking.Status);
        Assert.Equal(job.Id, tracking.Id);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/track/{job.TrackingToken}/route")).StatusCode);

        // the link reveals the delivery, not the person or the account behind it
        Assert.DoesNotContain("Tracked Person", body);
        Assert.DoesNotContain("customerId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trackingToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("00000000000000000000000000000000")]            // right shape, no such job
    [InlineData("0000000000000000000000000000000000000000")]    // too long
    [InlineData("'; DROP TABLE Jobs;--")]
    public async Task Unknown_or_malformed_tokens_are_a_plain_404(string token)
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/track/{Uri.EscapeDataString(token)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/track/{Uri.EscapeDataString(token)}/route")).StatusCode);
    }

    [Fact]
    public async Task Tracking_tokens_are_unique_random_and_not_derived_from_the_job_id()
    {
        var dispatcher = factory.DispatcherClient();
        var jobs = new List<JobDto>();
        for (var i = 0; i < 12; i++) jobs.Add(await factory.CreateJobAsync(dispatcher, $"T{i}"));

        var tokens = jobs.Select(j => j.TrackingToken!).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Matches("^[0-9a-f]{32}$", t));
        Assert.All(jobs, j => Assert.NotEqual(j.Id.ToString("N"), j.TrackingToken));
    }

    [Fact]
    public async Task The_job_id_alone_is_not_a_tracking_link()
    {
        var dispatcher = factory.DispatcherClient();
        var job = await factory.CreateJobAsync(dispatcher);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/track/{job.Id:N}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/jobs/{job.Id}")).StatusCode);
    }
}
