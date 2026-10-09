# Logistics Dispatch Engine — Full Implementation Plan

> **Progress tracking:** every task is a checkbox. As we complete work, tick it (`- [ ]` → `- [x]`). Step 0 copies this plan into the project as `IMPLEMENTATION.md`, which becomes the live checklist.

## Context
PLAN.md specifies a real-time dispatch system: delivery jobs, drivers, SignalR live updates, a background driver-movement simulation, and a dispatch API with validation and concurrency handling. The project folder (`C:\Users\Administrator\Desktop\dev\logistics-dispatch`) contains only PLAN.md, so this is a greenfield build. Installed SDK: .NET 10 (10.0.401). Not a git repo.

## Decisions (defaults)
- Target `net10.0`; SQLite via EF Core (SQL Server swappable via provider + connection string).
- Frontend: static HTML/JS in `Api/wwwroot` using the SignalR JS client from CDN (no second project).
- Controllers + DTO records with DataAnnotations (`[ApiController]` auto-400).
- Tests: xUnit; API tests via `WebApplicationFactory` with in-memory SQLite.
- Concurrency: optimistic, `long Version` incremented in `SaveChanges`, configured `IsConcurrencyToken()` (SQLite has no rowversion).
- `TimeProvider` injected so worker/state logic is testable.

## Target folder structure
```
logistics-dispatch/
├─ LogisticsDispatch.sln
├─ PLAN.md  IMPLEMENTATION.md  README.md  .gitignore  Directory.Build.props
├─ src/
│  ├─ Core/                           # LogisticsDispatch.Core (no package refs)
│  │  ├─ Entities/      Job.cs Driver.cs Location.cs StatusHistory.cs
│  │  ├─ Enums/         JobStatus.cs DriverStatus.cs
│  │  ├─ States/        IJobState.cs JobStateBase.cs PendingState.cs AssignedState.cs
│  │  │                 InTransitState.cs CompletedState.cs CancelledState.cs JobStateFactory.cs
│  │  ├─ Exceptions/    InvalidJobTransitionException.cs ConcurrencyConflictException.cs NotFoundException.cs
│  │  ├─ Abstractions/  IJobRepository.cs IDriverRepository.cs IUnitOfWork.cs IDispatchNotifier.cs
│  │  ├─ Services/      DispatchService.cs GeoMath.cs
│  │  └─ Models/        JobDto.cs DriverDto.cs JobProgressEvent.cs JobStatusChangedEvent.cs
│  ├─ Infrastructure/                 # refs Core
│  │  ├─ Data/          DispatchDbContext.cs DbSeeder.cs Configurations/ Migrations/
│  │  ├─ Repositories/  JobRepository.cs DriverRepository.cs UnitOfWork.cs
│  │  ├─ Realtime/      DispatchHub.cs HubEvents.cs SignalRDispatchNotifier.cs
│  │  ├─ Simulation/    SimulationWorker.cs SimulationOptions.cs
│  │  └─ DependencyInjection.cs
│  └─ Api/                            # Web SDK, refs Core + Infrastructure
│     ├─ Program.cs  appsettings*.json  Properties/launchSettings.json  Api.http
│     ├─ Controllers/   JobsController.cs DriversController.cs StatusController.cs
│     ├─ Contracts/     CreateJobRequest.cs AssignJobRequest.cs
│     ├─ Middleware/    ExceptionHandlingMiddleware.cs
│     └─ wwwroot/       index.html app.js styles.css
└─ tests/
   ├─ Core.Tests/       JobStateTests GeoMathTests DispatchServiceTests
   └─ Api.Tests/        ApiFactory JobsApiTests AssignConcurrencyTests SimulationWorkerTests HubTests
```

## Packages
- Infrastructure: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design` (PrivateAssets=all), `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (SignalR server).
- Api: `Microsoft.EntityFrameworkCore.Design`; built-in `AddOpenApi()`.
- Tests: `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Data.Sqlite`, `Microsoft.AspNetCore.SignalR.Client` (Api.Tests).

---

## Implementation checklist

### Step 0 — Scaffold
- [x] `git init`; add `.gitignore` (`dotnet new gitignore`)
- [x] Copy this plan to `IMPLEMENTATION.md` in the project root
- [x] `dotnet new sln -n LogisticsDispatch`
- [x] Create `src/Core`, `src/Infrastructure` (classlib), `src/Api` (`dotnet new web`), `tests/Core.Tests`, `tests/Api.Tests` (xunit)
- [x] Add all projects to the sln; wire project references (Infra→Core; Api→Core,Infra; tests→their targets)
- [x] Add `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors off)
- [x] Install NuGet packages (see Packages) and `dotnet tool install --global dotnet-ef` if missing
- [x] Delete template `Class1.cs` / `UnitTest1.cs`
- [x] `dotnet build` green

### Step 1 — Domain (Core)
- [x] Enums: `JobStatus` (Pending, Assigned, InTransit, Completed, Cancelled), `DriverStatus` (Idle, Busy, Offline)
- [x] `Location` (`readonly record struct Location(double Lat, double Lng)`)
- [x] `StatusHistory` (Id, JobId, From, To, At, Note)
- [x] `Driver` (Id, Name, Status, CurrentLocation, ActiveJobId, Version; `MarkBusy`, `MarkIdle`)
- [x] `Job` (Id, Reference, CustomerName, Notes, Pickup, Dropoff, Status, DriverId, CurrentLocation, EtaSeconds, Progress, CreatedAt, UpdatedAt, Version, History)
- [x] Exceptions: `InvalidJobTransitionException`, `ConcurrencyConflictException`, `NotFoundException`
- [x] State pattern: `IJobState`, `JobStateBase` (default throws), `PendingState` (Assign, Cancel), `AssignedState` (StartTransit, Cancel), `InTransitState` (Complete), `CompletedState`/`CancelledState` (terminal), `JobStateFactory`
- [x] `Job` methods `Assign/StartTransit/Complete/Cancel` delegate to state and append `StatusHistory`
- [x] `GeoMath`: haversine distance, `MoveToward`, `EtaSeconds`
- [x] Abstractions: `IJobRepository`, `IDriverRepository`, `IUnitOfWork`, `IDispatchNotifier`
- [x] DTO/event models: `JobDto`, `DriverDto`, `JobProgressEvent`, `JobStatusChangedEvent`
- [x] `DispatchService`: `CreateJobAsync`, `AssignAsync`, `StartTransitAsync`, `CompleteAsync`, `CancelAsync` (mutate → save → notify after save)
- [x] `dotnet build` green

### Step 2 — Data (Infrastructure)
- [x] `DispatchDbContext` with `Jobs`, `Drivers`, `StatusHistory`; `SaveChangesAsync` override bumps `Version` on modified Job/Driver
- [x] EF configurations: `Location` as complex property (Pickup/Dropoff/CurrentLocation), enums as strings, `Version` as concurrency token, unique `Reference`, indexes on `Status`/`DriverId`
- [x] `JobRepository`, `DriverRepository`
- [x] `UnitOfWork` mapping `DbUpdateConcurrencyException` → `ConcurrencyConflictException`
- [x] `DbSeeder` (5 idle drivers around 40.7128, -74.0060)
- [x] `DependencyInjection.AddInfrastructure` (DbContext, repos, UoW, `DispatchService`, notifier, `TimeProvider`, options, hosted worker)
- [x] Generate migration: `dotnet ef migrations add InitialCreate -p src/Infrastructure -s src/Api -o Data/Migrations`
- [x] `dotnet build` green

### Step 3 — SignalR
- [x] `HubEvents` constants (`JobCreated`, `JobStatusChanged`, `JobProgress`, `DriverUpdated`)
- [x] `DispatchHub`: `JoinDispatchGroup`, `LeaveDispatchGroup`, `SendStatusUpdate`; auto-join `dispatchers` on connect
- [x] `SignalRDispatchNotifier` via `IHubContext<DispatchHub>` (send to group; log + swallow send failures)
- [x] Register `AddSignalR()` and `MapHub<DispatchHub>("/hubs/dispatch")` in `Program.cs`

### Step 4 — API
- [x] `Program.cs`: controllers, ProblemDetails, OpenAPI, `AddInfrastructure`, static files, auto `MigrateAsync` + seed on startup, `public partial class Program`
- [x] `ExceptionHandlingMiddleware` (NotFound→404, InvalidTransition→409, Concurrency→409, other→500)
- [x] `CreateJobRequest` / `AssignJobRequest` with DataAnnotations (lat −90..90, lng −180..180, pickup ≠ dropoff)
- [x] `JobsController`: `POST /api/jobs`, `GET /api/jobs?status=`, `GET /api/jobs/{id}`, `POST …/assign`, `…/accept`, `…/complete`, `…/cancel`
- [x] `DriversController`: `GET /api/drivers`
- [x] `StatusController`: `GET /api/status` (counts by status, idle/busy drivers, avg ETA)
- [x] `launchSettings.json` → `http://localhost:5080`; `Api.http` sample requests
- [x] Manual smoke test: run the API, create + assign a job via `Api.http`/curl

### Step 5 — Simulation worker
- [x] `SimulationOptions` (TickInterval 3s, DriverSpeedMps 15, AutoAssign, TimeScale, PickupDwell)
- [x] `SimulationWorker : BackgroundService` with `PeriodicTimer`, per-tick scope via `IServiceScopeFactory`, per-tick try/catch, clean cancellation
- [x] Tick stage: auto-assign Pending → nearest Idle driver (swallow concurrency conflicts)
- [x] Tick stage: Assigned → InTransit after dwell
- [x] Tick stage: move InTransit jobs (coords, progress, ETA) + broadcast `JobProgress`
- [x] Tick stage: complete on arrival, free driver
- [x] Per-job try/catch so one failure doesn't stall the batch
- [x] Manual check: a created job walks Pending → Completed on its own

### Step 6 — Dashboard UI
- [x] `index.html` layout: connection badge, summary tiles, new-job form (+ "random nearby"), jobs table, drivers panel
- [x] `styles.css`: status badge classes, progress bar, toast, responsive
- [x] `app.js`: hub connection with automatic reconnect; re-join group + full resync on reconnect
- [x] Handlers: `JobCreated`, `JobStatusChanged`, `JobProgress` (in-place update), `DriverUpdated`
- [x] Assign dropdown/button, cancel button, error toasts from ProblemDetails
- [x] Manual check in two browser tabs: updates appear live without refresh

### Step 7 — Tests
- [x] `Core.Tests/JobStateTests`: full transition matrix + history entries
- [x] `Core.Tests/GeoMathTests`
- [x] `Core.Tests/DispatchServiceTests` (fake repos: busy driver rejected, driver freed on complete/cancel)
- [x] `Api.Tests/ApiFactory` (in-memory SQLite, recording notifier, AutoAssign off by default)
- [x] `JobsApiTests`: 201, 400 invalid coords, 404, detail has history
- [x] `AssignConcurrencyTests`: parallel assigns → exactly one 200 + one 409
- [x] `SimulationWorkerTests`: fast ticks, job reaches Completed, expected events recorded
- [x] `HubTests`: SignalR client receives `JobCreated`
- [x] `dotnet test` all green

### Step 8 — Polish & wrap-up
- [x] `README.md` (what it is, run, test, architecture diagram/notes)
- [x] Structured logging for state transitions
- [x] Final full verification run (below)
- [x] First git commit

---

## Key design details

**State transitions:** Pending→Assigned (assign) / Cancelled · Assigned→InTransit (accept/start) / Cancelled · InTransit→Completed · Completed & Cancelled terminal. Illegal call → `InvalidJobTransitionException` (409).

**Assign concurrency:** `AssignAsync` loads job + driver, checks job Pending and driver Idle, mutates both, saves. Version tokens on both rows mean a concurrent writer causes `DbUpdateConcurrencyException` → `ConcurrencyConflictException` → 409.

**Worker scope:** singleton `SimulationWorker` never holds a DbContext; each tick `CreateScope()` and resolves scoped services. Notify only after `SaveChangesAsync` succeeds.

**Simulation movement:** driver snaps to pickup on assignment (keeps v1 simple); InTransit moves straight toward dropoff by `speed × tick × TimeScale` meters; ETA = remaining distance / speed.

## Verification (end-to-end)
1. `dotnet build` — 0 errors; `dotnet test` — all green.
2. `dotnet run --project src/Api` → open `http://localhost:5080` in two tabs; badge shows Connected.
3. Create a job in tab A → appears in tab B instantly; it auto-progresses Pending → Assigned → InTransit (progress/ETA update) → Completed; driver returns to Idle.
4. Race check with `AutoAssign=false`: two simultaneous `curl -X POST http://localhost:5080/api/jobs/{id}/assign -H "Content-Type: application/json" -d "{\"driverId\":\"…\"}"` → one 200, one 409.
5. `GET /api/jobs/{id}` shows full `StatusHistory`; `GET /api/status` counts match dashboard tiles.
6. Ctrl+C: worker stops cleanly; restart keeps data (SQLite) and resumes in-flight jobs.
