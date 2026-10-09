(() => {
  const $ = (id) => document.getElementById(id);
  const next = Auth.safeNext(new URLSearchParams(location.search).get('next'));

  function show(which) {
    const login = which === 'login';
    $('login-form').classList.toggle('hidden', !login);
    $('register-form').classList.toggle('hidden', login);
    $('tab-login').classList.toggle('active', login);
    $('tab-register').classList.toggle('active', !login);
    $('login-error').classList.add('hidden');
    $('register-error').classList.add('hidden');
  }
  $('tab-login').onclick = () => show('login');
  $('tab-register').onclick = () => show('register');

  function fail(id, message) {
    const box = $(id);
    box.textContent = message;
    box.classList.remove('hidden');
  }

  async function submit(path, body, errorId, button) {
    button.disabled = true;
    try {
      const res = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (res.ok) {
        const user = await res.json();
        location.replace(next ?? Auth.HOME[user.role] ?? '/');
        return;
      }
      let message = 'Something went wrong. Please try again.';
      if (res.status === 429) message = 'Too many attempts. Wait a minute and try again.';
      else {
        try {
          const p = await res.json();
          message = (p.errors && Object.values(p.errors).flat().join(' ')) || p.detail || message;
        } catch { /* non-JSON error */ }
      }
      fail(errorId, message);
    } catch {
      fail(errorId, 'Could not reach the server.');
    } finally {
      button.disabled = false;
    }
  }

  $('login-form').onsubmit = (ev) => {
    ev.preventDefault();
    const f = ev.target;
    submit('/api/auth/login', { email: f.email.value, password: f.password.value }, 'login-error', f.querySelector('button[type=submit]'));
  };

  $('register-form').onsubmit = (ev) => {
    ev.preventDefault();
    const f = ev.target;
    submit('/api/auth/register', { email: f.email.value, displayName: f.displayName.value, password: f.password.value }, 'register-error', f.querySelector('button[type=submit]'));
  };

  // Already signed in? Skip the form.
  Auth.me().then((u) => { if (u) location.replace(next ?? Auth.HOME[u.role] ?? '/'); });

  // Development convenience: one-click sign-in as a seeded demo account (the endpoint answers {enabled:false} in production).
  fetch('/api/auth/demo').then((r) => r.json()).then((demo) => {
    if (!demo.enabled) return;
    $('demo').classList.remove('hidden');
    $('demo-buttons').replaceChildren(...demo.accounts.map((a) => {
      const b = Object.assign(document.createElement('button'), { type: 'button', textContent: `${a.role}` });
      b.title = a.email;
      b.onclick = () => submit('/api/auth/login', { email: a.email, password: demo.password }, 'login-error', b);
      return b;
    }));
  }).catch(() => {});
})();
