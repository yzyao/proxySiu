const baseUrl = import.meta.env.VITE_API_BASE || '/api'

async function request(path, options = {}) {
  const response = await fetch(`${baseUrl}${path}`, {
    ...options,
    headers: {
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...options.headers
    }
  })

  if (!response.ok) {
    const body = await response.json().catch(() => ({}))
    throw new Error(body.message || `请求失败 (${response.status})`)
  }

  if (response.status === 204) return null
  const contentType = response.headers.get('content-type') || ''
  return contentType.includes('application/json') ? response.json() : response.text()
}

function queryString(params) {
  const search = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => {
    if (value !== '' && value !== null && value !== undefined) search.set(key, value)
  })
  return search.toString()
}

export const api = {
  dashboard: () => request('/dashboard'),
  proxies: (params) => request(`/proxies?${queryString(params)}`),
  addProxy: (data) => request('/proxies', { method: 'POST', body: JSON.stringify(data) }),
  deleteProxy: (id) => request(`/proxies/${id}`, { method: 'DELETE' }),
  checkProxy: (id) => request(`/proxies/${id}/check`, { method: 'POST' }),
  randomProxy: (protocol = '') => request(`/proxy/random?${queryString({ protocol })}`),
  sources: () => request('/sources'),
  addSource: (data) => request('/sources', { method: 'POST', body: JSON.stringify(data) }),
  updateSource: (id, data) => request(`/sources/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  deleteSource: (id) => request(`/sources/${id}`, { method: 'DELETE' }),
  action: (name, params = {}) => request(`/actions/${name}?${queryString(params)}`, { method: 'POST' })
}
