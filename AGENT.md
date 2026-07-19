# ProxySiu 协作指南

## 项目目标

ProxySiu 是一个仅限本机访问的公开代理池管理工具。它从配置的公开列表采集候选代理，检测 HTTP、SOCKS4、SOCKS5 连通性，并在 Vue 管理台中展示池状态、检测队列和采集源。

## 目录与职责

| 路径 | 职责 |
| --- | --- |
| `src/ProxySiu.Api` | ASP.NET Core Minimal API、后台采集与检测任务、JSON 持久化 |
| `src/ProxySiu.Api/Services` | 代理解析、检测、调度、安全校验与业务逻辑 |
| `src/ProxySiu.Api/Storage` | `proxy-pool.json` 的并发安全读写 |
| `src/ProxySiu.Api/wwwroot` | Vue 构建产物；不要手工编辑 `assets` 文件 |
| `src/ProxySiu.Web` | Vue 3 / Vite / Element Plus 管理界面 |
| `src/ProxySiu.Web/src/api.js` | 前端 API 请求封装 |
| `src/ProxySiu.Web/src/App.vue` | 页面状态、交互与主要视图 |

## 常用命令

```powershell
# 后端构建
dotnet build .\ProxySiu.slnx

# 前端构建（产物输出到 API 的 wwwroot）
cd src\ProxySiu.Web
npm run build

# 启动服务
cd ..\ProxySiu.Api
dotnet run
```

开发前端时可在 `src/ProxySiu.Web` 执行 `npm run dev`。Vite 会将 `/api` 转发给本机 5080 端口。

## 开发约定

- 修改代理池状态必须通过 `JsonProxyStore`，不要绕过它直接写 JSON 文件。
- API 返回 JSON 使用 camelCase；前端字段名与 `Contracts` 中的 DTO 保持同步。
- 前端改动后执行 `npm run build`，不要手工修改 `src/ProxySiu.Api/wwwroot/assets`。
- 后台队列状态由 `ProxyPoolService` 汇总，经 `/api/dashboard` 提供；前端不要自行推测检测进度或下次检测时间。
- `appsettings.json` 中的检测频率、并发与批次限制是安全边界的一部分，修改前应评估对目标网络、云服务商策略和月度流量的影响。

## 安全边界

- 默认只监听 `127.0.0.1:5080`，且 `AllowRemoteAccess=false` 时拒绝非本机请求。
- 保持 `AllowPrivateNetworks=false`，避免代理或采集源被用于访问内网地址。
- 不要通过公开代理传输账号、Cookie、密钥或任何敏感数据。
- 若需要远程管理，先部署 HTTPS 与认证。管理网页使用服务端 Cookie 会话；程序调用使用独立、可撤销且分权限的 API Key。不要把固定密钥嵌入 Vue 代码或存入 `localStorage`。

## 交付检查

- 后端：`dotnet build .\ProxySiu.slnx`
- 前端：`npm run build`
- 冒烟检查：`GET /api/health`、`GET /api/dashboard`，确认服务仍只监听回环地址。
- 不提交运行时数据、`node_modules`、`bin`、`obj` 或临时截图。
