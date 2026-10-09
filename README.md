# Real-Time Logistics & Order Dispatch Engine

A dispatch system where delivery jobs move through a lifecycle, a background simulation drives fake
drivers along their routes, and every change is pushed to browsers over SignalR — no client polling.

```
Pending ──assign──▶ Assigned ──accept──▶ InTransit ──arrive──▶ Completed
   └──cancel──▶ Cancelled ◀──cancel──┘
```

## Run

```bash
dotnet run --project src/Api
```

Open <http://localhost:5080> (dispatcher console with a live map). Click a job reference to open its customer tracking page (`track.html?id=…`). Create a job (try **Random nearby**) and watch it get auto-assigned to the
nearest idle driver and drive to its destination. Open a second tab to see both update live.
The SQLite database (`dispatch.db`) is created and seeded with 5 drivers on first start.

## Street routes

Drivers follow real roads: each job gets a pickup→dropoff path (and an approach path from the driver's position at assignment) from a
self-hosted [OSRM](https://project-osrm.org/) server, stored on the job and drawn on every map. The dispatcher header badge shows
**Street routes** or **Straight-line routes**. If OSRM isn't running the app falls back to straight lines automatically.
Setup (needs Docker Desktop): `./routing/setup.ps1` - see [routing/README.md](routing/README.md).

## Driver offers

With `Simulation:UseOffers` on (the default in `appsettings.json`) jobs are no longer assigned instantly. Each Pending job is
**offered** to the nearest idle driver, who has `OfferTimeout` (20 s) to answer:

- **Accept** → job assigned, driver busy, any competing offers withdrawn.
- **Decline** or **no answer** → the offer closes and the job goes to the next-nearest driver. That driver is skipped for that
  job for `DeclineCooldown` (30 s), so offers cascade through the fleet instead of ping-ponging.
- A dispatcher assigning or cancelling the job withdraws any open offer.

Five drivers are simulated and answer on their own (2–6 s, ~75 % accept). **"You (Demo Driver)"** is human: open
`driver.html` (linked from the dispatcher's driver list) to receive offers live, with payout, distances and a countdown.
Turn `UseOffers` off to get the old instant auto-assign.

## Test

```bash
dotnet test
```

- `Core.Tests` — state-machine transition matrix, geo math, `DispatchService` rules (fake repositories).
- `Api.Tests` — real app via `WebApplicationFactory` on a throwaway SQLite file: REST validation/errors,
  concurrent-assignment races, the simulation worker end-to-end, and SignalR hub events.

## Architecture

| Project | Responsibility |
|---|---|
| `src/Core` | Entities, **State pattern** (`States/`), `DispatchService` use-cases, repository + notifier abstractions. No dependencies. |
| `src/Infrastructure` | EF Core/SQLite, **Repository pattern**, **Hub pattern** (`DispatchHub`, `SignalRDispatchNotifier`), `SimulationWorker`. |
| `src/Api` | Controllers, validation, ProblemDetails middleware, static dashboard in `wwwroot`. |

Key decisions:

- **Notify after save.** `DispatchService` mutates → `SaveChanges` → publishes via `IDispatchNotifier`, so clients never see state that failed to commit. Broadcast failures are logged, never thrown.
- **Optimistic concurrency.** `Job` and `Driver` carry a `Version` token bumped in `DispatchDbContext.SaveChangesAsync`. Two requests racing to assign the same job/driver: one wins (200), the other gets 409.
- **Scoped DbContext in a singleton worker.** `SimulationWorker` uses `IServiceScopeFactory` and a fresh scope per operation, so one failed job can't poison the rest of a tick.
- **Time is injected** (`TimeProvider`), and the simulation is configurable (`Simulation` section in `appsettings.json`: tick interval, speed, `TimeScale`, auto-assign, pickup dwell).

## API

| Route | Description |
|---|---|
| `POST /api/jobs` | Create a job (validated: coordinates in range, pickup ≠ dropoff) |
| `GET /api/jobs?status=` · `GET /api/jobs/{id}` | List / detail with status history |
| `POST /api/jobs/{id}/assign` `{driverId}` | Assign a driver (409 on busy driver, wrong state, or lost race) |
| `POST /api/jobs/{id}/accept` · `/complete` · `/cancel` | Lifecycle transitions |
| `POST /api/jobs/{id}/offer` `{driverId}` | Dispatcher manually offers a Pending job to one driver |
| `GET /api/offers?status=| `GET /api/drivers` · `GET /api/status` | Drivers; live counts by status |driverId=` | Open offers by default (`status=all` for history) |
| `POST /api/offers/{id}/accept` · `/decline` | Driver answers an offer (409 if expired/withdrawn/already answered) |
| `GET /api/jobs/{id}/route` | Road paths `{approach, trip}` as `[lat,lng]` pairs (null until computed) |
| `GET /api/routing` | Configured routing engine and whether it is answering |
| `GET /api/drivers` · `GET /api/status` | Drivers; live counts by status |

Hub: `/hubs/dispatch` — methods `JoinDispatchGroup(group)`, `LeaveDispatchGroup(group)`, `SendStatusUpdate(update)`;
events `JobCreated`, `JobStatusChanged`, `JobProgress`, `DriverUpdated`, `OfferCreated`, `OfferUpdated`, `RouteReady`. Sample requests: `src/Api/Api.http`.

## Switching to SQL Server

Replace `UseSqlite` with `UseSqlServer` in `src/Infrastructure/DependencyInjection.cs`, set the connection
string, and regenerate the migration (`dotnet ef migrations add InitialCreate -p src/Infrastructure -s src/Api -o Data/Migrations`).
The `DateTimeOffset` binary converter in `DispatchDbContext.ConfigureConventions` is SQLite-specific and can be removed.
