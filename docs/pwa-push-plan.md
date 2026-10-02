# PWA 推送部署方案（EdgeOne Makers + 边缘函数 + KV）

> ## ⚠️ 已搁置 —— 仅作调研存档（2026-10）
>
> 本方案**曾经完整实现并自测通过（138 项）**，但最终**决定不采用**，
> 配套代码已从仓库移除，本文只保留设计、实测数据与踩过的坑，供以后重新评估时参考。
>
> **搁置原因**
> 1. **国内侧数据源是硬约束**：学校服务器只在大陆网络可达（境外 DNS/IP 双层不通，见
>    §2.4 实测），而推送分发必须在海外边缘节点 → 要么依赖境内设备常开，要么用国内云函数
>    （腾讯云 SCF 免费额度仅前三个月，实算约 ¥2.5/年）或国内 VPS。
> 2. **请求模式更显眼**（决定性原因）：网页要"及时"就得比托盘程序轮询得更密。
>    5 分钟一次 = 每天 288 次、每月约 8 GB 流量，是托盘程序默认 30 分钟间隔的 **6 倍**。
>    万一学校 WAF 因此收紧接口，代价会落到全校同学头上。
> 3. 托盘程序已覆盖本机提醒的核心需求，站点还要额外背上域名、备案判断、云函数计费、
>    推送订阅维护一整套，收益与复杂度不划算。
>
> **本文档仍然有效的部分**（与是否采用无关）：
> 学校 WAF 的请求头实测（见 README 2.1 节 `Origin` → 403）、EdgeOne 与 Cloudflare
> 的能力/免费额度对照、iOS 主屏 Web Push 的限制、成本实算、以及风险清单。
>
> 文中提到的 `edge-functions/`、`web/`、`tools/`、`serverless/` 等路径**在本仓库已不存在**。
>
> 关联文档：[README](../README.md)（第 2.4 节境外不可达实测、第 6 节搁置说明）。

> 关于实现细节（存档）：落地时把后端做成**单文件通配路由**（`/api/[[default]].js`），
> 以规避平台对跨文件 `import` 的未定义行为；并去掉 SPA `rewrites`，站点只服务根路径。

---

## 0. 结论摘要

- **可行**。EdgeOne Makers 免费版提供：静态托管 + 边缘函数 + KV（1GB）+ 自定义域名 + 免费 SSL，
  边缘函数基于 V8，**WebCrypto 全算法可用**（ECDH / HKDF / AES-GCM / ECDSA），足够自行实现
  Web Push 的 VAPID 签名（ES256）与 `aes128gcm` 载荷加密（RFC 8291）。
- **但抓学校接口这一步不能放进 EdgeOne**。学校服务器境外 DNS/IP 双层不可达（README 2.4 节实测），
  而 EdgeOne 的项目**加速区域决定 Cloud Functions 可用地域**：选"全球（不含中国大陆）"则免备案、
  但只能部署海外区域；选含大陆区域则**自定义域名必须 ICP 备案**。所以国内侧抓数据仍由
  **托盘程序**或**腾讯云 SCF（广州）**承担。
- **必须有自定义域名**。免费版的项目/部署域名在大陆网络下必须使用平台预览链接（**3 小时有效，过期 401**），
  而 iOS 主屏安装与 Service Worker 都要求 origin 稳定。
- **"支持 PWA 的设备都能推"要打折**：真正条件是"浏览器支持 Web Push"。iPhone（16.4+，需添加到主屏幕）
  与桌面浏览器最可靠；**国内无 Google 服务框架的 Android 基本收不到**；**微信内置浏览器完全不支持**。
  建议在同一处 fan-out 逻辑里并联一个兜底通道（Server酱 / Bark / 企业微信机器人）。
- **成本**：EdgeOne Makers 免费版 + SCF 免费额度 + 域名（¥10–30/年）。

---

## 1. 前置条件清单

| # | 项 | 说明 | 完成 |
|---|---|---|---|
| 1 | 腾讯云账号（实名） | EdgeOne Makers 与 SCF 都需要 | ☐ |
| 2 | 一个**可用于 CNAME** 的域名 | 已有；选"全球（不含中国大陆）"区域时**无需 ICP 备案** | ☐ |
| 3 | 开通 EdgeOne Makers（Pages） | 控制台创建站点/项目 | ☐ |
| 4 | 开通 KV 并创建命名空间 | 需在控制台"存储 - KV"点"立即申请" | ☐ |
| 5 | 一个收验证邮件的地址 | VAPID 的 `sub` claim 用（`mailto:`） | ☐ |
| 6 | （可选）腾讯云 SCF | 用于 7×24，不依赖 PC 开机 | ☐ |

---

## 2. 架构与取舍

```
        ┌─ 静态 PWA（manifest + Service Worker）   ← EdgeOne 静态托管（海外节点，免备案）
手机 ───┤
        ├─ POST /api/subscribe ──► Edge Function ──► KV（订阅表）
        └─ 收通知 ◄── APNs / FCM / Mozilla
                            ▲
                            │ Web Push：VAPID(ES256 JWT) + aes128gcm(RFC 8291)
数据源（必须国内）           │
 A. 托盘程序（现已每 30 分钟查询）──► POST /api/ingest ─► Edge Function
 B. 腾讯云 SCF（广州）定时器 ────────┘   阈值判断 + 读 KV + 分发推送（+ 兜底通道）
```

**为什么这样切**

| 环节 | 必须在哪里 | 原因 |
|---|---|---|
| 抓学校 `getRoomInfo` | **中国大陆** | 学校权威 NS 只对国内解析器作答，境外 IP 直连超时（README 2.4） |
| 推送发送（POST 到 FCM/APNs/Mozilla） | **中国大陆以外** | `fcm.googleapis.com` 等在大陆不可达；边缘节点须落在海外 |
| 订阅存储 + 阈值判断 + fan-out | 海外边缘函数 | KV **仅能在 Edge Functions 中使用**（Cloud Functions 读写不了） |
| 静态站 | 海外节点即可 | 选"全球（不含中国大陆）"免备案；大陆用户可访问（延迟略高） |

**方案边界（明确不做）**：不改动、不代刷学校接口的登录态；不碰充值链路；推送内容不含个人标识；
托盘程序继续保持"单文件 exe、无第三方库、C# 5"的约束。

---

## 3. 阶段 0：命门验证（**先做这个，失败就别往下走**）

整个方案有两个命门，必须先用最小代价验证：

**命门 1：大陆网络能稳定访问你的自定义域名（不出现 401 预览限制）**

1. 随便部署一个静态文件到 EdgeOne 项目，绑好自定义域名 + 免费证书（见阶段 1）。
2. 用**手机蜂窝数据**（不要用校园 WiFi，避免代理干扰）访问 `https://<你的域名>/`。
3. 期望：正常 200。若出现 401 或跳转到预览链接，说明域名没绑对或加速区域选错。

**命门 2：海外边缘节点能连通三大推送端点**

放置一个临时边缘函数 `edge-functions/api/status.js`：

```js
export default async function onRequest() {
  const targets = [
    'https://fcm.googleapis.com/',                  // Chrome / Edge（Android、桌面）
    'https://web.push.apple.com/',                  // Safari / iOS 主屏 web app
    'https://updates.push.services.mozilla.com/'    // Firefox
  ];
  const out = {};
  for (const t of targets) {
    try {
      const r = await fetch(t, { method: 'GET' });
      out[t] = 'HTTP ' + r.status;                  // 拿到任何 HTTP 状态码即证明网络可达
    } catch (e) {
      out[t] = 'ERR: ' + (e && e.message ? e.message : String(e));
    }
  }
  return new Response(JSON.stringify(out), {
    headers: { 'content-type': 'application/json; charset=utf-8' }
  });
}
```

访问 `https://<你的域名>/api/status`，期望三个目标都返回 HTTP 状态码（401/404/200 都算通），
**不能是超时或 ERR**。

> 若命门 2 失败：见 §13 的替代方案（把 fan-out 挪到 Cloudflare Workers，或用新加坡 SCF + npm `web-push`）。

---

## 4. 阶段 1：EdgeOne 项目与域名

1. 控制台创建项目，**加速区域选「全球可用区（不含中国大陆）」**。
   - 选这个区域：自定义域名**免 ICP 备案**；但 Cloud Functions 只能部署海外地域（我们本来也不用它抓数据）。
   - 不要选含大陆的区域，否则必须备案；后期想改区域会连带触发备案要求。
2. 添加自定义域名：控制台 → 域名管理 → 添加自定义域名（子域即可，如 `power.example.com`）。
3. 在域名服务商处添加 **CNAME** 记录指向控制台给出的目标（按控制台"配置 DNS 的 CNAME 记录"指引）。
   - 建议用子域而不是裸域，避免 CNAME 与裸域其他记录冲突。
4. 申请**免费 SSL 证书**并等待签发（EdgeOne Makers 支持免费证书/托管证书）。
5. 验证：`https://<子域>/` 返回 200，且证书有效、无混合内容告警。

---

## 5. 阶段 2：KV 命名空间

1. 控制台 → 存储 → KV → 立即申请（开通 KV 账号，免费 1GB）。
2. 创建命名空间，例如 `powerfee-prod`。
3. 在**项目**里绑定该命名空间，**运行时变量名填 `SUB_KV`**（函数里直接以全局变量 `SUB_KV` 使用）。
4. 记住两个硬限制（写代码时会踩）：
   - **键名只能包含数字、字母、下划线**，长度 ≤512B → **不能拿推送 endpoint URL 直接当键**，必须哈希/编码。
   - 值大小按控制台配额页上限 **1MB** 保守处理（免费版）。
   - KV 是**最终一致**，跨节点最长 60s 才能读到新值 → 新订阅后最多 1 分钟才会收到第一条推送，属正常。

---

## 6. 阶段 3：环境变量与密钥

在项目环境变量里配置（值长度上限 1000 字节，够用）：

| 变量名 | 内容 | 生成方式 |
|---|---|---|
| `VAPID_PUBLIC` | VAPID 公钥（base64url，65 字节裸公钥） | 见附录 B |
| `VAPID_PRIVATE_D` | VAPID 私钥标量（base64url，32 字节 `d`） | 见附录 B |
| `VAPID_SUBJECT` | `mailto:你的邮箱` | 手填 |
| `INGEST_SECRET` | 上报共享密钥（随机 ≥32 字节） | `openssl rand -hex 32` |
| `ROOM_TOKEN_<roomNum>` | 每个房间一个订阅令牌（见下） | `openssl rand -hex 16` |

**订阅令牌的用途**：订阅接口必须校验令牌，否则任何知道你域名和房间号的人都能把**自己的设备**挂上来，
而订阅表里同时存在 roomNum 与 endpoint，等于把"谁住哪间"暴露成可枚举数据。

建议的令牌分发方式：生成形如
`https://<子域>/#t=<ROOM_TOKEN>&r=<roomNum>` 的链接，只发给你自己/同宿舍成员；PWA 首页读取
`location.hash` 后在调用 `/api/subscribe` 时带上。校验通过后把订阅写入 KV。

> ⚠️ 托盘程序的 `INGEST_SECRET` **不要写成代码里的默认值**——它会随 exe 分发出去。
> 参照 README 的既有原则：敏感值一律放 `%APPDATA%\PowerFeeTray\config.ini`，不进源码。

---

## 7. 阶段 4：边缘函数

### 7.1 目录结构（Makers 约定：`/edge-functions` 下按路径生成路由）

```
项目根/
├─ edgeone.json
├─ edge-functions/
│  └─ api/
│     └─ [[default]].js   # 单文件承载全部 /api/* 路由（见下）
├─ web/                   # 静态站（PWA）
│  ├─ index.html
│  ├─ app.js
│  ├─ sw.js               # 必须放在作用域根目录
│  ├─ manifest.json
│  └─ icons/ (180/192/512 png)
└─ docs/pwa-push-plan.md  # 本文档
```

**最终的接口清单**（都在 `edge-functions/api/[[default]].js` 里，通配路由内部分发）：

| 路由 | 方法 | 用途 | 鉴权 |
|---|---|---|---|
| `/api/status` | GET | 健康检查；`?probe=1` 探测三大推送端点连通性 | 公开 |
| `/api/state` | GET | 余额 / 历史 / 统计 / 设备数（按令牌解析房间） | 房间令牌 |
| `/api/subscribe` | POST | 登记 Web Push 订阅 | 房间令牌 |
| `/api/unsubscribe` | POST | 退订 | 房间令牌 |
| `/api/refresh` | POST | 经国内代理即时查询一次 | 房间令牌 |
| `/api/ingest` | POST | 接收国内侧读数 → 判阈值 → 分发推送 | `X-Sign` |
| `/api/rooms` | GET | 自助注册用：校区→楼栋→房间 三级树 | 邀请码 |
| `/api/register` | POST | 自助注册：签发该房间的独立令牌 | 邀请码 |
| `/api/admin-room` | POST | 写入/更新房间配置、签发令牌；`op=invite` 设置邀请码 | `X-Sign` |
| `/api/admin-rooms` | POST | 给 SCF 用：返回当前应监控的房间清单 | `X-Sign` |

### 7.2 KV 键设计

| 键 | 值 | 说明 |
|---|---|---|
| `cfg_<roomNum>` | `{roomNum, name, campus, building, room, threshold, warnRatio, cooldownMinutes, token}` | 房间配置；**这也是"房间注册表"**——`/api/admin-rooms` 就是列出 `cfg_` 前缀的键 |
| `tok_<token>` | `{roomNum, createdAt, source}` | 令牌 → 房间（服务端解析房间号的唯一入口，前端从不持有房间号） |
| `toks_<roomNum>` | `{tokens:[{token, createdAt, ua}]}` | 该房间已签发的全部令牌（自助注册每次签一条，便于逐个吊销） |
| `invite_code` | `{code, enabled, createdAt}` | 自助注册的邀请码；键名**刻意不带 `cfg_` 前缀**，免得混进房间清单 |
| `rooms_index` | `{fetchedAt, total, rooms:[[roomNum, room, building, campus],…]}` | 房间列表缓存（约 85 KB / 2077 间），7 天内不重复向国内代理拉取 |
| `sub_<hash>` | `{endpoint, p256dh, auth, roomNum, ua, createdAt, lastOkAt, failCount}` | `hash` = `sha256(endpoint)` 的**十六进制前 32 位**（满足"仅数字字母"的键名限制） |
| `subs_<roomNum>` | `["sub_ab12…", "sub_cd34…"]` | 该房间的订阅索引；房间内人数有限，一个值足够 |
| `state_<roomNum>` | `{balance, ts, lastAlertAt, level, source}` | 去重与冷却状态 |
| `hist_<roomNum>` | `{points:[[ts,balance],…]}` | 7 天采样，最多 600 点 |

遍历 `cfg_` 用 `SUB_KV.list({ prefix: 'cfg_', limit: 256 })`（`/api/admin-rooms` 走这条）。
本场景用户数以"几间宿舍"计，其余读取都是一次 `get`。

### 7.3 `POST /api/subscribe`

请求体：

```json
{ "token": "<ROOM_TOKEN>", "roomNum": "1001",
  "subscription": { "endpoint": "https://…", "keys": { "p256dh": "…", "auth": "…" } } }
```

逻辑：
1. 校验 `token` 与该房间令牌一致（常量时间比较）。
2. 校验 `subscription.endpoint` 与 `keys` 非空；仅接受 `https://`。
3. `hash = sha256hex(endpoint).slice(0,32)`；`put('sub_' + hash, JSON)`；
   把 `sub_<hash>` 追加进 `subs_<roomNum>`（读-改-写，重复则幂等跳过）。
4. 返回 `{ ok: true, key: 'sub_xxx' }`。

### 7.4 `POST /api/ingest`

请求头：`X-Sign: HMAC-SHA256(INGEST_SECRET, ts + "\n" + body)`（十六进制小写），
请求体：

```json
{ "roomNum": "1001", "balance": "12.34", "ts": 1767000000 }
```

逻辑：
1. 校验签名与 `|now - ts| <= 300s`（防重放）。
2. `state = await SUB_KV.get('state_<roomNum>', {type:'json'})`。
3. 计算等级：`low`（< 阈值）/ `warn`（< 阈值 × `warnRatio`）/ `ok`（其余），阈值与 `warnRatio`、
   冷却时间与托盘程序保持同一套语义（默认 20 度 / 2.0 / 180 分钟）。
4. 触发条件：等级进入更差的一档，**或**仍处于 `low` 且距 `lastAlertAt` 超过冷却时间。
   → 避免每 30 分钟重复轰炸。
5. 需要推送时：读 `subs_<roomNum>` → 逐个 `get('sub_' + key)` → 调用 §7.5 发送 → 汇总成功/失败。
6. 遇到 `404/410` 的订阅：从 KV 删除并从 `subs_` 索引里摘掉。
7. 更新 `state_<roomNum>`；返回 `{ ok: true, level: 'low', sent: 1, failed: 0 }`。

> 阈值不要从请求体接收，避免上报端被伪造后任意触发；阈值放环境变量或 KV 配置，与托盘程序手动保持一致。

### 7.5 推送发送：VAPID（ES256）+ `aes128gcm`（RFC 8291）

这是唯一"有点技术含量"的地方，EdgeOne 的 WebCrypto 能力表已确认够用
（ECDH：`generateKey/deriveBits/importKey/exportKey`；HKDF：`deriveBits/importKey`；
AES-GCM：`encrypt`，单次 1MB 上限；ECDSA：`sign`）。步骤：

**A. VAPID JWT**

1. `aud` = 推送 endpoint 的 **origin**（如 `https://fcm.googleapis.com`、`https://web.push.apple.com`），
   **必须与 endpoint 一致**，否则 401/403。
2. Header：`{"typ":"JWT","alg":"ES256"}`；Payload：`{"aud":aud,"exp":now+12h,"sub":VAPID_SUBJECT}`（`exp` 不得超过 24h）。
3. `base64url(header) + "." + base64url(payload)` 作为待签数据。
4. 用 ECDSA P-256 + SHA-256 签名：从 `VAPID_PRIVATE_D` 与公钥的 x/y 组装 JWK
   （`{kty:'EC', crv:'P-256', d, x, y}`）→ `importKey('jwk', …)` → `sign({name:'ECDSA',hash:'SHA-256'}, …)`。
   WebCrypto 返回的签名已是 **raw r||s（64 字节）**，直接 `base64url` 即可（无需 DER 转换）。
5. 请求头：`Authorization: vapid t=<jwt>, k=<VAPID_PUBLIC>`。

**B. 载荷加密（`aes128gcm`）**

输入：`ua_public` = `keys.p256dh`（65 字节）、`auth_secret` = `keys.auth`（16 字节）。

1. 生成临时 ECDH P-256 密钥对 `as`；导出 `as_public`（65 字节裸公钥）。
2. `ecdh_secret = ECDH(as.private, ua_public)`（32 字节）。
3. `auth_info = "WebPush: info\x00" || ua_public || as_public`。
4. `ikm = HKDF(salt=auth_secret, ikm=ecdh_secret, info=auth_info, len=32)`
   —— WebCrypto 写法：`importKey('raw', ecdh_secret, 'HKDF', false, ['deriveBits'])` 后
   `deriveBits({name:'HKDF', hash:'SHA-256', salt:auth_secret, info:auth_info}, key, 256)`。
   （HKDF 在 WebCrypto 里只支持 `deriveBits`，不支持 `deriveKey`——用 `deriveBits` 两段式即可。）
5. `salt = crypto.getRandomValues(new Uint8Array(16))`。
6. `cek = HKDF(salt=salt, ikm=ikm, info="Content-Encoding: aes128gcm\x00", len=16)`。
7. `nonce = HKDF(salt=salt, ikm=ikm, info="Content-Encoding: nonce\x00", len=12)`。
8. 明文 = `JSON.stringify(payload)` 的 UTF-8 + 分隔符 `0x02`（单条记录，最后一条记录用 `0x02`）。
9. `ciphertext = AES-128-GCM(cek, nonce, 明文)`（含 16 字节 tag）。
10. 请求体 = `salt(16) || rs(4, 大端, 例如 4096) || idlen(1)=65 || as_public(65) || ciphertext`。
11. 请求头：
    - `Content-Encoding: aes128gcm`
    - `Content-Type: application/octet-stream`
    - `TTL: 86400`（可按需调小）
    - `Urgency: high`（低电量告警值得高优先级；iOS 会按此决定是否及时投递）
    - `Topic: powerfee-<roomNum>`（同 topic 的新消息会覆盖旧的未读消息，避免堆积）

**C. 响应处理**

- `201/200` → 成功，更新 `lastOkAt`。
- `404/410` → 订阅已失效，删除 KV 记录（**必须做**，否则订阅表只增不减）。
- `429` → 退避重试（记录 `failCount`，下次 ingest 再试）。
- 其他 `4xx/5xx` → 记为失败，写日志，不重试风暴。

**D. 批量注意**

边缘函数单次 **CPU 时间限 200ms**（免费版 300 万 ms/月）。几十个订阅的 ECDH+AES-GCM 是毫秒级，
安全；但**不要**在这条路径上做几十次串行 KV 往返之外的额外加密工作。
推送串行发送即可，必要时用 `Promise.all` 并发，但要限制并发度（例如 8）。

### 7.6 兜底通道扩展点

在 §7.4 第 5 步的 fan-out 里并联一个函数即可，零架构改动：

| 通道 | 覆盖 | 备注 |
|---|---|---|
| Server酱 / PushPlus | 微信接收 | 最简单，一个 `fetch` POST |
| Bark | iOS | 若想要比 Web Push 更稳的 iOS 通道 |
| 企业微信 / 钉钉 / 飞书 机器人 | 群通知，适合整间宿舍 | Webhook 一条 `fetch` |
| 邮件（如用 SCF 侧发） | 全平台 | 兜底中的兜底 |

**触发条件建议**：仅当 Web Push 全部失败、或该房间**没有任何订阅**时才走兜底，避免双份通知轰炸。

---

## 8. 阶段 5：PWA 前端

### 8.1 `manifest.json`

```json
{
  "id": "/",
  "name": "宿舍电费提醒",
  "short_name": "电费",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#ffffff",
  "theme_color": "#2e7d32",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

要点：
- iOS 要求 `display` 为 **`standalone` 或 `fullscreen`**，否则"添加到主屏幕"只会存成书签，**收不到推送**。
- iOS 图标更认 `apple-touch-icon`（180×180），在 `index.html` 里也加一份 `<link rel="apple-touch-icon">`。
- 加 `<meta name="apple-mobile-web-app-capable" content="yes">`。

### 8.2 `sw.js`（放根目录，作用域才覆盖全站）

```js
self.addEventListener('install', (e) => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  // 始终携带 JSON payload（避免空推送在部分平台上不触发通知）
  let data = { title: '宿舍电费提醒', body: '电量偏低', url: '/' };
  try { if (event.data) data = Object.assign(data, event.data.json()); } catch (e) {}
  event.waitUntil(self.registration.showNotification(data.title, {
    body: data.body,
    icon: '/icons/icon-192.png',
    badge: '/icons/badge.png',
    tag: data.tag || 'powerfee',
    renotify: true,
    data: { url: data.url || '/' }
    // 注意：iOS 对 actions 支持有限，不要依赖按钮
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || '/';
  event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    .then((list) => {
      for (const c of list) { if (c.url.indexOf(self.registration.scope) === 0 && 'focus' in c) return c.focus(); }
      return self.clients.openWindow(url);
    }));
});
```

### 8.3 订阅流程（含各平台差异）

```
是否支持：'serviceWorker' in navigator && 'PushManager' in window
  ↓ 否 → 显示"当前浏览器不支持通知"
是否 iOS 主屏 web app：window.matchMedia('(display-mode: standalone)').matches
  ↓ 否（且是 iOS）→ 显示引导：Safari 打开 → 分享 → 添加到主屏幕 → 从图标启动
用户点"开启通知"（必须是用户手势，iOS 强制要求）
  ↓
Notification.requestPermission() → 'granted'
  ↓
reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(VAPID_PUBLIC) })
  ↓
POST /api/subscribe（带 #t= 令牌与 roomNum）
```

其他必须处理的点：

- **每次打开都做一次对账**：若本地已有订阅但服务端可能丢失（例如 KV 数据被清），调用 `/api/subscribe`
  幂等重登记一次；iOS 上 `pushsubscriptionchange` 并不可靠，别只依赖它。
- **提供"重新启用通知"按钮**：用于用户换机、清除站点数据、权限被系统回收后的恢复路径。
- **`applicationServerKey` 必须是 65 字节裸公钥**（base64url 解码后首字节 `0x04`），
  误传 PEM/DER 会静默 subscribe 失败。
- **微信内置浏览器检测**：UA 含 `MicroMessenger` 时直接显示：

  > 微信内置浏览器不支持消息推送，也无法添加到桌面。
  > 请点右上角「…」→「在浏览器打开」，或复制本页链接到 Safari / Chrome 打开。

  这一步很关键——你们原页面的入口是 `from=wxminiprogram`，用户极可能从微信进来。

### 8.4 缓存策略（避免更新卡死）

| 路径 | `Cache-Control` |
|---|---|
| `/sw.js` | `no-cache`（必须） |
| `/manifest.json` | `no-cache` |
| `/icons/*` | `public, max-age=31536000, immutable` |
| `/index.html`、`/app.js` | `no-cache`（或在 SW 里用版本号控制） |

用 `edgeone.json` 的 `headers` 配置（上限 30 条，值不含中文）。

---

## 9. 阶段 6：数据源接入

### 9.1 方案 A：托盘程序上报（零新增服务，PC 开机即有推送）

改动点集中在 `src/PowerFeeTray.cs`（C# 5 约束：不能用 `$""`、`?.`、`nameof`），
在**查询成功拿到余额之后**追加一次上报，失败必须**非致命**（只写日志，绝不阻塞查询/托盘循环）：

```csharp
// 伪代码，示意；实际落地时沿用源码既有的日志与异常处理风格
static void ReportToCloud(string roomNum, string balance)
{
    try
    {
        long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        string body = "{\"roomNum\":\"" + roomNum + "\",\"balance\":\"" + balance + "\",\"ts\":" + ts + "}";

        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        using (HMACSHA256 hmac = new HMACSHA256(keyBytes))
        {
            byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(ts + "\n" + body));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < mac.Length; i++) sb.Append(mac[i].ToString("x2"));
            string sign = sb.ToString();
            // HttpWebRequest: POST {cloudUrl}/api/ingest，头 X-Sign、Content-Type: application/json
            // Timeout 5000ms，TLS 已在 Main 里全局设置，勿在此处重设
        }
    }
    catch (Exception ex) { Log("上报失败(忽略): " + ex.Message); }
}
```

要点：
- 新配置项 `cloudUrl` / `cloudSecret` 存 `%APPDATA%\PowerFeeTray\config.ini`，**不要给默认值**。
- 上报频率跟随现有查询间隔（默认 30 分钟 → 1440 次/月）。
- 若查询失败（托盘图标转灰那种情况）**不要上报**，避免把错误状态推成"电量正常/异常"。
- 余额是字符串且可能为负，原样上报，由服务端解析（与 README 的字段说明一致）。

### 9.2 方案 B：腾讯云 SCF（广州）定时触发器（补 7×24）

| 项 | 取值 |
|---|---|
| 地域 | **广州 / 上海 / 北京**（绝不要香港、新加坡——会以同样方式失败） |
| 运行时 | Python 3.10 或 Node.js 20（`requests` / `fetch` 均可） |
| 触发 | 定时触发器，每 5 分钟（**用控制台图形化选择，别手写 cron**） |
| 环境变量 | `INGEST_SECRET`、`EDGE_URL`（如 `https://power.example.com/api/ingest`）、`ROOM_NUM` |
| 逻辑 | ① 先向站点 `POST /api/admin-rooms`（签名）索取"当前应监控的房间清单"→ ② 抓一次学校 `getRoomInfo`（`implType=CGCOMMON0001`，一次返回全校，N 个房间仍只抓一次）→ ③ 逐房间签名 `POST /api/ingest`。清单以站点注册表为准，所以**自助注册的房间自动被纳入监控**；`ROOM_NUMS` 仅作兜底/强制项 |
| 超时 | 10s 足够；单次内存 128MB |
| 计费 | **不是免费的**（已核实）：腾讯云 SCF 免费额度自 2022-06-01 起调整，新用户**仅前三个月**免费，第四个月起按量计费。本场景实算 ≈**¥2.5/年**（8640 次调用 + ~1620 GBs + ~26 MB 出流量）；按量计费需账户有余额 |
| 替代 | 不想产生账单就用**境内常开设备轮询**（`serverless/pc-poller/poll.mjs`，0 元）：路由器 / 树莓派 / 闲置旧安卓手机 / 本机计划任务。协议完全一致，站点侧无需改动 |
| 未验证风险 | SCF 走机房 IP，学校 WAF 是否接受**尚未验证**（已知仅住宅/校园网络可查）。首次部署后先手动触发看日志 |

注意事项：
- SCF 默认有公网出网能力（不需要额外配 NAT）。
- 学校接口是 `application/x-www-form-urlencoded`，**真正必需的参数是 `implType`**（缺了会返回
  `{"ret":false,"msg":"查询失败"}`）。
- SCF 侧**只做数据搬运**，阈值/冷却判断统一放在边缘函数，避免两处逻辑漂移。
- 若方案 A 与 B 同时启用，两侧上报同一房间时由服务端的冷却逻辑天然去重（幂等）。

---

## 10. 阶段 7：端到端验证清单

| # | 步骤 | 期望 |
|---|---|---|
| 1 | `curl https://<域名>/api/status` | 三个推送端点均返回 HTTP 状态码 |
| 2 | `curl -X POST https://<域名>/api/ingest`（先用小脚本生成 `X-Sign`） | `{ok:true}`，KV 里出现 `state_<roomNum>` |
| 3 | 伪造签名 / 过期 `ts` 再试 | `401`，KV 无变化 |
| 4 | 桌面 Chrome 打开 PWA → 点"开启通知" → 允许 | `sub_*` 键写入 KV，`subs_<roomNum>` 出现该键 |
| 5 | 用 `balance` 低于阈值触发一次 ingest | 桌面弹出系统通知，内容含余额 |
| 6 | 立刻再触发一次 | **不重复弹**（冷却生效） |
| 7 | iPhone：Safari 打开 → 添加到主屏幕 → 从图标启动 → 点"开启通知" | 系统弹权限框（必须从主屏图标启动！） |
| 8 | 再触发一次 ingest | iPhone 锁屏/通知中心出现通知，点击能打开 PWA |
| 9 | 在 KV 里手动删掉一个订阅后触发 | 该设备不再收到，无报错 |
| 10 | （方案 B）停掉 PC，等 SCF 触发 | 依然收到推送 → 7×24 达成 |
| 11 | 微信内打开链接 | 显示"请在浏览器中打开"引导，不报错 |
| 12 | 关闭一个订阅端后触发 | 服务端捕获 410 并清理该订阅 |

---

## 11. 设备支持矩阵与用户引导

| 平台 | 能否收到 | 前提 |
|---|---|---|
| iPhone / iPad **16.4+** | ✅ | **必须** Safari 打开 → 分享 → 添加到主屏幕 → **从图标启动** → 点按钮授权；走 APNs，**无需 Apple 开发者账号** |
| macOS Safari 16.1+ | ✅ | 需"添加到程序坞"，在标签页里收不到 |
| Windows / macOS Chrome、Edge、Firefox | ✅ | 页面内点按钮授权即可 |
| Android Chrome / Edge / Firefox | ⚠️ | 走 FCM，**需 Google 服务框架（GMS）**；国产 ROM 无 GMS 基本收不到；小米/华为/OPPO 自带浏览器不支持 Web Push |
| **微信内置浏览器** | ❌ | 不支持 Service Worker 推送，也不能加桌面 |
| HarmonyOS NEXT | ❌ | 无 Web Push 能力 |

**给用户的引导文案（可直接放在 PWA 首页）**

> **iPhone**：请用 Safari 打开本页 → 点底部「分享」→「添加到主屏幕」→ 回到桌面点开新图标 → 再点「开启通知」。
> 只在 Safari 标签页里是收不到通知的。
>
> **Android**：用 Chrome 打开 → 点「开启通知」。若你的手机没有安装 Google 服务框架，系统可能收不到通知，
> 这种情况请另外开启兜底通知通道（见 §7.6）。
>
> **电脑**：用 Chrome / Edge / Firefox 打开 → 点「开启通知」。
>
> **微信里打开的话**：点右上角「…」→「在浏览器打开」。

---

## 12. 运维、配额与成本

| 项 | 现状 |
|---|---|
| 边缘函数执行 | 免费版 300 万次/月（本场景 ~1500 次/月） |
| 边缘函数 CPU | 单次 200ms；免费版 300 万 ms/月 |
| KV | 1GB、单值 1MB、命名空间 ≤10 个 |
| 构建 | 500 次/月 |
| 自定义域名 / 证书 | 免费证书支持 |
| 限流与防护 | 免费版可配 4 条自定义规则 + 1 条精准限流 → 给 `/api/subscribe`、`/api/ingest` 各加一条 |
| 日志 | 控制台可查；单条最大 5MB，**单次查询时间范围最长 24 小时** |
| 现金支出 | 域名 ¥10–30/年；数据源二选一：**境内设备轮询 0 元**，或**广州 SCF ≈¥2.5/年**（免费额度仅前 3 个月） |

建议加的监控：
- 每次 ingest 记录一行 `level / sent / failed`，用日志分析抽查。
- 订阅表做一次"零成功"告警：连续 N 次推送全失败时，走兜底通道（否则推送静默失效没人知道）。
- KV 清理：`failCount >= 5` 或 `410` 的订阅定期清（在 ingest 路径里顺手做）。

---

## 13. 风险与坑（现象 → 原因 → 对策）

| 现象 | 原因 | 对策 |
|---|---|---|
| 大陆网络访问站点 401 | 用了免费版**项目/部署域名**，大陆须走 3 小时预览链接 | 绑自定义域名（阶段 1） |
| 边缘函数连不上 FCM | 加速区域含大陆，边缘节点落在大陆 | 区域选"全球（不含中国大陆）"；用 `/api/status` 验证 |
| 抓不到学校数据 | 函数部署在海外区域 | 抓数据留在国内（托盘程序 / 广州 SCF） |
| `schedules` 定时任务不按预期频率跑 | 官方文档写"最小间隔为一天"，但示例又有每小时触发，**自相矛盾** | 不要依赖 `schedules` 做 30 分钟轮询；用 SCF 定时触发器或托盘程序触发（若想试，先单独压测一周） |
| iOS 里点"开启通知"没反应 / 报错 | 未从主屏幕图标启动，或不在用户手势回调里 | 检测 `display-mode: standalone`，给引导；授权调用放在点击处理器内 |
| iOS 加了桌面但没通知 | manifest `display` 不是 `standalone/fullscreen` → 变成了书签 | 修正 manifest + `apple-touch-icon` |
| 安卓国产机收不到 | 无 GMS，FCM 不可达 | 接受；启用兜底通道 |
| subscribe 静默失败 | `applicationServerKey` 传了 PEM 而非 65 字节裸公钥 | 解码后校验首字节 `0x04` 与长度 65 |
| KV 写入报错 | 键名含非法字符（只允许数字/字母/下划线） | 用 `sha256(endpoint)` 十六进制做键 |
| 新订阅 1 分钟内收不到 | KV 最终一致，跨节点最长 60s | 可接受；前端提示"1 分钟后生效" |
| **manifest 被当成 `application/octet-stream`** | 平台不认识 `.webmanifest` 扩展名 | 改名为 `manifest.json`（已在 2026-10 实测确认此坑；不修则 iOS 加主屏只得到书签、收不到推送） |
| 自定义头规则"没生效" | EdgeOne 的 headers 按路径**全部匹配后合并**，同名字段**后者覆盖前者** | catch-all 的 `/*` 必须放**最前**，具体路径规则放后面（实测：`/*` 放最后时 `/icons/*` 的 `immutable` 被覆盖） |
| 推送 401/403 | JWT 的 `aud` 与 endpoint origin 不一致，或 `exp` 超过 24h | 每类 endpoint 动态取 origin；`exp` 设 12h |
| 推送堆积重复 | 未设 `Topic` 且未做冷却 | 设 `Topic: powerfee-<roomNum>` + 服务端冷却 |
| **命门 2 失败**（边缘函数出网到推送端点不通） | 平台出网限制 | 备选：① fan-out 挪到 Cloudflare Workers（同样纯 WebCrypto，订阅表可用 Workers KV，见 §14F）；② 用**新加坡 SCF** 跑 Node + npm `web-push`（代码量最少，但 KV 不可用，订阅表需另存 COS 或由边缘函数提供只读接口） |
| 免费版商业化后配额变化 | 平台策略 | 用量本身极低，影响有限；关注控制台公告 |

---

## 14. 附录

### A. `edgeone.json`（真身见仓库根目录，此处为摘要）

```json
{
  "outputDirectory": "./web",
  "headers": [
    { "source": "/*",
      "headers": [
        { "key": "Cache-Control", "value": "no-cache" },
        { "key": "X-Content-Type-Options", "value": "nosniff" },
        { "key": "Referrer-Policy", "value": "no-referrer" },
        { "key": "X-Frame-Options", "value": "DENY" }
      ] },
    { "source": "/icons/*",
      "headers": [{ "key": "Cache-Control", "value": "public, max-age=31536000, immutable" }] },
    { "source": "/sw.js",
      "headers": [{ "key": "Cache-Control", "value": "no-cache, no-store, must-revalidate" },
                  { "key": "Service-Worker-Allowed", "value": "/" }] },
    { "source": "/manifest.json",
      "headers": [{ "key": "Cache-Control", "value": "no-cache" },
                  { "key": "Content-Type", "value": "application/manifest+json; charset=utf-8" }] },
    { "source": "/api/*",
      "headers": [{ "key": "Cache-Control", "value": "no-store" }] }
  ]
}
```

> **⚠️ 顺序不能随意调换**：EdgeOne 的头部规则是**按路径全部匹配后合并**，同一个字段
> **由靠后的规则覆盖靠前的**（不是"最具体的匹配优先"）。所以 catch-all 的 `/*` **必须放在最前面**，
> 否则它会盖掉 `/icons/*` 的 `immutable` 和 `/api/*` 的 `no-store`（这一点已在 2026-10 的
> 真实部署上实测确认：`/*` 放最后时，图标响应头确实是 `no-cache` 而非 `immutable`）。

> 与规划期的三点差异：输出目录是 `web/`（不是 `dist-web`）；**刻意不用 `rewrites`**——
> 本站只服务根路径，去掉 SPA fallback 可避免 `/foo/` 这类地址下相对路径解析错位；
> **manifest 文件从 `.webmanifest` 改名为 `.json`**——平台不认识的扩展名会被当成
> `application/octet-stream` 返回（实测），浏览器会因此忽略 manifest，iOS 加到主屏后
> 只会变成普通书签、**收不到推送**。
> `schedules`（定时任务）也不使用，原因见 §13。

### B. VAPID 密钥生成

```bash
# 方式一：官方 CLI（最稳）
npx web-push generate-vapid-keys --json
# 输出：{"publicKey":"B…","privateKey":"…"}（均为 base64url）

# 方式二：Node 手写
node -e "const c=require('crypto');const e=c.createECDH('prime256v1');e.generateKeys();\
console.log('public(raw65):',e.getPublicKey().toString('base64url'));\
console.log('private(d):',e.getPrivateKey().toString('base64url'))"
```

- `VAPID_PUBLIC` = 公开密钥（65 字节裸公钥的 base64url），前端 `applicationServerKey` 用它。
- `VAPID_PRIVATE_D` = 私钥标量 `d` 的 base64url；运行时用它与公钥解出的 x/y 组装 JWK 供 `importKey`。
- 生成后**只放环境变量**，不要提交进仓库。

### C. curl 验证命令样例

```bash
# 健康检查
curl -s https://power.example.com/api/status

# 生成签名并触发一次 ingest（本机调试用）
TS=$(date +%s)
BODY="{\"roomNum\":\"1001\",\"balance\":\"12.34\",\"ts\":$TS}"
SIGN=$(printf '%s\n%s' "$TS" "$BODY" | openssl dgst -sha256 -hmac "$INGEST_SECRET" -hex | awk '{print $2}')
curl -s -X POST https://power.example.com/api/ingest \
  -H "Content-Type: application/json" -H "X-Sign: $SIGN" -d "$BODY"
```

### D. 未决问题（实施前需实测确认）

1. `edgeone.json` 的 `schedules` 最小间隔到底是 1 分钟还是 1 天（文档自相矛盾）。
   → 结论前**不要**把它当作轮询方案。
2. ~~边缘函数对 `fcm.googleapis.com` 等外部域名的出网是否有隐藏限制~~ →
   **已于 2026-10 在真实部署上验证通过**：`/api/status?probe=1` 返回
   `fcm.googleapis.com → HTTP 404`、`web.push.apple.com → HTTP 405`、
   `updates.push.services.mozilla.com → HTTP 406`，三个端点均可达、无 `ERR`。
3. SCF 当前免费额度政策（控制台确认；本场景用量极低）。
4. EdgeOne Makers 免费版商业化后的配额调整。

### E. 实施顺序建议

1. 阶段 0 命门验证 → 2. 阶段 1 域名 → 3. 阶段 5 PWA 静态页（先把"能装到桌面"跑通）→
4. 阶段 2/3 KV 与密钥 → 5. 阶段 4 边缘函数（status → subscribe → ingest → 推送）→
6. 阶段 7 清单逐条验证 → 7. 阶段 6 方案 A 接托盘程序 → 8.（可选）方案 B 接 SCF 补 7×24。

预计工作量：半天（做到桌面浏览器 + iPhone 能收到推送）；再加半天接托盘/SCF 与打磨引导页。

### F. 为什么不用纯 Cloudflare（2026-10 评估，结论：**不采用**）

Cloudflare 免费版能 1:1 替代 EdgeOne 的**除「发起大陆请求」以外**的全部能力：

| 能力 | Cloudflare 免费版 | 是否可替代 |
|---|---|---|
| 静态托管 + 自定义域 + 证书 | Pages / Workers Static Assets，绑自定义域免备案 | ✅ |
| WebCrypto（VAPID / RFC 8291） | Workers 同样支持 ECDH / HKDF / AES-GCM / ECDSA | ✅ 代码几乎原样移植 |
| 订阅与历史存储 | Workers KV：1GB、读 10 万/天、**写仅 1000/天**（5 分钟一次 × 2 键 = 576 写/天，已接近上限，需把 state 与 hist 合并成 1 键）；D1：写 10 万行/天、强一致 | ✅（注意写入预算） |
| 定时触发 | Cron Triggers，`*/5 * * * *` 免费可用 | ✅ |
| **从中国大陆发起 HTTP 请求** | 无大陆节点；Cloudflare China Network 仅 **Enterprise** 套餐 + 每个顶级域必须 **ICP 备案** | ❌ **做不到** |

「让用户浏览器自己查学校接口」这条捷径也已被实测否掉：学校 WAF 见到 `Origin` 头一律 403
（详见 README 第 2.1 节的实测表），而浏览器跨域请求必然携带 `Origin` → 前端永远读不到响应。

> **推论（本方案的硬约束）**：7×24 的自动查询必须有一台**长期开机、位于大陆境内、
> 且抓取时不带 `Origin` 头**的实体。Cloudflare 提供不了这台机器，"纯 Cloudflare" 只能把
> 数据源放到自己的 PC（计划任务上报 / cloudflared 隧道），代价是 PC 关机即无数据——
> 与 7×24 目标直接冲突。因此保留「EdgeOne（海外边缘）+ 广州 SCF（大陆抓取）」的组合。

**若日后要换**，迁移成本很低（可做成一套逻辑、两个部署目标）：Workers 适配器
`export default { fetch, scheduled }` 并把 `env.SUB_KV` 传进 `onRequest`（`resolveKv()` 已兼容
`context.env.SUB_KV`）；KV 的 `get/put/delete` 签名一致，且本函数**未使用 `list`**，
正好规避 Workers KV 返回 `{name}` 与 EdgeOne 返回 `{key}` 的差异。
另注意：`*.workers.dev` / `*.pages.dev` 在大陆访问质量差，必须绑自定义域名。
