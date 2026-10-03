// Shared helpers for every dashboard page. Plain fetch + localStorage -- no framework,
// to match this project's "don't build a frontend framework showcase" scope decision.

function getToken() {
  return localStorage.getItem('token');
}

function requireAuth() {
  if (!getToken()) {
    window.location.href = '/login.html';
  }
}

async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  const token = getToken();
  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  const response = await fetch(path, { ...options, headers });

  if (response.status === 401) {
    localStorage.removeItem('token');
    window.location.href = '/login.html';
    throw new Error('Not authenticated');
  }

  return response;
}

function logout() {
  localStorage.removeItem('token');
  window.location.href = '/login.html';
}
