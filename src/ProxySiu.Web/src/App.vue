<script setup>
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import {
  Aim,
  CircleCheck,
  Collection,
  Connection,
  CopyDocument,
  DataAnalysis,
  Delete,
  Download,
  Edit,
  Link,
  Plus,
  Refresh,
  Search,
  Timer,
  VideoPlay,
  WarningFilled
} from '@element-plus/icons-vue'
import { api } from './api'

const activeView = ref('dashboard')
const loading = ref(false)
const actionBusy = ref('')
const profileSwitching = ref(false)
const selectedProfile = ref('')
const sessionLoading = ref(true)
const authenticated = ref(false)
const loginToken = ref('')
const loginBusy = ref(false)
const dashboard = ref({
  total: 0,
  alive: 0,
  dead: 0,
  pending: 0,
  availabilityRate: 0,
  averageLatencyMs: null,
  sources: 0,
  enabledSources: 0,
  protocols: [],
  operations: { checkQueue: {} }
})
const proxyRows = ref([])
const proxyTotal = ref(0)
const sources = ref([])
const checkingIds = reactive(new Set())
const query = reactive({ q: '', status: '', protocol: '', sort: '', desc: false, page: 1, pageSize: 30 })

const proxyDialogVisible = ref(false)
const proxyForm = reactive({ host: '', port: 8080, protocol: 'http', isPinned: true })
const sourceDialogVisible = ref(false)
const editingSourceId = ref(null)
const sourceForm = reactive({ name: '', url: '', protocol: 'http', enabled: true })

const titleMap = {
  dashboard: ['运行总览', '掌握代理池健康度与后台任务状态'],
  proxies: ['代理池', '筛选、检测和维护所有候选代理'],
  sources: ['采集源', '管理公开代理列表及其扫描状态']
}
const currentTitle = computed(() => titleMap[activeView.value])
const availabilityTone = computed(() => {
  if (dashboard.value.availabilityRate >= 20) return 'good'
  if (dashboard.value.availabilityRate >= 5) return 'warn'
  return 'bad'
})
const checkQueue = computed(() => dashboard.value.operations?.checkQueue || {})
const maintenanceOperation = computed(() => dashboard.value.operations?.activeOperation || null)
const maintenanceBusy = computed(() => maintenanceOperation.value !== null)
const checkProgress = computed(() => Math.round(checkQueue.value.progressPercent || 0))
const displayedCheckProgress = computed(() => checkQueue.value.isRunning || !checkQueue.value.waiting ? checkProgress.value : 0)
const checkProgressLabel = computed(() => {
  if (checkQueue.value.isRunning) return `${checkProgress.value}%`
  if (checkQueue.value.waiting) return '待开始'
  return checkQueue.value.total ? `${checkProgress.value}%` : '—'
})
const checkQueueTitle = computed(() => {
  if (checkQueue.value.isRunning) {
    return checkQueue.value.total
      ? `正在检测 ${checkQueue.value.completed}/${checkQueue.value.total}`
      : '正在准备检测队列'
  }
  if (checkQueue.value.waiting) return `${checkQueue.value.waiting} 个代理等待检测`
  if (checkQueue.value.completed) return `上一批已完成 ${checkQueue.value.completed} 个`
  return '当前没有待检测代理'
})
const checkQueueHint = computed(() => {
  if (checkQueue.value.isRunning) {
    return `并发 ${checkQueue.value.concurrency || 0}，正在处理 ${checkQueue.value.inFlight || 0} 个`
  }
  if (checkQueue.value.waiting) return '到期代理会优先进入下一批检测'
  return `上次检测 ${formatDate(dashboard.value.operations?.lastCheckAt)}`
})

let pollTimer

function scheduleDashboardPoll() {
  window.clearTimeout(pollTimer)
  const isBusy = maintenanceBusy.value || dashboard.value.operations?.isScanning || dashboard.value.operations?.isChecking
  const delay = isBusy ? 2000 : 5000
  pollTimer = window.setTimeout(async () => {
    await loadDashboard()
    scheduleDashboardPoll()
  }, delay)
}

onMounted(async () => {
  try {
    await api.session()
    authenticated.value = true
    await initializeAuthenticatedApp()
  } catch {
    authenticated.value = false
  } finally {
    sessionLoading.value = false
  }
})

onBeforeUnmount(() => window.clearTimeout(pollTimer))

async function initializeAuthenticatedApp() {
  await Promise.allSettled([loadDashboard(), loadProxies(), loadSources()])
  scheduleDashboardPoll()
}

async function login() {
  if (!loginToken.value.trim()) return
  loginBusy.value = true
  try {
    await api.login(loginToken.value)
    loginToken.value = ''
    authenticated.value = true
    await initializeAuthenticatedApp()
  } catch {
    ElMessage.error('访问令牌无效')
  } finally {
    loginBusy.value = false
  }
}

async function logout() {
  try {
    await api.logout()
  } finally {
    window.clearTimeout(pollTimer)
    authenticated.value = false
    loginToken.value = ''
  }
}

async function loadDashboard() {
  try {
    const wasMaintaining = maintenanceBusy.value
    const nextDashboard = await api.dashboard()
    dashboard.value = nextDashboard
    if (nextDashboard.profile?.name) selectedProfile.value = nextDashboard.profile.name
    if (wasMaintaining && !nextDashboard.operations?.activeOperation) {
      await Promise.all([loadProxies(), loadSources()])
    }
  } catch (error) {
    ElMessage.error(error.message)
  }
}

async function loadProxies() {
  loading.value = true
  try {
    const result = await api.proxies(query)
    proxyRows.value = result.items
    proxyTotal.value = result.total
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loading.value = false
  }
}

async function loadSources() {
  try {
    sources.value = await api.sources()
  } catch (error) {
    ElMessage.error(error.message)
  }
}

async function reloadAll() {
  await Promise.all([loadDashboard(), loadProxies(), loadSources()])
}

function changeView(view) {
  activeView.value = view
  if (view === 'proxies') loadProxies()
  if (view === 'sources') loadSources()
}

function searchProxies() {
  query.page = 1
  loadProxies()
}

function sortProxies({ prop, order }) {
  const sortMap = {
    status: 'status',
    latencyMs: 'latency',
    successRate: 'successRate'
  }
  query.sort = order && sortMap[prop] ? sortMap[prop] : ''
  query.desc = order === 'descending'
  query.page = 1
  loadProxies()
}

async function runActionLegacy(name, label, params = {}) {
  actionBusy.value = name
  try {
    const result = await api.action(name, params)
    const message = result.message || [result.scan?.message, result.check?.message].filter(Boolean).join(' ')
    ElMessage.success(message || `${label}完成`)
    await reloadAll()
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    actionBusy.value = ''
  }
}

async function runAction(name, label, params = {}) {
  if (maintenanceBusy.value) {
    ElMessage.warning('已有维护任务在执行，请等待完成')
    return
  }

  actionBusy.value = name
  try {
    await api.action(name, params)
    ElMessage.success(`${label} 已加入后台队列`)
    await loadDashboard()
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    actionBusy.value = ''
  }
}

async function switchProfile(profile) {
  if (!profile) return
  profileSwitching.value = true
  try {
    const summary = await api.updateProfile(profile)
    selectedProfile.value = summary.name
    await loadDashboard()
    ElMessage.success(`已切换为 ${summary.name}`)
  } catch (error) {
    ElMessage.error(error.message)
    await loadDashboard()
  } finally {
    profileSwitching.value = false
  }
}

async function checkProxy(row) {
  checkingIds.add(row.id)
  try {
    const result = await api.checkProxy(row.id)
    ElMessage[result.status === 'alive' ? 'success' : 'warning'](
      result.status === 'alive' ? `可用，延迟 ${result.latencyMs} ms` : result.lastError || '代理不可用'
    )
    await Promise.all([loadDashboard(), loadProxies()])
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    checkingIds.delete(row.id)
  }
}

async function removeProxy(row) {
  try {
    await ElMessageBox.confirm(`确定删除 ${row.host}:${row.port}？`, '删除代理', { type: 'warning' })
    await api.deleteProxy(row.id)
    ElMessage.success('已删除')
    await Promise.all([loadDashboard(), loadProxies()])
  } catch (error) {
    if (error !== 'cancel') ElMessage.error(error.message || '删除失败')
  }
}

function openProxyDialog() {
  Object.assign(proxyForm, { host: '', port: 8080, protocol: 'http', isPinned: true })
  proxyDialogVisible.value = true
}

async function saveProxy() {
  try {
    await api.addProxy(proxyForm)
    proxyDialogVisible.value = false
    ElMessage.success('代理已加入待检测队列')
    await Promise.all([loadDashboard(), loadProxies()])
  } catch (error) {
    ElMessage.error(error.message)
  }
}

function openSourceDialog(row = null) {
  editingSourceId.value = row?.id || null
  Object.assign(sourceForm, row
    ? { name: row.name, url: row.url, protocol: row.protocol, enabled: row.enabled }
    : { name: '', url: '', protocol: 'http', enabled: true })
  sourceDialogVisible.value = true
}

async function saveSource() {
  try {
    if (editingSourceId.value) await api.updateSource(editingSourceId.value, sourceForm)
    else await api.addSource(sourceForm)
    sourceDialogVisible.value = false
    ElMessage.success(editingSourceId.value ? '采集源已更新' : '采集源已添加')
    await Promise.all([loadDashboard(), loadSources()])
  } catch (error) {
    ElMessage.error(error.message)
  }
}

async function toggleSource(row) {
  try {
    await api.updateSource(row.id, {
      name: row.name,
      url: row.url,
      protocol: row.protocol,
      enabled: row.enabled
    })
    await loadDashboard()
  } catch (error) {
    row.enabled = !row.enabled
    ElMessage.error(error.message)
  }
}

async function removeSource(row) {
  try {
    await ElMessageBox.confirm(`确定删除采集源“${row.name}”？已有代理记录不会被删除。`, '删除采集源', { type: 'warning' })
    await api.deleteSource(row.id)
    ElMessage.success('采集源已删除')
    await Promise.all([loadDashboard(), loadSources()])
  } catch (error) {
    if (error !== 'cancel') ElMessage.error(error.message || '删除失败')
  }
}

async function copyRandom(protocol = '') {
  try {
    const result = await api.randomProxy(protocol)
    await navigator.clipboard.writeText(result.url)
    ElMessage.success(`已复制 ${result.url}`)
  } catch (error) {
    ElMessage.error(error.message)
  }
}

function protocolLabel(protocol) {
  return { http: 'HTTP', socks4: 'SOCKS4', socks5: 'SOCKS5' }[protocol] || protocol
}

function geoLabel(location) {
  if (!location) return ''
  const parts = [location.countryName || location.countryCode, location.regionName, location.cityName]
    .filter(Boolean)
  return parts.length ? parts.join(' · ') : '归属地未知'
}

function statusMeta(status) {
  return {
    alive: ['可用', 'success'],
    dead: ['失效', 'danger'],
    pending: ['待检测', 'warning']
  }[status] || [status, 'info']
}

function formatDate(value) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('zh-CN', {
    month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit'
  }).format(new Date(value))
}

function formatRelativeTime(value, dueText = '即将执行') {
  if (!value) return dueText
  const milliseconds = new Date(value).getTime() - Date.now()
  if (milliseconds <= 0) return dueText
  const seconds = Math.ceil(milliseconds / 1000)
  if (seconds < 60) return `${seconds} 秒后`
  const minutes = Math.ceil(seconds / 60)
  if (minutes < 60) return `${minutes} 分钟后`
  const hours = Math.ceil(minutes / 60)
  if (hours < 24) return `${hours} 小时后`
  return `${Math.ceil(hours / 24)} 天后`
}

function isCheckDue(value) {
  return !value || new Date(value).getTime() <= Date.now()
}

function successRate(row) {
  const total = row.successCount + row.failureCount
  return total ? `${Math.round(row.successCount * 100 / total)}%` : '—'
}
</script>

<template>
  <el-config-provider :locale="zhCn">
  <div v-if="sessionLoading" class="login-shell"><div class="login-card"><strong>ProxySiu</strong><span>正在验证会话…</span></div></div>
  <div v-else-if="!authenticated" class="login-shell">
    <form class="login-card" @submit.prevent="login">
      <div class="login-mark"><Aim /></div>
      <strong>ProxySiu</strong>
      <span>请输入访问令牌以继续</span>
      <el-input v-model="loginToken" type="password" show-password autocomplete="current-password" placeholder="Access token" />
      <el-button type="primary" native-type="submit" :loading="loginBusy" :disabled="!loginToken.trim()">进入管理台</el-button>
    </form>
  </div>
  <div v-else class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark"><Aim /></div>
        <div><strong>ProxySiu</strong><span>POOL CONTROL</span></div>
      </div>

      <nav class="nav-list">
        <button :class="{ active: activeView === 'dashboard' }" @click="changeView('dashboard')">
          <DataAnalysis /><span>运行总览</span>
        </button>
        <button :class="{ active: activeView === 'proxies' }" @click="changeView('proxies')">
          <Connection /><span>代理池</span><em>{{ dashboard.alive }}</em>
        </button>
        <button :class="{ active: activeView === 'sources' }" @click="changeView('sources')">
          <Collection /><span>采集源</span><em>{{ dashboard.enabledSources }}</em>
        </button>
      </nav>

      <div class="sidebar-status">
        <div class="pulse" :class="{ busy: dashboard.operations?.isScanning || dashboard.operations?.isChecking }"></div>
        <div>
          <strong>{{ dashboard.operations?.isScanning ? '正在采集' : dashboard.operations?.isChecking ? '正在检测' : '后台守护中' }}</strong>
          <span v-if="checkQueue.isRunning">已完成 {{ checkQueue.completed }}/{{ checkQueue.total }} · 进行中 {{ checkQueue.inFlight }} · 等待 {{ checkQueue.waiting }}</span>
          <span v-else>{{ dashboard.operations?.lastMessage || '等待下一次维护周期' }}</span>
        </div>
      </div>
    </aside>

    <main class="main-area">
      <header class="topbar">
        <div>
          <h1>{{ currentTitle[0] }}</h1>
          <p>{{ currentTitle[1] }}</p>
        </div>
        <div class="top-actions">
          <el-select v-model="selectedProfile" class="profile-select" size="small" :loading="profileSwitching" :disabled="maintenanceBusy" @change="switchProfile">
            <el-option label="高吞吐" value="high-throughput" />
            <el-option label="IDC 安全" value="idc-safe" />
          </el-select>
          <el-button text @click="logout">退出</el-button>
          <span class="updated"><Timer /> 数据更新于 {{ formatDate(dashboard.updatedAt) }}</span>
          <el-button :icon="Refresh" :loading="actionBusy === 'refresh'" @click="runAction('refresh', '刷新')">
            一键刷新
          </el-button>
          <el-button type="primary" :icon="VideoPlay" :loading="actionBusy === 'check' || checkQueue.isRunning" @click="runAction('check', '检测', { force: false })">
            {{ checkQueue.isRunning ? `检测中 ${checkProgress}%` : checkQueue.waiting ? `检测队列 (${checkQueue.waiting})` : '检测队列' }}
          </el-button>
        </div>
      </header>

      <section v-if="activeView === 'dashboard'" class="content dashboard-view">
        <div class="metrics-grid">
          <article class="metric-card hero-metric">
            <div class="metric-icon blue"><Connection /></div>
            <div class="metric-copy"><span>候选代理总数</span><strong>{{ dashboard.total.toLocaleString() }}</strong><small>{{ dashboard.pending }} 个等待首次检测</small></div>
            <div class="metric-spark"><i v-for="n in 9" :key="n" :style="{ height: `${18 + ((n * 13) % 42)}px` }"></i></div>
          </article>
          <article class="metric-card">
            <div class="metric-icon green"><CircleCheck /></div>
            <div class="metric-copy"><span>当前可用</span><strong>{{ dashboard.alive.toLocaleString() }}</strong><small>平均延迟 {{ dashboard.averageLatencyMs ?? '—' }} ms</small></div>
          </article>
          <article class="metric-card">
            <div class="metric-icon amber"><WarningFilled /></div>
            <div class="metric-copy"><span>失效记录</span><strong>{{ dashboard.dead.toLocaleString() }}</strong><small>连续失败后自动清理</small></div>
          </article>
          <article class="metric-card">
            <div class="metric-icon violet"><Link /></div>
            <div class="metric-copy"><span>启用采集源</span><strong>{{ dashboard.enabledSources }}<b>/{{ dashboard.sources }}</b></strong><small>按计划自动拉取公开列表</small></div>
          </article>
        </div>

        <div class="dashboard-grid">
          <article class="panel health-panel">
            <div class="panel-heading"><div><h2>代理池健康度</h2><p>只有最近检测成功的代理会进入可用池</p></div><span class="health-badge" :class="availabilityTone">{{ dashboard.availabilityRate }}%</span></div>
            <div class="health-body">
              <div class="donut" :style="{ '--value': `${Math.min(dashboard.availabilityRate, 100) * 3.6}deg` }">
                <div><strong>{{ dashboard.alive }}</strong><span>ALIVE</span></div>
              </div>
              <div class="health-legend">
                <div><i class="alive"></i><span>可用代理</span><strong>{{ dashboard.alive }}</strong></div>
                <div><i class="pending"></i><span>等待检测</span><strong>{{ dashboard.pending }}</strong></div>
                <div><i class="dead"></i><span>检测失败</span><strong>{{ dashboard.dead }}</strong></div>
              </div>
            </div>
          </article>

          <article class="panel protocol-panel">
            <div class="panel-heading"><div><h2>协议分布</h2><p>各协议候选量与可用量</p></div></div>
            <div class="protocol-list">
              <div v-for="item in dashboard.protocols" :key="item.protocol" class="protocol-row">
                <div class="protocol-name"><span>{{ protocolLabel(item.protocol) }}</span><small>{{ item.alive }} 可用 / {{ item.total }} 总计</small></div>
                <div class="protocol-track"><i :style="{ width: `${item.total ? item.alive * 100 / item.total : 0}%` }"></i></div>
                <strong>{{ item.total ? Math.round(item.alive * 100 / item.total) : 0 }}%</strong>
              </div>
            </div>
          </article>

          <article class="panel api-panel">
            <div class="panel-heading"><div><h2>快速取用</h2><p>从低延迟可用代理中随机选择</p></div><Download /></div>
            <div class="api-actions">
              <button @click="copyRandom('http')"><span>HTTP</span><code>GET /api/proxy/random?protocol=http</code><CopyDocument /></button>
              <button @click="copyRandom('socks5')"><span>SOCKS5</span><code>GET /api/proxy/random?protocol=socks5</code><CopyDocument /></button>
              <button @click="copyRandom()"><span>ANY</span><code>GET /api/proxy/random</code><CopyDocument /></button>
            </div>
            <p class="safety-note"><WarningFilled /> 公共代理可能记录或篡改流量，请勿传输账号、Cookie、密钥等敏感信息。</p>
          </article>

          <article class="panel task-panel queue-panel">
            <div class="panel-heading">
              <div><h2>检测队列</h2><p>实时显示当前批次的处理进度</p></div>
              <el-tag :type="checkQueue.isRunning ? 'primary' : checkQueue.waiting ? 'warning' : 'success'" effect="light" round>
                {{ checkQueue.isRunning ? '运行中' : checkQueue.waiting ? '待处理' : '空闲' }}
              </el-tag>
            </div>
            <div class="queue-body">
              <div class="queue-lead">
                <span class="queue-icon" :class="{ running: checkQueue.isRunning }"><VideoPlay /></span>
                <div><strong>{{ checkQueueTitle }}</strong><small>{{ checkQueueHint }}</small></div>
                <b>{{ checkProgressLabel }}</b>
              </div>
              <el-progress :percentage="displayedCheckProgress" :show-text="false" :stroke-width="9" :status="!checkQueue.isRunning && !checkQueue.waiting && checkProgress === 100 ? 'success' : undefined" />
              <div class="queue-stats">
                <div><span>等待</span><strong>{{ checkQueue.waiting || 0 }}</strong></div>
                <div><span>进行中</span><strong>{{ checkQueue.inFlight || 0 }}</strong></div>
                <div><span>成功</span><strong class="success">{{ checkQueue.alive || 0 }}</strong></div>
                <div><span>失败</span><strong class="failed">{{ checkQueue.failed || 0 }}</strong></div>
              </div>
              <div class="queue-schedule">
                <Timer />
                <div>
                  <span>下一次自动检测</span>
                  <strong>{{ checkQueue.isRunning ? '当前批次执行中' : formatRelativeTime(dashboard.operations?.nextCheckAt, '即将开始') }}</strong>
                </div>
                <em>{{ checkQueue.isRunning ? `本批共 ${checkQueue.total || 0} 个` : formatDate(dashboard.operations?.nextCheckAt) }}</em>
              </div>
              <div class="queue-time">
                <span>{{ checkQueue.isRunning ? `开始于 ${formatDate(checkQueue.startedAt)}` : `完成于 ${formatDate(checkQueue.finishedAt)}` }}</span>
                <span>并发上限 {{ checkQueue.concurrency || '—' }}</span>
              </div>
            </div>
            <div class="task-list queue-actions">
              <button @click="runAction('scan', '采集')"><span class="task-icon"><Refresh /></span><span><strong>扫描公开列表</strong><small>上次 {{ formatDate(dashboard.operations?.lastScanAt) }}</small></span><em>运行</em></button>
              <button :disabled="checkQueue.isRunning" @click="runAction('check', '全量检测', { force: true })"><span class="task-icon"><VideoPlay /></span><span><strong>强制检测一批</strong><small>忽略到期时间，按优先级取一批</small></span><em>{{ checkQueue.isRunning ? '运行中' : '运行' }}</em></button>
              <button @click="runAction('prune', '清理')"><span class="task-icon"><Delete /></span><span><strong>清理长期失效</strong><small>上次 {{ formatDate(dashboard.operations?.lastPruneAt) }}</small></span><em>运行</em></button>
            </div>
          </article>
        </div>
      </section>

      <section v-else-if="activeView === 'proxies'" class="content">
        <article class="panel table-panel">
          <div class="table-toolbar">
            <div class="filters">
              <el-input v-model="query.q" :prefix-icon="Search" clearable placeholder="搜索 IP" @keyup.enter="searchProxies" />
              <el-select v-model="query.protocol" clearable placeholder="全部协议" @change="searchProxies">
                <el-option label="HTTP" value="http" /><el-option label="SOCKS4" value="socks4" /><el-option label="SOCKS5" value="socks5" />
              </el-select>
              <el-select v-model="query.status" clearable placeholder="全部状态" @change="searchProxies">
                <el-option label="可用" value="alive" /><el-option label="待检测" value="pending" /><el-option label="失效" value="dead" />
              </el-select>
              <el-button :icon="Search" @click="searchProxies">筛选</el-button>
            </div>
            <el-button type="primary" :icon="Plus" @click="openProxyDialog">手动添加</el-button>
          </div>

          <el-table
            v-loading="loading"
            :data="proxyRows"
            row-key="id"
            class="proxy-table"
            @sort-change="sortProxies"
          >
            <el-table-column prop="address" label="代理地址" min-width="190">
              <template #default="{ row }"><div class="address-cell"><strong>{{ row.host }}:{{ row.port }}</strong><span>{{ row.exitIp ? `出口 ${row.exitIp}` : '尚无出口信息' }}</span></div></template>
            </el-table-column>
            <el-table-column prop="protocol" label="协议" width="115"><template #default="{ row }"><span class="protocol-chip">{{ protocolLabel(row.protocol) }}</span></template></el-table-column>
            <el-table-column prop="status" label="状态" width="115" sortable="custom"><template #default="{ row }"><el-tag :type="statusMeta(row.status)[1]" effect="light" round>{{ statusMeta(row.status)[0] }}</el-tag></template></el-table-column>
            <el-table-column label="归属地" min-width="170"><template #default="{ row }"><span v-if="row.status === 'alive'" class="geo-location">{{ geoLabel(row.geoLocation) || (row.exitIp ? '查询中/未知' : '尚无出口信息') }}</span></template></el-table-column>
            <el-table-column prop="latencyMs" label="延迟" width="115" sortable="custom"><template #default="{ row }"><span :class="['latency', { fast: row.latencyMs != null && row.latencyMs < 1000 }]">{{ row.latencyMs != null ? `${row.latencyMs} ms` : '—' }}</span></template></el-table-column>
            <el-table-column prop="successRate" label="成功率" width="110" sortable="custom"><template #default="{ row }">{{ successRate(row) }}</template></el-table-column>
            <el-table-column label="来源" min-width="150"><template #default="{ row }"><span class="source-count">{{ row.sources.length ? row.sources.join('、') : row.isPinned ? '手动固定' : '未知' }}</span></template></el-table-column>
            <el-table-column prop="nextCheckAt" label="下次检测" width="170">
              <template #default="{ row }">
                <div class="next-check-cell" :class="{ due: isCheckDue(row.nextCheckAt) }" :title="`上次检测 ${formatDate(row.lastCheckedAt)}`">
                  <strong>{{ formatRelativeTime(row.nextCheckAt, '等待入队') }}</strong>
                  <span>{{ row.nextCheckAt ? formatDate(row.nextCheckAt) : '尚未首次检测' }}</span>
                </div>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="130" fixed="right">
              <template #default="{ row }"><el-button link type="primary" :loading="checkingIds.has(row.id)" @click="checkProxy(row)">检测</el-button><el-button link type="danger" @click="removeProxy(row)">删除</el-button></template>
            </el-table-column>
          </el-table>
          <div class="pagination-wrap"><span>共 {{ proxyTotal.toLocaleString() }} 条记录</span><el-pagination v-model:current-page="query.page" v-model:page-size="query.pageSize" background layout="prev, pager, next" :total="proxyTotal" @current-change="loadProxies" /></div>
        </article>
      </section>

      <section v-else class="content">
        <article class="panel table-panel">
          <div class="table-toolbar"><div><h2>公开代理采集源</h2><p>支持每行 IP:PORT 或带协议前缀的文本列表</p></div><el-button type="primary" :icon="Plus" @click="openSourceDialog()">添加采集源</el-button></div>
          <el-table :data="sources" row-key="id" class="source-table">
            <el-table-column label="名称" min-width="190"><template #default="{ row }"><div class="source-name"><span class="source-logo"><Link /></span><div><strong>{{ row.name }}</strong><small v-if="row.isBuiltIn">内置源</small></div></div></template></el-table-column>
            <el-table-column label="协议" width="110"><template #default="{ row }"><span class="protocol-chip">{{ protocolLabel(row.protocol) }}</span></template></el-table-column>
            <el-table-column label="地址" min-width="330"><template #default="{ row }"><a :href="row.url" target="_blank" rel="noreferrer">{{ row.url }}</a></template></el-table-column>
            <el-table-column label="最近结果" width="150"><template #default="{ row }"><div class="scan-result" :class="{ failed: row.lastError }"><strong>{{ row.lastError ? '失败' : `${row.lastFound} 条` }}</strong><small>{{ formatDate(row.lastScanAt) }}</small></div></template></el-table-column>
            <el-table-column label="启用" width="85"><template #default="{ row }"><el-switch v-model="row.enabled" @change="toggleSource(row)" /></template></el-table-column>
            <el-table-column label="操作" width="130" fixed="right"><template #default="{ row }"><el-button link type="primary" :icon="Edit" @click="openSourceDialog(row)">编辑</el-button><el-button link type="danger" :icon="Delete" @click="removeSource(row)" /></template></el-table-column>
          </el-table>
        </article>
      </section>
    </main>

    <el-dialog v-model="proxyDialogVisible" title="手动添加代理" width="460px">
      <el-form label-position="top">
        <el-form-item label="主机或 IP"><el-input v-model="proxyForm.host" placeholder="例如 203.0.113.10" /></el-form-item>
        <div class="form-grid"><el-form-item label="端口"><el-input-number v-model="proxyForm.port" :min="1" :max="65535" controls-position="right" /></el-form-item><el-form-item label="协议"><el-select v-model="proxyForm.protocol"><el-option label="HTTP" value="http" /><el-option label="SOCKS4" value="socks4" /><el-option label="SOCKS5" value="socks5" /></el-select></el-form-item></div>
        <el-form-item><el-checkbox v-model="proxyForm.isPinned">固定记录，不参与自动清理</el-checkbox></el-form-item>
      </el-form>
      <template #footer><el-button @click="proxyDialogVisible = false">取消</el-button><el-button type="primary" @click="saveProxy">添加代理</el-button></template>
    </el-dialog>

    <el-dialog v-model="sourceDialogVisible" :title="editingSourceId ? '编辑采集源' : '添加采集源'" width="560px">
      <el-form label-position="top">
        <el-form-item label="名称"><el-input v-model="sourceForm.name" placeholder="便于识别的名称" /></el-form-item>
        <el-form-item label="列表 URL"><el-input v-model="sourceForm.url" placeholder="https://example.com/proxies.txt" /></el-form-item>
        <div class="form-grid"><el-form-item label="默认协议"><el-select v-model="sourceForm.protocol"><el-option label="HTTP" value="http" /><el-option label="SOCKS4" value="socks4" /><el-option label="SOCKS5" value="socks5" /></el-select></el-form-item><el-form-item label="状态"><el-switch v-model="sourceForm.enabled" active-text="启用" inactive-text="停用" /></el-form-item></div>
      </el-form>
      <template #footer><el-button @click="sourceDialogVisible = false">取消</el-button><el-button type="primary" @click="saveSource">保存</el-button></template>
    </el-dialog>
  </div>
  </el-config-provider>
</template>
