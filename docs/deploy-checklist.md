# 部署手册（lvbyte.top）

> ## ⚠️ 已搁置 —— 仅作存档（2026-10）
>
> 本手册对应的方案**最终决定不采用**（原因见
> [pwa-push-plan.md](pwa-push-plan.md) 顶部与 README 第 6 节），
> 配套代码（`edge-functions/`、`web/`、`tools/`、`serverless/`）**已从仓库移除**。
>
> 手册**没有被执行过**：EdgeOne 项目只构建过预览、自定义域名未绑定、SCF 从未部署、
> KV 只提交过申请、环境变量未填写 —— 因此没有产生任何费用，也没有需要清理的线上资源。
>
> 保留它的价值：如果以后重新评估（例如学校放开接口、或有了常开的境内设备），
> 这份逐步清单连同费用实算（第四节）可以直接复用，不必重新调研一遍。
>
> 配套文档：[pwa-push-plan.md](pwa-push-plan.md)（原理、坑、备选方案）。

---

## 一、整体分工

| 谁 | 做什么 |
|---|---|
| **我（已完成）** | 边缘函数（查询/订阅/推送/上报/管理）、PWA 前端、SCF 代理、密钥与初始化工具、自测与本地预览 |
| **你（控制台）** | ① 推代码到 GitHub ② 建 EdgeOne Makers 项目 ③ 绑 `power.lvbyte.top` ④ 开通并绑定 KV ⑤ 填环境变量 ⑥ 建广州 SCF |
| **你（本机）** | 运行 `tools/seed-room.mjs` 初始化房间；把订阅链接在手机上打开并授权 |

代码结构（已就位）：

```
edgeone.json                     EdgeOne 项目配置（输出目录 web/、缓存与安全头）
edge-functions/api/[[default]].js 全部后端逻辑（单文件通配路由 /api/*）
web/                             PWA 静态站（index.html / app.js / sw.js / manifest / icons）
web/config.js                    由 tools/gen-secrets.mjs 生成（仅 VAPID 公钥，可提交）
serverless/scf-query/index.py    广州 SCF：抓校园接口（定时 + HTTP 两种模式）
tools/gen-secrets.mjs            生成 VAPID 密钥对与站点密钥
tools/seed-room.mjs              初始化房间、签发订阅令牌、打印分享链接
tools/set-invite.mjs             设置/轮换/关闭自助注册的邀请码
tools/selftest.mjs               76 项自测（VAPID / RFC 8291 / 策略 / 端到端 / 410 清理）
tools/mock-server.mjs            本地预览（真代码 + KV 模拟）
tools/make-icons.py              生成 PWA 图标
secrets.local.env                ★ 私钥与共享密钥，已 gitignore，绝不提交
```

---

## 二、EdgeOne Makers（约 15 分钟）

### 步骤 1 · 把代码推到 GitHub

EdgeOne Makers 从 Git 仓库构建。仓库用现有的 `Bytelv/GDPCPowerFeeTray` 即可
（`web/`、`edge-functions/`、`edgeone.json` 都在根目录，平台直接可用）。

```powershell
git add -A
git commit -m "feat(web): PWA 电量查询站 + EdgeOne 边缘函数推送"
git push
```

> ⚠️ 推送前务必确认 `git status` 里**没有** `secrets.local.env`。
> 它已被 `.gitignore` 忽略；如果曾用 `git add -f` 强加过，请立刻轮换密钥
> （`node tools/gen-secrets.mjs --force`，代价是所有订阅失效）。

### 步骤 2 · 创建项目

控制台 → EdgeOne Makers → 新建项目 → 导入 Git 仓库 → 选择该仓库，然后按下表填：

| 配置项 | 值 |
|---|---|
| 框架预设 | **Other / 无框架**（不要选 Next/Vite 等） |
| 构建命令 | **留空**（纯静态，无需构建） |
| 安装命令 | **留空** |
| 输出目录 | `web` |
| **加速区域** | **全球可用区（不含中国大陆）** ← 关键 |

> **为什么必须选「不含中国大陆」**：选含大陆的区域，自定义域名会被要求 ICP 备案。
> 我们不需要大陆节点——站点是纯静态的，抓数据由广州 SCF 负责。
> 若控制台强制要求填构建命令，填 `echo no-build` 即可。

### 步骤 3 · 绑定自定义域名

1. 先部署一次（任一次构建成功即可拿到默认项目域名）。
2. 项目 → 域名管理 → 添加自定义域名：**`power.lvbyte.top`**
   （想用别的子域也行，全文替换即可；不建议直接用裸域，CNAME 会和其他记录冲突）。
3. 按控制台给出的目标值，到 lvbyte.top 的 DNS 服务商添加 **CNAME** 记录：
   `power` → 控制台显示的目标域名。
4. 申请**免费 SSL 证书**，等待签发（通常几分钟）。
5. 验证：手机**蜂窝数据**（关 WiFi）打开 `https://power.lvbyte.top/`：
   - 期望：看到站点页面（此时会提示"需要订阅令牌"，这是正常的）
   - 若返回 **401** 或跳到 `*.edgeone.app` 预览链接 → 域名没绑对，回到第 2 步

> 免费版项目域名在大陆网络下只有 3 小时有效的预览链接，**第 3 步不做完就没法给别人用**，
> iOS 也没法"添加到主屏幕"。

### 步骤 4 · 开通并绑定 KV

1. 控制台 → 存储 → KV → **立即申请**（免费版 1GB）。
2. 创建命名空间：`powerfee-prod`。
3. 回到项目 → KV 存储 → 绑定命名空间，**运行时变量名必须填 `SUB_KV`**
   （边缘函数代码按这个全局名读取）。

### 步骤 5 · 配置环境变量

项目 → 设置 → 环境变量，从本机 `secrets.local.env` **逐条复制**（该文件不要外发）：

| 变量名 | 值来源 | 说明 |
|---|---|---|
| `VAPID_PUBLIC` | secrets.local.env | 推送公钥（与 `web/config.js` 里的必须一致） |
| `VAPID_PRIVATE_D` | secrets.local.env | ★ 推送私钥，泄漏等于别人能冒充你发推送 |
| `VAPID_SUBJECT` | `mailto:lvbyte@lvbyte.top` | 推送规范要求的联系方式 |
| `INGEST_SECRET` | secrets.local.env | ★ 上报/管理接口的签名密钥，需与 SCF 侧一致 |
| `DEFAULT_THRESHOLD` | `20` | 全局默认阈值（可被房间配置覆盖） |
| `DEFAULT_WARN_RATIO` | `2` | 预警倍数（低于 20×2=40 度转黄） |
| `DEFAULT_COOLDOWN_MINUTES` | `180` | 同一档位重复提醒间隔 |
| `FETCH_PROXY_URL` | SCF 的 HTTP 触发地址 | 第 6 步做完再回来填 |
| `NOTIFY_RECOVERY` | `1` | 充值恢复后也推一条；填 `0` 关闭 |
| `NOTIFY_WARN` | `1` | 进入预警区（低于阈值×2）也推一条；填 `0` 则与托盘程序一致——预警只记状态、不打扰 |
| `BARK_URL` / `SERVERCHAN_KEY` / `WEBHOOK_URL` | 可选 | 兜底通道：Web Push 一条都发不出去时使用（国产 Android 无 GMS 的出路） |

改完环境变量需**重新部署**一次才生效。

### 步骤 6 · 验证服务端

```powershell
# 基础健康检查
curl.exe -s https://power.lvbyte.top/api/status

# 命门验证：海外边缘节点能否连通三大推送端点（三个都必须是 HTTP xxx，不能是 ERR）
curl.exe -s "https://power.lvbyte.top/api/status?probe=1"
```

期望 `capabilities` 里 `kv / vapid / ingestSign` 全为 `true`。
`probe` 里出现 `ERR:` 说明该端点不可达 —— 参考方案文档 §13 的备选方案。

---

## 三、初始化房间并分享（支持多个宿舍）

后端按房间号分键存储（`cfg_ / tok_ / state_ / hist_ / subs_ / sub_`），**多宿舍不需要改任何代码**：
每个房间一条独立令牌链接，阈值、冷却、历史、订阅设备完全隔离（已由自测第 [9] 段覆盖，21 项断言）。

### 3.1 取 roomNum

`roomNum` 是学校系统的内部编号：打开托盘程序的「设置…」，选中房间后下方会以绿色文字显示
（例如 `1001`）。同一个下拉列表可以查出每个宿舍的编号。

### 3.2 逐个初始化

```powershell
# 本宿舍
node tools/seed-room.mjs --url https://power.lvbyte.top --room 1001 `
     --name "1号楼 1A101" --threshold 20 --warn 2 --cooldown 180

# 隔壁宿舍（阈值可以不同：他们用电快就调高）
node tools/seed-room.mjs --url https://power.lvbyte.top --room 1002 `
     --name "1号楼 1A102" --threshold 30 --warn 2 --cooldown 180
```

每次都打印该房间专属的订阅链接：

```
订阅链接（发给该宿舍的人，请勿公开）：
  https://power.lvbyte.top/#t=3f2a…（32 位十六进制）
```

### 3.3 把链接发给对应宿舍

- **一个宿舍一条链接**，彼此独立：A 宿舍的链接看不到 B 宿舍的余额，也收不到 B 的推送。
- 链接里的 `#t=` 就是该房间的唯一凭证：拿到它就能看该房间余额、并把**自己的设备**加进该房间的
  通知列表。所以别发群里、别截图外发。
- 怀疑泄漏就轮换令牌（旧链接立即失效，需重新分发）：
  ```powershell
  node tools/seed-room.mjs --url https://power.lvbyte.top --room 1001 --rotate
  ```
- **推荐改用"自助注册"**（见 3.5）：同学自己从下拉列表挑房间，你不用手动 seed 每一间。
  手动 seed 仍然有用——需要在登记前就把阈值/名称定好时用它。

### 3.4 自助注册（推荐，省掉逐个 seed）

```powershell
# 生成邀请码（并打印可直接发群的加入链接）
node tools/set-invite.mjs --url https://power.lvbyte.top
# 输出形如：
#   邀请码： 3f2a1c…（16 位十六进制）
#   加入链接： https://power.lvbyte.top/#code=3f2a1c…
```

把**加入链接**发到宿舍群即可。同学点开后的流程是：

1. 页面自动带入邀请码 → 自动拉取房间列表（校区 → 楼栋 → 房间 三级下拉，房间名就是学校系统里的房号）
2. 选中自己宿舍 → 点「获取我的订阅链接」→ 拿到本房间专属令牌
3. 点「开启通知」授权 → 完成；页面随后会显示"分享给同宿舍的人"的链接

工作原理与安全边界：

| 项 | 说明 |
|---|---|
| 房间列表来源 | 站点经广州 SCF 抓一次学校接口，缓存进 KV，**7 天**内不再重复拉取 |
| 令牌粒度 | **每次登记都签发独立令牌**（同宿舍多人/多机各自一条，可单独轮换或吊销，互不影响） |
| 数据隔离 | 令牌在服务端反解出房间号，同学只能看到自己房间的数据 |
| 邀请码能做什么 | 只决定"能不能自助登记房间"。拿到邀请码的人**可以**登记别人的房间（从而看到那间房的余额）——这是自助模式的固有代价，靠"链接只发宿舍群"来控制 |
| 防暴力破解 | 邀请码默认 16 位十六进制（64 bit）；同一 IP 连续猜错 20 次后限流 5 分钟 |
| 关闭 / 轮换 | `node tools/set-invite.mjs --url … --disable` 或重新执行生成新码；**已登记的同学不受影响**（他们手里是房间令牌） |
| 想改某房间阈值 | `node tools/seed-room.mjs --url … --room 1002 --threshold 30`（同房间重复执行是幂等的，会保留新旧令牌） |

### 3.5 别忘了让 SCF 一起上报

站点持有房间注册表，**SCF 每次定时会主动来问"该监控哪些房间"**（`/api/admin-rooms`），
所以自助注册的房间会被**自动**纳入监控，不需要改任何环境变量。

`ROOM_NUMS` 现在只是可选的兜底/强制项（见下一节）。

---

## 四、国内侧数据源（三选一，必选其一）

> **为什么必须有"国内侧"**：学校服务器只在大陆网络可达（境外 DNS 与 IP 双层不通），
> 而站点必须在海外边缘节点才能发推送；并且学校 WAF 见到 `Origin` 头一律 403，
> 所以**浏览器无法直连**学校接口，必须有一个"不带 Origin 头"的服务端/脚本代查。

| 方案 | 成本 | 7×24 | 说明 |
|---|---|---|---|
| **4A · PC 轮询脚本** | **0 元** | ❌ PC 开机才有数据 | 不改托盘程序、不动现有代码，一个 Node 脚本 + 一条计划任务 |
| **4B · 广州 SCF** | **≈¥3/年** | ✅ | 只建**定时触发器**，不要建 API 网关触发器 |
| **4C · 境内常开设备** | **0 元** | ✅（设备开着） | 路由器 / 树莓派 / 闲置旧安卓手机 / NAS，见 `serverless/pc-poller/README.md` |

> **建议顺序**：先用 4A 把整条链路跑通（0 元、当天可验证，用的还是托盘程序已验证过的网络路径），
> 再决定要不要为"PC 关机时的覆盖"花那几块钱。
>
> ⚠️ **待验证的风险**：4B/4C 走的是机房或设备 IP，而学校 WAF 是否接受腾讯云机房 IP 段**尚未验证**
> （已知只有住宅/校园网络能正常查）。所以第一次部署 SCF 后，先手动触发一次看日志是否报 403/超时；
> 若被拦，就用 4A/4C。

### 4A · PC 轮询脚本（0 元，推荐先做）

```powershell
# 1) 先手工跑一轮，确认能查通（会真的抓一次学校接口）
$env:EDGE_URL='https://power.lvbyte.top'
node serverless\pc-poller\poll.mjs
Remove-Item Env:\EDGE_URL

# 2) 注册成每 5 分钟自动跑的计划任务（不需要管理员权限）
schtasks /Create /TN "PowerFeePoll" /SC MINUTE /MO 5 /F /TR "\"C:\Program Files\nodejs\node.exe\" \"I:\GDPCPowerFeeTray\serverless\pc-poller\poll.mjs\""

# 查看 / 删除
schtasks /Query /TN "PowerFeePoll" /V /FO LIST
schtasks /Delete /TN "PowerFeePoll" /F
```

脚本每轮做四件事：问站点要监控清单 → 抓一次学校接口（一次返回全校，N 个房间只抓一次）
→ 站点说需要时把房间索引推上去（**自助注册就靠这个，因此不需要 API 网关**）→ 逐房间上报读数。
日志写在 `.local/poll.log`。

### 4B · 广州 SCF（≈¥3/年，要 7×24 时用）

1. 控制台 → 云函数 SCF → 新建函数：
   - **地域：广州**（必须！上海/北京也可；**绝不要**香港/新加坡，境外不可达）
   - 运行环境：**Python 3.10**
   - 函数代码：把 `serverless/scf-query/index.py` 全文粘贴进去
   - 超时时间：10 秒；内存 128MB
2. 环境变量：

| 变量名 | 值 |
|---|---|
| `EDGE_URL` | `https://power.lvbyte.top` |
| `INGEST_SECRET` | 与 EdgeOne 里**完全一致** |
| `ROOM_NUMS` | 可选。强制额外监控的房间（逗号分隔）。**通常留空**：站点会把已注册房间自动告诉它 |
| `ROOM_NUM` | 可选（兼容旧写法） |
| `HTTP_ALLOW_UNSIGNED` | `0`（保持默认） |

3. 新建**定时触发器**：触发管理 → 创建触发器 → 定时触发 → 每 5 分钟
   （用控制台的图形化选择，不要手写 cron 表达式）。
4. **不需要**建 HTTP 触发器 / API 网关：房间索引改由定时任务顺手推给站点，
   「立即查询」按钮会自动退化为"刷新数据"（读站点里的最新值）。
5. 验证：控制台「测试」按钮跑一次，日志应出现：
   `[powerfee] 站点返回 N 个已注册房间，needRoomIndex=…` → `抓到全校 2077 个房间`
   → `房间 xxx 上报成功：… 度 → level=…`。
   若显示 403/超时，说明机房 IP 被学校拦了 → 改用 4A 或 4C。

**计费真相（已核实，2026-10）**：腾讯云 SCF 免费额度自 2022-06-01 调整，
**新用户只有前三个月免费，第四个月起不再免额**
（[计费概述](https://cloud.tencent.cn/document/product/583/17299)）。按官方价目
（资源 ¥0.00011108/GBs、调用 ¥0.0133/万次）实算：

| 项 | 用量 | 费用 |
|---|---|---|
| 调用次数 | 8640 次/月（每 5 分钟 1 次） | ¥0.011/月 |
| 资源使用 | 8640 × 0.125 GB × ~1.5 s ≈ 1620 GBs | ¥0.18/月 |
| 外网出流量 | 约 26 MB/月 | ≈¥0.02/月 |
| **合计** | | **≈¥0.21/月 ≈ ¥2.5/年** |

按量计费需要账户有余额（充 ¥10 可用三四年）。若不想产生任何账单，选 4A/4C。

### 4C · 境内常开设备（0 元 + 7×24）

路由器、树莓派、闲置旧安卓手机（Termux）、NAS 只要装了 Node 就能跑同一个脚本，
把 `EDGE_URL` 与 `INGEST_SECRET` 写进该设备上的 `secrets.local.env`（或环境变量）后
用 `--loop` 常驻，或交给系统 cron。细节见 `serverless/pc-poller/README.md`。

---

## 五、端到端验收清单

| # | 操作 | 期望 | ✓ |
|---|---|---|---|
| 1 | 手机蜂窝数据打开站点 | 正常显示页面（非 401、非预览链接） | ☐ |
| 2 | `curl .../api/status?probe=1` | 三个推送端点均返回 HTTP 状态码 | ☐ |
| 3 | 点「立即查询」 | 余额更新，来源显示"手动查询" | ☐ |
| 4 | 桌面 Chrome 打开订阅链接 → 「开启通知」 | 授权成功，页面显示"已开启通知（共 1 台设备）" | ☐ |
| 5 | 用本机脚本伪造一次低电量上报（见下） | 桌面弹出系统通知，标题含"电量不足" | ☐ |
| 6 | 立刻再触发一次 | **不重复弹**（冷却生效） | ☐ |
| 7 | iPhone：Safari 打开链接 → 分享 → 添加到主屏幕 → **从图标启动** → 开启通知 | 系统弹权限框，授权后能收到推送 | ☐ |
| 8 | 关掉 PC，等 5 分钟后看网页 | 余额仍在更新（SCF 定时上报生效）→ 7×24 达成 | ☐ |
| 9 | 在手机上关闭通知后触发一次 | 服务端 `cleaned` 计数增加，无报错 | ☐ |
| 10 | 微信内打开链接 | 显示"请在浏览器中打开"引导，不报错 | ☐ |

伪造一次低电量上报（本机，用真实密钥签名）：

```powershell
node -e "
const fs=require('fs'),c=require('crypto');
const env=Object.fromEntries(fs.readFileSync('secrets.local.env','utf8').split(/\r?\n/).filter(l=>l&&!l.startsWith('#')).map(l=>{const i=l.indexOf('=');return [l.slice(0,i),l.slice(i+1)];}));
const ts=Math.floor(Date.now()/1000);
const body=JSON.stringify({roomNum:'1001',balance:'3.5',ts,source:'manual-test'});
const sign=c.createHmac('sha256',env.INGEST_SECRET).update(ts+'\n'+body).digest('hex');
fetch('https://power.lvbyte.top/api/ingest',{method:'POST',headers:{'content-type':'application/json','x-sign':sign},body}).then(r=>r.json()).then(console.log);
"
```

---

## 六、排错速查

| 现象 | 原因 | 处理 |
|---|---|---|
| 打开域名 401 / 跳预览链接 | 免费版项目域名在大陆的 3 小时限制 | 完成「步骤 3 绑定自定义域名」 |
| `/api/status` 显示 `kv: false` | 没绑 KV 或变量名不是 `SUB_KV` | 步骤 4 |
| 开启通知报"站点未配置 VAPID 公钥" | `web/config.js` 缺失或没跟着部署 | 本地跑 `node tools/gen-secrets.mjs` 后重新部署 |
| 开启通知成功但收不到 | ① iPhone 没从主屏图标启动 ② Android 无 GMS ③ 权限被系统收回 | 见站点内「收不到通知？看这里」；Android 建议配 `BARK_URL`/`SERVERCHAN_KEY` 兜底 |
| 推送 401/403（日志里） | VAPID 的 `aud` 与 endpoint 不匹配 / `exp` 超 24h | 代码已按 endpoint origin 动态生成；若仍报错，检查控制台时间与密钥是否配套（公私钥必须同批生成） |
| 「立即查询」提示未配置代理 | `FETCH_PROXY_URL` 没填或没重新部署 | 完成第四步并重新部署 |
| 网页数据一直是旧的 | SCF 定时触发器没建，或函数执行失败 | 到 SCF 控制台看调用日志（本次范围内唯一的数据源就是它） |
| KV 写入报错"键名只能包含数字、字母、下划线" | 键名构造有问题 | 代码已用 `sha256(endpoint)` 前 32 位做键；若你改过代码，注意这条限制 |
| iOS 加了主屏但没通知 | manifest 的 `display` 不是 `standalone` | 本项目已设为 `standalone`，若改过请改回 |

---

## 七、本次范围与后续可选项

**已确定的范围**

- 数据源**只用广州 SCF**：托盘程序 `src/PowerFeeTray.cs` 本次**不做任何改动**。
- 房间初始化由你**在本机执行** `tools/seed-room.mjs`：真实 `roomNum` 与订阅令牌
  不经过任何对话、日志或提交记录。

**后续可选（需要时再说）**

- 给托盘程序加上报，作为 SCF 之外的第二条通道（约十几行，仍是 C# 5 + 无第三方库）。
- 兜底通道：加 `BARK_URL` / `SERVERCHAN_KEY` / `WEBHOOK_URL`，覆盖国产 Android 无 GMS 的情况。
- 多房间：重复执行 `seed-room.mjs` 换一个 `roomNum` 即可——后端本来就按房间号分键
  （`cfg_ / state_ / hist_ / subs_ / tok_`），每间一个令牌，互不干扰。
- 自定义 404 页面（当前非根路径会返回平台默认 404）。
