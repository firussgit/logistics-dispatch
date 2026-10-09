## Project: Real-Time Logistics & Order Dispatch Engine

### 1. Overview & Business Value
A dynamic dispatch system (similar to fleet management or delivery dispatch) that handles real-time job state transitions, background movement simulations, and live updates via WebSockets.

* **Primary Technical Focus:** Asynchronous programming, real-time communication, state management, background services.
* **Key Demonstration:** Proves ability to handle live data streams without relying on traditional client polling.

---

### 2. Architecture & Tech Stack
* **Framework:** ASP.NET Core Web API / Blazor WebAssembly
* **Real-Time Communication:** ASP.NET Core SignalR
* **Background Tasks:** `BackgroundService` (`IHostedService`)
* **Database & ORM:** SQLite / SQL Server via Entity Framework Core
* **Design Patterns:** State Pattern (Job lifecycle), Repository Pattern, Hub Pattern (SignalR)

---

### 3. Key Feature Specifications

#### A. Real-Time Dispatch Dashboard (SignalR Hub)
* Establish persistent WebSocket connection between backend and client.
* Broadcast updates when:
  * A new delivery job is created.
  * A driver accepts or completes a job.
  * GPS coordinates/progress update occurs.

#### B. Background Driver Simulation Engine
* Implement a C# `BackgroundService` that runs periodically.
* Simulates driver movement along a route:
  * Every few seconds, updates job ETA and coordinates.
  * Triggers state changes (`Pending` → `Assigned` → `InTransit` → `Completed`).

#### C. Order & Dispatch API
* Endpoints to create jobs, assign drivers, and view live system status.
* Input validation and concurrency handling for assignment actions.

---

### 4. Implementation Steps

1. **Setup & Domain Modeling:**
   * Create solution structure: `Api`, `Core` (Entities), `Infrastructure` (Data/SignalR).
   * Define entities: `Job`, `Driver`, `Location`, `StatusHistory`.

2. **SignalR Integration:**
   * Configure `DispatchHub` with methods like `JoinDispatchGroup`, `SendStatusUpdate`.
   * Register SignalR middleware in `Program.cs`.

3. **Background Worker Implementation:**
   * Create `SimulationWorker : BackgroundService`.
   * Inject `IServiceScopeFactory` to safely consume scoped EF DbContext inside a singleton background worker.

4. **Frontend / UI Integration:**
   * Build a Blazor WebAssembly or lightweight JavaScript interface with live status badges and real-time updating lists.