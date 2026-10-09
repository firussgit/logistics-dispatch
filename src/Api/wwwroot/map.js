// Shared Leaflet map used by the dispatcher console, the customer tracking page and the driver app.
window.DispatchMap = function (elementId, { center = [40.7128, -74.006], zoom = 12 } = {}) {
  const map = L.map(elementId, { zoomControl: true }).setView(center, zoom);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
  }).addTo(map);

  const drivers = new Map();   // driverId -> marker
  const jobs = new Map();      // jobId -> { job, group }
  const routes = new Map();    // jobId -> { approach: [[lat,lng]..]|null, trip: [...]|null, isStraightLine }
  const loading = new Set();   // jobIds with a route request in flight
  const ACTIVE = new Set(['Pending', 'Assigned', 'InTransit']);

  const pin = (cls, glyph, label) =>
    L.divIcon({ className: '', html: `<div class="pin ${cls}" title="${label}">${glyph}</div>`, iconSize: [30, 30], iconAnchor: [15, 15] });
  const truck = (status) =>
    L.divIcon({ className: 'driver-icon', html: `<div class="pin truck ${status.toLowerCase()}">🚚</div>`, iconSize: [34, 34], iconAnchor: [17, 17] });

  const ll = (loc) => [loc.lat, loc.lng];

  /** Marker + line layers for one job. Uses the road path once we have it, a dashed straight line until then. */
  function buildLayers(j) {
    const layers = [
      L.marker(ll(j.pickup), { icon: pin('pickup', 'P', 'Pickup') }).bindTooltip(`${j.reference} pickup`),
      L.marker(ll(j.dropoff), { icon: pin('dropoff', 'D', 'Dropoff') }).bindTooltip(`${j.reference} dropoff · ${j.customerName}`),
    ];
    const r = routes.get(j.id);
    const hasRoad = r && r.trip && !r.isStraightLine;

    if (hasRoad) {
      // white casing under the coloured line keeps it readable on busy maps
      layers.push(L.polyline(r.trip, { color: '#fff', weight: 8, opacity: 0.85, lineCap: 'round', lineJoin: 'round' }));
      layers.push(L.polyline(r.trip, { color: '#2f5bea', weight: 5, opacity: 0.9, lineCap: 'round', lineJoin: 'round' }));
    } else {
      layers.push(L.polyline([ll(j.pickup), ll(j.dropoff)], { color: '#2f5bea', weight: 3, opacity: 0.55, dashArray: '6 8' }));
    }
    // the driver's leg to the pickup only matters until they get there
    if (j.status === 'Assigned' && r?.approach && !r.isStraightLine) {
      layers.push(L.polyline(r.approach, { color: '#d98a00', weight: 5, opacity: 0.9, dashArray: '2 9', lineCap: 'round' }));
    }
    return layers;
  }

  function draw(j) {
    jobs.get(j.id)?.group.remove();
    jobs.delete(j.id);
    if (!ACTIVE.has(j.status)) { routes.delete(j.id); return; }
    jobs.set(j.id, { job: j, group: L.layerGroup(buildLayers(j)).addTo(map) });
  }

  const api = {
    leaflet: map,

    setDriver(d) {
      let m = drivers.get(d.id);
      if (!m) {
        m = L.marker(ll(d.currentLocation), { icon: truck(d.status), zIndexOffset: 1000 }).addTo(map);
        drivers.set(d.id, m);
      } else {
        m.setLatLng(ll(d.currentLocation));
        m.setIcon(truck(d.status));
      }
      m.bindTooltip(`${d.name} · ${d.status}`, { direction: 'top', offset: [0, -14] });
    },

    moveDriver(id, lat, lng) {
      drivers.get(id)?.setLatLng([lat, lng]);
    },

    /** Draws pickup/dropoff + path for active jobs; removes finished ones. Fetches the road path the first time. */
    setJob(j) {
      draw(j);
      if (ACTIVE.has(j.status) && !routes.has(j.id)) api.loadRoute(j.id);
    },

    removeJob(id) {
      jobs.get(id)?.group.remove();
      jobs.delete(id);
      routes.delete(id);
    },

    /** (Re)loads the road path of a job from the API and redraws it. Call with force=true on a RouteReady event. */
    async loadRoute(jobId, force = false) {
      if (loading.has(jobId) || (!force && routes.has(jobId))) return;
      loading.add(jobId);
      try {
        const res = await fetch(`/api/jobs/${jobId}/route`);
        if (!res.ok) return;
        const r = await res.json();
        if (!r.trip && !r.approach) return; // not computed yet; a RouteReady event will bring us back
        routes.set(jobId, { approach: r.approach, trip: r.trip, isStraightLine: r.isStraightLine });
        const entry = jobs.get(jobId);
        if (entry) draw(entry.job);
      } finally {
        loading.delete(jobId);
      }
    },

    focusJob(j, driverId) {
      const pts = [ll(j.pickup), ll(j.dropoff)];
      const r = routes.get(j.id);
      if (r?.trip) pts.push(...r.trip);
      const dm = driverId && drivers.get(driverId);
      if (dm) pts.push(dm.getLatLng());
      map.fitBounds(L.latLngBounds(pts), { padding: [60, 60], maxZoom: 16 });
    },

    fitAll() {
      const pts = [...drivers.values()].map((m) => m.getLatLng());
      if (pts.length) map.fitBounds(L.latLngBounds(pts), { padding: [50, 50], maxZoom: 14 });
    },
  };
  return api;
};
