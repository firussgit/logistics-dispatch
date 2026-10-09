# Street routing (OSRM)

The simulation moves drivers along real roads using a self-hosted [OSRM](https://project-osrm.org/) server.
Without it the app still works: it falls back to straight lines and shows "straight-line" in the dashboard badge.

```powershell
./routing/setup.ps1      # one-time: needs Docker Desktop running
docker compose -f routing/docker-compose.yml up -d    # afterwards (also restarts automatically with Docker)
docker compose -f routing/docker-compose.yml down     # stop
```

* **Area covered:** lat 40.68–40.80, lng −74.04…−73.93 (Battery → Harlem, plus nearby Brooklyn/Jersey City). Pickups and
  dropoffs outside it snap to the nearest covered road. To change the area, edit the bbox in `overpass-query.txt` and run
  `./routing/setup.ps1 -Rebuild`.
* **Data:** © OpenStreetMap contributors (ODbL), fetched from the public Overpass API. `data/` is git-ignored.
* **Configuration:** `Routing` section of `src/Api/appsettings.json` (`Provider`: `Osrm` | `StraightLine`, `BaseUrl`).
* **Why not the public demo server?** `router.project-osrm.org` is rate-limited and not meant for applications; self-hosting
  also keeps the simulation working offline.
