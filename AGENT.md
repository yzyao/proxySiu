# ProxySiu 协作指南

## 项目目标

ProxySiu 是自托管的公开代理池维护工具。它采集 HTTP、SOCKS4、SOCKS5 候选，检测连通性与出口归属地，并通过 Vue 管理台和只读代理 API 对外提供结果。

## 目录与职责

| 路径 | 职责 |
| --- | --- |
| `src/ProxySiu.Api` | ASP.NET Core Minimal API、认证、后台采集/检测/清理和 JSON 持久化 |
| `src/ProxySiu.Api/Services` | 解析、检测、调度、GeoIP、维护队列与业务逻辑 |
| `src/ProxySiu.Api/Storage` | `proxy-pool.json` 的并发安全读写与备份 |
| `src/ProxySiu.Web` | Vue 3 / Vite / Element Plus 独立管理台 |
| `src/ProxySiu.Web/src/api.js` | 前端 API 请求封装 |
| `src/ProxySiu.Web/src/App.vue` | 页面状态、交互与主要视图 |
| `compose.yaml` | 两容器部署：内部 API 与仅本机暴露的 Web |
| `tests/ProxySiu.Api.Tests` | 无外部测试框架的可执行回归测试 |

## 当前策略约束

- 所有档位共用 `MaxPoolSize`，默认 6,000；档位只控制并发、批量与调度节奏。
- 池满时淘汰优先级：多次失败 Dead → 从未成功 Dead → Pending → 恢复中 Dead → Alive。
- 首轮只检测 Pending；稳定阶段按 Alive / Pending / Dead 的检测名额选批，再用同顺序补齐。
- 一个时间点只能有一个维护任务。`force=false` 只检测到期记录，`force=true` 忽略到期时间。
- `CheckQueue` 的等待、进行中、成功、失败只描述正在执行的检测任务；不能把它当作全局到期代理统计。
- Web 热切换档位只保存在当前进程；重启后从 `.env` 的 `PROXYSIU_PROFILE` 恢复。

## 常用命令

```powershell
# 后端与回归测试
dotnet run --project .\tests\ProxySiu.Api.Tests\ProxySiu.Api.Tests.csproj

# 前端构建（不要手工修改 dist）
cd src\ProxySiu.Web
npm run build

# 本地后端
dotnet run --project .\src\ProxySiu.Api

# Compose 部署
docker compose up -d --build
```

开发前端时可在 `src/ProxySiu.Web` 执行 `npm run dev`；Vite 将 `/api` 转发到本机 `127.0.0.1:5080`。

## 配置与安全

- 根目录 `.env` 用于本地开发。Compose 不会挂载它，而是将 `PROXYSIU_PROFILE`、`PROXYSIU_ACCESS_TOKEN`、`PROXYSIU_MAX_POOL_SIZE`、`PROXYSIU_REMOVE_UNSEEN_AFTER_HOURS` 和 GeoIP 变量注入 API 容器。
- 默认仅监听 `127.0.0.1:5080`。Compose 通过内部网络访问 API，并显式启用内部访问；不要给 `api` 添加宿主机端口映射。
- 浏览器管理接口使用 Token 换取 `HttpOnly` Cookie 会话。`/api/proxy/random`、`/plain`、`/countries` 可使用同一 Token 的 Bearer 或 `X-API-Key` 认证；Token 不得进入前端代码或 `localStorage`。
- 保持 `AllowPrivateNetworks=false`，避免代理检查或采集源被用来访问内网。
- 公开代理不可信，不能承载账号、Cookie、Token 或其他敏感数据。

## 开发约定

- 修改代理池状态必须通过 `JsonProxyStore`，不可直接写 JSON 文件。
- API JSON 使用 camelCase；前端字段与 `Contracts` DTO 同步。
- 前端改动后执行 `npm run build`；不要提交 `dist`、`node_modules`、`bin`、`obj`、运行数据或临时产物。
- 后端改动后运行测试项目。若本地 API 占用 `bin` 下的 apphost，可用 `--artifacts-path` 在独立目录验证构建。
- 维护任务日志应保持可操作：开始、完成、耗时和处理统计；不要把高频检测细节提升为默认 `Information` 日志。
