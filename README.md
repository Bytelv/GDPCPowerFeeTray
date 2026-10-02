# 宿舍电费提示 (PowerFeeTray)

广东警官学院 校园智能控电系统 —— **电量查询 + 低电量弹窗提醒** 的 Windows 托盘小程序。

单文件 exe，**54 KB**，不依赖任何第三方库，不需要安装 .NET SDK 或运行时
（用 Windows 自带的 .NET Framework 4.8 编译器构建，Win10/11 默认自带）。

---

## 1. 快速开始

```powershell
# 构建 (会在 dist\ 下生成 PowerFeeTray.exe)
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

# 构建并立即做一次接口自检
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Clean -Run

# 直接运行 (常驻托盘)
dist\PowerFeeTray.exe
```

启动后右下角托盘区出现一个绿色圆形「电」图标，双击即可立即查询。

**首次运行**：程序不含任何预置房间（避免把某个人的宿舍信息编译进 exe）。
首次启动会提示你打开设置窗口，从下拉列表里依次选择**校区 → 楼栋 → 房间号**即可，
不需要知道任何内部编号（选中后会自动识别并显示）。

### 命令行参数

| 参数 | 作用 |
|---|---|
| *(无)* | 常驻托盘，按间隔自动查询，低电量时弹窗 |
| `--check` | 无界面查询一次，结果写入 `check.txt`（便于脚本化验证） |
| `--selftest` | 查询一次并用弹窗显示结果 |
| `--test-alert` | 用示例数据预览低电量弹窗样式 |
| `--dump-ui` | 把弹窗渲染成位图并导出控件布局（无人值守下验证 UI） |
| `--dump-settings` | 无界面验证设置窗口：加载房间列表、切换各级下拉框、保存往返测试 |
| `--uninstall-autostart` | 取消开机自启 |

---

## 2. 接口逆向结果（核心发现）

原始页面：
`https://yktxyk.gdppla.edu.cn/user/powerfee/index?from=wxminiprogram&token=<TOKEN>`

页面内 `powerfee` 相关请求共两类：

### 2.1 查询房间余额 —— **免登录，本程序使用的就是它**

```
POST /user/powerfee/getRoomInfo?from=wxminiprogram&implType=CGCOMMON0001&buyMark=
```

> 页面上原始请求还会带一个 `token=<TOKEN>` 参数（学校页面把节点令牌直接渲染进了
> 每个链接）。**经实测，该参数被服务端完全忽略，因此本程序不携带它**——下面的
> 关键特性有完整证据。这不是为了"省一个参数"，而是因为令牌本来就没有鉴权作用。

**关键特性（已实测验证）：**

- **不需要登录态**：不带任何 Cookie、不带 JSESSIONID 也能正常返回。
- **`token` 参数被完全忽略**：不带 token、token 为空、乱填 `BADTOKEN`、截断成 `node0111`、
  把节点号换成 `node0112` —— 返回结果**完全一致**，都是 2077 个房间。
  也就是说 token 与能否查询**毫无关系**，学校若轮换它也不影响本程序。
- **本程序已彻底移除 token**：源码、编译后的 exe、运行时配置里都没有任何令牌值
  （已用 ASCII 与 UTF-16 双重检索确认 exe 内不含该字符串）。
- **真正必需的参数是 `implType`**：缺失或填错（如 `BADVALUE`）会返回 `{"ret":false,"msg":"查询失败"}`。
- **一次拿到全校数据**：单个请求返回全部 **2077** 个房间的当前余额。
- **请求头里出现 `Origin` 一律 403**（2026-10 实测，对 PWA 方案是决定性约束）：

  | 请求头组合 | 结果 |
  |---|---|
  | 基线（不加任何额外头） | **200**，940 KB |
  | 仅加 `X-Requested-With: XMLHttpRequest` | 200 |
  | 仅加浏览器 `User-Agent` | 200 |
  | 仅加 `Origin: https://任意域` | **403** |
  | `X-Requested-With` + `Origin` | **403** |

  响应里也**没有任何** `Access-Control-Allow-Origin` 头。

  **推论：浏览器无法跨域直连该接口。** 浏览器发起跨域请求时必然携带 `Origin`，
  服务端 WAF 会直接 403（连 CORS 协商的机会都没有）。因此任何网页前端
  （PWA / 静态站 / 小程序 WebView）都**必须**经由一个"不带 `Origin` 头"的服务端代理
  来查询——这也正是托盘程序一直正常工作的原因：它只发
  `User-Agent` + `X-Requested-With`，从不发 `Origin`（见 `src/PowerFeeTray.cs` 第 528–531 行）。
- 因此服务端轮询极其廉价：每 30 分钟 1 个请求即可。

响应结构（**以下字段值均为虚构示例，不代表任何真实房间**）：

```json
{
  "ret": true,
  "msg": "查询成功",
  "obj": [
    {
      "roomNum": "1001",            // 系统内部唯一编号，最可靠的匹配键
      "schoolArea": "示例校区",
      "schoolAreaNo": "0",
      "building": "学生宿舍1号楼",
      "buildingNo": "1000",
      "room": "1A101",              // 人类可读房间号（楼号+AB侧+房号）
      "powerBalance": "50.00",      // 剩余电量（字符串，可为负数）
      "formatPowerBalanceStr": "50.00度",
      "waterBalance": null,
      "formatWaterBalanceStr": null,
      "leftMoney": null,
      "leftMoneyStr": null,
      "du": "度"
    }
  ]
}
```

### 2.2 单房间精确查询 —— **需要登录，本程序未使用**

```
POST /user/powerfee/getBalance
data: implType=CGCOMMON0001&roomNum=<roomNum>&token=<TOKEN>&from=wxminiprogram
```

未登录时返回 `{"ret":false,"msg":null}`；对应的
`POST /user/user/account/getUserSno` 会返回 `{"code":203,"msg":"登录失效"}`。
由于 2.1 已包含余额字段，本程序无需登录态即可工作。

### 2.3 其他相关端点

| 端点 | 说明 |
|---|---|
| `/user/powerfee/getRoomInfo` | ✅ 房间列表 + 余额（免登录） |
| `/user/powerfee/showDailyDetails` | 是否开放用电/用水明细 |
| `/user/powerfee/toDailyDetails` | 明细页面（本程序用它做「打开用电明细」） |
| `/user/powerfee/recharge` | 充值下单（需登录） |
| `/user/powerfee/bindRoomInfo` | 绑定房间（需登录） |

### 2.4 为什么不能用 GitHub Actions / Cloudflare Workers 定时

实测结论：**学校服务器完全不对外开放**，境外主机在 DNS 和 IP 两层都不可达。

| 测试项 | 境外节点结果 | 对照组 |
|---|---|---|
| HTTP 访问 | 10 个节点全部失败 | example.com ✅ baidu.com ✅ |
| DNS 解析 | 8 个节点全部返回空 A/AAAA | — |
| 直连 `210.39.98.60:443` | 8 个节点全部 Connection timed out | 百度国内 IP ✅ 0.25s |

- 权威 NS 是自建的 `dns.gdppla.edu.cn`（210.39.98.143/144），**只对国内解析器作答**。
- 服务器 IP 属 **CERNET 中国教育网**广东节点，境外直连超时。

所以定时任务**必须跑在中国大陆网络内**。GitHub 托管 runner 全在境外、Cloudflare
免费版 Workers 也没有中国大陆节点（China Network 需企业版 + ICP 备案），两者都无法工作。
若改用云函数，**地域也必须选国内（上海/广州/北京）**，选香港或新加坡会以相同方式失败。

---

## 3. 配置

配置文件位置：`%APPDATA%\PowerFeeTray\config.ini`（托盘菜单「打开数据文件夹」可直达）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `campus` / `building` / `room` | 空（首次运行需自行选择） | 要监控的房间 |
| `roomNum` | 空（选中房间后自动识别） | 系统内部编号，无需手动填写 |
| `threshold` | 20 | 剩余电量低于此值（度）时弹窗告警；可在设置里填任意值 |
| `intervalMinutes` | 30 | 查询间隔（分钟） |
| `cooldownMinutes` | 180 | 同一次低电量告警的重复提醒间隔，避免反复弹窗 |
| `popup` | true | 自绘弹窗提醒（推荐，仅一个「知道了」按钮） |
| `balloon` | **false** | 系统托盘通知；关闭弹窗时才生效，好处是会留在通知中心可回查 |
| `sound` | true | 告警音 |
| `autostart` | true | 开机自动启动（写 HKCU Run 键） |
| `warnRatio` | 2.0 | 低于 `threshold × warnRatio` 时托盘图标转黄（预警） |

> **房间信息刻意不预置任何默认值。** 这些默认值会被编译进分发的 exe，
> 预置等于把某个人住哪间宿舍公开出去。首次运行程序会主动引导你从下拉列表中选择。

> **`popup` 与 `balloon` 是互斥的，不会同时触发。**
> 两者都锚定屏幕右下角，同时开启会互相遮挡（早期版本确实会重叠，现已修正）。
> 程序在读取配置时若发现两者同时为 `true`，会自动关闭 `balloon` 并写入日志。

修改配置后需**重启程序**；也可用托盘菜单「设置...」图形化修改（改完自动重载并立即查询）。

### 图形化设置：全部靠下拉选择，无需手输

打开「设置...」后会自动从服务器拉取房间列表（约 0.8 秒，共 2077 个房间），
所有输入项都是**下拉选择**，不需要知道任何内部编号：

| 字段 | 类型 | 选项来源 |
|---|---|---|
| 校区 | 下拉（只读） | 接口返回的全部校区（示例为 2 个） |
| 楼栋 | 下拉（只读） | 随校区级联刷新（示例：甲校区 11 个 / 乙校区 8 个） |
| 房间号 | 下拉（可键入搜索） | 随楼栋级联刷新，1～341 个；显示时附带当前余额 |
| 内部编号 | **只读文字，不可编辑** | 选中房间后自动识别并显示 |
| 提醒阈值 | 下拉 + **可自定义填写** | 预设 5/10/15/20/30/50/80/100 度，也可直接键入任意值（0.1～9999） |
| 查询间隔 | 下拉（只读） | 10 / 15 / 30 / 60 / 120 / 180 分钟 |
| 重复提醒 | 下拉（只读） | 60 / 120 / 180 / 360 / 720 分钟 |
| **提醒方式** | 下拉（只读，三选一） | 右下角弹窗 / 系统托盘通知 / 不弹出 |
| 声音提醒、开机自动启动 | 复选框 | — |

说明：

- 房间号下拉框**可以直接键入**（如输入 `1A1` 会自动补全为 `1A101`），
  用来在几百个房间里快速定位；其余下拉框为纯选择，杜绝无效值。
- **提醒阈值支持自定义**：既能从预设里挑，也能直接键入任意数值（如 `37.5`）。
  保存时会校验范围 0.1～9999，填了非数字或超范围会明确提示并**拒绝保存**，
  配置里的旧值不受影响。
  查询间隔与重复提醒仍为固定预设——避免手滑填成 1 分钟，对学校服务器造成不必要的压力。
- **内部编号已从输入项中彻底移除**。原先需要用户自己填 `1001` 这种系统内部编号，
  正常用户无从得知；现在它由选中的房间自动推导，并在下方以绿色文字显示供确认。
- 保存时房间相关字段（含内部编号、`schoolAreaNo`、`buildingNo`）**全部由选中的房间推导**，
  因此不可能出现「改了房间号但编号还是旧的」导致监控错房间的情况。
- 房间列表加载失败（如断网）时「保存」按钮会保持禁用并提示重试，避免误存空值。
- **提醒方式三选一**（`popup` / `balloon` 互斥）：
  - *右下角弹窗*（默认）：自绘窗口，置顶 90 秒后自动关闭，不抢焦点；
    **不受 Windows「专注助手 / 通知设置」影响**，不会被静音。窗口内只有「知道了」，
    不再提供跳转充值的按钮——充值请直接在微信小程序里操作。
  - *系统托盘通知*：走 Windows 原生通知，会留在「通知中心」方便事后回查，
    但可能被专注助手或系统通知设置拦掉。
  - *不弹出*：只改托盘图标颜色并可选响铃。

### 托盘图标颜色

| 颜色 | 含义 |
|---|---|
| 🟢 绿 | 电量充足 |
| 🟡 黄 | 进入预警区（低于阈值 2 倍） |
| 🔴 红 | 低于阈值，已触发告警 |
| ⚪ 灰 | 查询失败（网络或接口异常，5 分钟后自动重试） |

---

## 4. 数据文件（均在 `%APPDATA%\PowerFeeTray\`）

| 文件 | 说明 |
|---|---|
| `config.ini` | 配置 |
| `powerfee.log` | 运行日志（超过 512 KB 自动裁剪） |
| `history.csv` | 每次查询的余额历史，用于估算「还能用几天」 |
| `check.txt` | `--check` 的输出 |

程序会根据最近 72 小时的余额变化，自动估算**日均用量**和**预计可用天数**，
显示在托盘提示和告警弹窗里。

---

## 5. 项目结构

```
GDPCPowerFeeTray/
├─ build.ps1                 构建脚本（纯 ASCII，避免 PS5.1 编码问题）
├─ src/PowerFeeTray.cs       全部源码（C# 5）
├─ src/app.ico               构建时自动生成的图标
├─ docs/                     已搁置方案的调研存档（见第 6 节）
├─ README.md
├─ RELEASE_NOTES.md
└─ .gitignore
```

> **`dist/` 与 `recon/` 都不入库。** 构建产物 `PowerFeeTray.exe` 只作为
> GitHub Release 的附件分发；逆向侦察留档（学校页面副本、运行截图）保留在本地，
> 因为截图与页面副本里会带有真实房间号。

---

## 6. 已搁置：PWA 推送方案（仅存档）

2026-10 曾做过一套手机端方案（EdgeOne 边缘函数 + KV + 自实现 VAPID / RFC 8291 推送，
含多宿舍共享与自助注册）。**代码与 138 项自测全部完成并验证通过，但最终决定不采用**，
相关代码已从仓库移除，只留下调研与实测存档：

| 文档 | 内容 |
|---|---|
| [docs/pwa-push-plan.md](docs/pwa-push-plan.md) | 方案设计、EdgeOne/Cloudflare 能力对照、iOS 主屏推送限制、KV 键设计、风险与坑清单 |
| [docs/deploy-checklist.md](docs/deploy-checklist.md) | 逐步部署手册、国内数据源三选一与费用实算 |

**为什么搁置**（供以后重新评估时参考）：

1. **国内侧数据源是硬约束**：学校服务器只在大陆网络可达（境外 DNS/IP 双层不通），
   而推送分发必须在海外边缘节点。于是要么依赖一台境内设备常开，要么用国内云函数
   （SCF 免费额度仅前三个月，实算约 ¥2.5/年）或国内 VPS。
2. **请求模式更显眼**：网页要想"及时"，轮询就得比托盘程序密。5 分钟一次 = 每天 288 次、
   每月约 8 GB 流量，是托盘程序默认间隔的 6 倍。万一学校 WAF 因此收紧接口，
   代价会落到全校同学头上——**这是决定搁置的主要原因。**
3. **收益与复杂度不划算**：托盘程序已覆盖本机提醒的核心需求，而站点要额外背上
   域名、备案判断、云函数计费、推送订阅维护这一整套。

> 托盘程序**从未因此改动过**：`src/` 与 `build.ps1` 一行未动；
> 那套站点的代码也**不在本仓库的历史里**（相关提交从未推送，已就地丢弃）。

---

## 7. 已知限制与注意事项

1. **PC 必须开机运行**。关机/休眠期间不会检测；唤醒后若发现已过检查点会立即补查。
   这是本方案唯一的真实短板。曾评估用"手机 PWA 推送 + 国内云函数"补上 7×24，
   **结论是不采用**，调研与费用实算存档见
   [docs/pwa-push-plan.md](docs/pwa-push-plan.md)（搁置原因见第 6 节）。
2. **程序已不含任何令牌**。`getRoomInfo` 不校验 token（详见 2.1 节实测证据），
   所以源码、exe、`config.ini` 里都移除了它，配置里也不再出现 `token` 这一行。
   唯一的代价是「用电明细」的浏览器链接不再与小程序 URL 完全一致——
   这两个页面不带 token 依然 HTTP 200 正常渲染（已实测）。
   需说明的是：**页面内的微信支付流程无法在无头环境下验证**（它依赖微信 JSAPI，
   只在微信内置浏览器里生效），所以实际充值请仍走小程序；若日后发现这两个链接
   有异常，那是唯一需要回头加回参数的地方。
3. **移动 exe 后无需手动修注册表**：程序每次启动都会用自身路径重写开机自启项，会自动修正。
4. **弹窗 90 秒后自动关闭**，不抢焦点（不打断打字/游戏）。
   `popup` 与 `balloon` 互斥，不会同时出现两个提醒（详见第 3 节的提醒方式说明）。
5. **源码必须保持可被 `/codepage:65001` 正确读取**。构建脚本已显式指定 UTF-8，
   且**不再改写源文件**（改写过会破坏编辑器和版本控制的预期）。
6. 内存占用：私有内存约 30 MB（.NET Framework WinForms 的正常水平）。
   exe 本体只有 54 KB，因为不需要打包运行时。

---

## 8. 开发备忘

- 编译器是 Windows 自带的 `csc.exe`（.NET Framework 4.8），**只支持 C# 5**：
  不能用字符串插值 `$""`、`?.`、`nameof`、表达式体成员、自动属性初始化器。
- `JavaScriptSerializer` 反序列化 JSON 数组时可能给出 `ArrayList` 而非 `object[]`，
  不能硬转；源码里用 `AsList()` 统一处理（这个坑曾导致「房间数据格式异常」）。
- 跨线程回调**不要用 `SynchronizationContext.Current`**：构造函数执行时
  `Application.Run` 尚未安装 WinForms 同步上下文，拿到的是 `null`，会导致
  `NullReferenceException` 并直接终止进程（这个坑已踩过）。现用一个永不显示的
  `Form` 作 `BeginInvoke` 宿主。
- 构建脚本刻意保持 **ASCII-only**：Windows PowerShell 5.1 会把无 BOM 的 `.ps1`
  按 ANSI 代码页解析，中文字符串会损坏并引发语法错误。
- **WinForms 的一个硬限制**：`ComboBoxStyle.DropDownList` 搭配
  `AutoCompleteSource.ListItems` 时，`AutoCompleteMode` 只允许 `None`，
  否则构造时直接抛 `NotSupportedException`。所以「纯选择」和「可键入搜索」
  必须用不同的 `DropDownStyle` 实现（见 `AddCombo`）。
- **TLS 必须在 `Main` 最开头统一设置**。`ServicePointManager.SecurityProtocol`
  是按进程生效的静态属性；只在部分入口设置会导致别的入口报
  「未能创建 SSL/TLS 安全通道」（这个坑在 `--dump-settings` 上踩过一次）。
- 用 `&` 在 PowerShell 里调用 GUI 子系统的 exe 时**不会等待其退出**，
  验证脚本应改用 `Start-Process -Wait`。
- **`NotifyIcon.Text` 在 .NET Framework 上的上限是 63 个字符**，写成 120 也不会
  被静默截断，而是 setter 直接抛 `ArgumentException`（.NET 6 才放宽到 127 并改为
  静默截断）。致命之处在于异常发生在 UI 回调里：它会被 `Post` 的 try/catch 吞掉，
  于是 `HandleResult` 后半段的低电量提醒整轮丢失，日志里只留一行难查的异常。
  所以托盘文本必须先用 `TrayContext.Trim()` 裁到 63 以内再赋值。
- **`history.csv` 按房间隔离**：首行是 `# room=<编号>`。换绑房间时旧文件会被
  归档成 `history-1.csv`（序号递增，上限 100 个）再从零累计，否则旧房间的余额落差
  会被当成新房间的消耗，算出「约可用 0.x 天」这种离谱结论并写进告警弹窗。
  老版本的两列格式（无表头）视为当前房间的数据，读取时会自动补上表头。
- **`powerBalance` 解析失败绝不能按 0 继续用**。学校接口一旦改字段格式，
  0 会让所有房间都低于阈值：托盘全红，并弹出「剩余 50.00度 / 已低于提醒阈值」
  这种自相矛盾的告警。现在解析不出余额的房间直接跳过，一个都没有时整轮报失败。
- **批量改写文件时别用 `Set-Content -Encoding utf8`**：Windows PowerShell 5.1 会写入
  UTF-8 BOM（曾把一个前端文件写成带 BOM 的）。要么用
  `[IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($false)))`，
  要么直接用编辑器改。
- **这个 shell 还会吞掉传给原生命令的双引号**：`node -e "…"` 或 `git commit -m "…"`
  里含 ASCII 双引号时会被拆坏，分别表现为 `SyntaxError: Unexpected end of input`
  和 `error: pathspec '…' did not match any file(s) known to git`。
  需要多行或含引号的内容时，先写成文件再引用（例如 `git commit -F <文件>`）。
- **两个通知机制不能同时开**。自绘弹窗与托盘气泡（Win10/11 会转成系统 toast）
  都锚定屏幕右下角，同时触发必然重叠——这是设计问题，不是坐标微调能解决的
  （toast 的堆叠高度随通知条数变化，固定偏移仍会撞）。
  正确做法是让它们互斥：`NotifyLow` 里用 `shown` 标志确保只走一条分支。
  另外注意 Windows toast 默认约 5 秒就消失，用 `EnumWindows` 事后去抓往往已经抓不到。
