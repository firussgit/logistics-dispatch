// Shared Leaflet map used by the dispatcher console and the customer tracking page.
window.DispatchMap = function (elementId, { center = [40.7128, -74.006], zoom = 12 } = {}) {
  const map = L.map(elementId, { zoomControl: true }).setView(center, zoom);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
  }).addTo(map);

  const drivers = new Map(); // driverId -> marker
  const jobs = new Map();    // jobId -> layerGroup
  const ACTIVE = new Set(['Pending', 'Assigned', 'InTransit']);

  const pin = (cls, glyph, label) =>
    L.divIcon({ className: '', html: `<div class="pin ${cls}" title="${label}">${glyph}</div>`, iconSize: [30, 30], iconAnchor: [15, 15] });
  const truck = (status) =>
    L.divIcon({ className: 'driver-icon', html: `<div class="pin truck ${status.toLowerCase()}">🚚</div>`, iconSize: [34, 34], iconAnchor: [17, 17] });

  const ll = (loc) => [loc.lat, loc.lng];

  return {
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

    /** Draws pickup/dropoff for active jobs; removes finished ones. */
    setJob(j) {
      jobs.get(j.id)?.remove();
      jobs.delete(j.id);
      if (!ACTIVE.has(j.status)) return;
      const g = L.layerGroup([
        L.marker(ll(j.pickup), { icon: pin('pickup', 'P', 'Pickup') }).bindTooltip(`${j.reference} pickup`),
        L.marker(ll(j.dropoff), { icon: pin('dropoff', 'D', 'Dropoff') }).bindTooltip(`${j.reference} dropoff · ${j.customerName}`),
        L.polyline([ll(j.pickup), ll(j.dropoff)], { color: '#2f5bea', weight: 3, opacity: 0.55, dashArray: '6 8' }),
      ]).addTo(map);
      jobs.set(j.id, g);
    },

    removeJob(id) {
      jobs.get(id)?.remove();
      jobs.delete(id);
    },

    focusJob(j, driverId) {
      const pts = [ll(j.pickup), ll(j.dropoff)];
      const dm = driverId && drivers.get(driverId);
      if (dm) pts.push(dm.getLatLng());
      map.fitBounds(L.latLngBounds(pts), { padding: [60, 60], maxZoom: 15 });
    },

    fitAll() {
      const pts = [...drivers.values()].map((m) => m.getLatLng());
      if (pts.length) map.fitBounds(L.latLngBounds(pts), { padding: [50, 50], maxZoom: 14 });
    },
  };
};
