// ============================================================================
//  宿舍电费提示 (PowerFeeTray)
//  广东警官学院 校园智能控电系统 —— 电量查询 + 低电量弹窗提醒
//
//  接口来源: https://yktxyk.gdppla.edu.cn/user/powerfee/index
//  逆向所得: POST /user/powerfee/getRoomInfo  免登录，返回全校房间及余额
//
//  编译: 见 build.ps1   (csc.exe / C# 5 语法)
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PowerFeeTray
{
    // ========================================================================
    //  常量与路径
    // ========================================================================
    internal static class AppInfo
    {
        public const string Title = "宿舍电费提示";
        public const string MutexName = "Global\\PowerFeeTray_SingleInstance_v1";
        public const string RunKey = "PowerFeeTray";
    }

    internal static class Paths
    {
        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PowerFeeTray");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }
        public static string Config { get { return Path.Combine(DataDir, "config.ini"); } }
        public static string Log { get { return Path.Combine(DataDir, "powerfee.log"); } }
        public static string History { get { return Path.Combine(DataDir, "history.csv"); } }
    }

    // ========================================================================
    //  日志
    // ========================================================================
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n";
                    File.AppendAllText(Paths.Log, line, new UTF8Encoding(true));

                    FileInfo fi = new FileInfo(Paths.Log);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string[] all = File.ReadAllLines(Paths.Log, Encoding.UTF8);
                        int keep = Math.Min(all.Length, 800);
                        string[] tail = new string[keep];
                        Array.Copy(all, all.Length - keep, tail, 0, keep);
                        File.WriteAllLines(Paths.Log, tail, new UTF8Encoding(true));
                    }
                }
            }
            catch { /* 日志失败绝不影响主流程 */ }
        }
    }

    // ========================================================================
    //  配置 (INI: key = value)
    // ========================================================================
    internal class Config
    {
        // 刻意不预置任何房间信息：这些默认值会被编译进分发的 exe，
        // 预置等于把开发者/使用者的宿舍位置公开出去。
        // 首次运行请用托盘菜单「设置...」从下拉列表中选择。
        public string Campus = "";
        public string Building = "";
        public string Room = "";
        public string RoomNum = "";
        public string SchoolAreaNo = "";
        public string BuildingNo = "";

        public string BaseUrl = "https://yktxyk.gdppla.edu.cn";
        public string ImplType = "CGCOMMON0001";
        public int TimeoutSeconds = 20;

        public double Threshold = 20.0;        // 低于此电量(度)触发提醒
        public int IntervalMinutes = 30;       // 轮询间隔
        public int CooldownMinutes = 180;      // 同一轮告警的最小重复间隔
        public double WarnRatio = 2.0;         // 低于 阈值*WarnRatio 时托盘变黄

        public bool AutoStart = true;
        public bool Sound = true;
        public bool Popup = true;
        // 弹窗与托盘气泡都锚定屏幕右下角，同时开启必然互相遮挡，
        // 所以默认只用自绘弹窗；气泡作为「不要弹窗」时的替代方案。
        public bool Balloon = false;

        /// <summary>是否已经配置了要监控的房间。未配置时应引导用户去设置。</summary>
        public bool HasRoom
        {
            get
            {
                return !string.IsNullOrEmpty(Room) &&
                       !string.IsNullOrEmpty(Building) &&
                       !string.IsNullOrEmpty(Campus);
            }
        }

        public static Config Load()
        {
            Config c = new Config();
            if (!File.Exists(Paths.Config))
            {
                c.Save();
                Log.Write("未找到配置，已生成默认配置: " + Paths.Config);
                return c;
            }

            try
            {
                string[] lines = File.ReadAllLines(Paths.Config, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("#") || s.StartsWith(";")) continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = s.Substring(eq + 1).Trim();

                    switch (k)
                    {
                        case "campus": c.Campus = v; break;
                        case "building": c.Building = v; break;
                        case "room": c.Room = v; break;
                        case "roomnum": c.RoomNum = v; break;
                        case "schoolareano": c.SchoolAreaNo = v; break;
                        case "buildingno": c.BuildingNo = v; break;
                        case "baseurl": c.BaseUrl = v; break;
                        case "impltype": c.ImplType = v; break;
                        case "timeoutseconds": c.TimeoutSeconds = ToInt(v, c.TimeoutSeconds); break;
                        case "threshold": c.Threshold = ToDouble(v, c.Threshold); break;
                        case "intervalminutes": c.IntervalMinutes = ToInt(v, c.IntervalMinutes); break;
                        case "cooldownminutes": c.CooldownMinutes = ToInt(v, c.CooldownMinutes); break;
                        case "warnratio": c.WarnRatio = ToDouble(v, c.WarnRatio); break;
                        case "autostart": c.AutoStart = ToBool(v, c.AutoStart); break;
                        case "sound": c.Sound = ToBool(v, c.Sound); break;
                        case "popup": c.Popup = ToBool(v, c.Popup); break;
                        case "balloon": c.Balloon = ToBool(v, c.Balloon); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取配置失败，使用默认值: " + ex.Message);
            }

            // 两种提醒都锚定屏幕右下角，同时开启会互相遮挡。
            // 旧版本默认两者皆开，这里统一收敛为只保留弹窗。
            if (c.Popup && c.Balloon)
            {
                c.Balloon = false;
                Log.Write("配置里 popup 与 balloon 同时为 true，已自动关闭 balloon 以免提醒重叠");
            }

            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 宿舍电费提示 —— 配置文件");
                sb.AppendLine("# 修改后需重启程序生效；也可以用托盘菜单的「设置」自动保存。");
                sb.AppendLine();
                sb.AppendLine("# ---- 监控的房间 ----");
                sb.AppendLine("campus = " + Campus);
                sb.AppendLine("building = " + Building);
                sb.AppendLine("room = " + Room);
                sb.AppendLine("roomNum = " + RoomNum);
                sb.AppendLine("schoolAreaNo = " + SchoolAreaNo);
                sb.AppendLine("buildingNo = " + BuildingNo);
                sb.AppendLine();
                sb.AppendLine("# ---- 提醒规则 ----");
                sb.AppendLine("# 剩余电量低于该值(度)时弹窗提醒");
                sb.AppendLine("threshold = " + Threshold.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("# 每隔多少分钟查询一次");
                sb.AppendLine("intervalMinutes = " + IntervalMinutes);
                sb.AppendLine("# 同一次低电量告警的重复提醒间隔(分钟)，避免反复弹窗");
                sb.AppendLine("cooldownMinutes = " + CooldownMinutes);
                sb.AppendLine("# 低于 threshold * warnRatio 时托盘图标转黄(预警)");
                sb.AppendLine("warnRatio = " + WarnRatio.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("# ---- 提醒方式 ----");
                sb.AppendLine("popup = " + Popup.ToString().ToLowerInvariant());
                sb.AppendLine("balloon = " + Balloon.ToString().ToLowerInvariant());
                sb.AppendLine("sound = " + Sound.ToString().ToLowerInvariant());
                sb.AppendLine("autostart = " + AutoStart.ToString().ToLowerInvariant());
                sb.AppendLine();
                sb.AppendLine("# ---- 接口参数(一般无需修改) ----");
                sb.AppendLine("# 查询接口 /user/powerfee/getRoomInfo 不校验任何令牌：");
                sb.AppendLine("#   实测不带、乱填、换节点号都返回完全相同的数据，");
                sb.AppendLine("#   因此本程序不携带 token 参数。");
                sb.AppendLine("# 真正必需的是 implType，取值为该学校的电控厂商实现编号。");
                sb.AppendLine("baseUrl = " + BaseUrl);
                sb.AppendLine("implType = " + ImplType);
                sb.AppendLine("timeoutSeconds = " + TimeoutSeconds);

                File.WriteAllText(Paths.Config, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Write("保存配置失败: " + ex.Message);
            }
        }

        private static int ToInt(string s, int d)
        {
            int r; if (int.TryParse(s, out r)) return r; return d;
        }
        private static double ToDouble(string s, double d)
        {
            double r;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            if (double.TryParse(s, out r)) return r;
            return d;
        }
        private static bool ToBool(string s, bool d)
        {
            if (string.IsNullOrEmpty(s)) return d;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on" || s == "是") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off" || s == "否") return false;
            return d;
        }
    }

    // ========================================================================
    //  数据模型
    // ========================================================================
    internal class RoomInfo
    {
        public string RoomNum = "";
        public string Campus = "";
        public string Building = "";
        public string Room = "";
        public string SchoolAreaNo = "";
        public string BuildingNo = "";
        public string Unit = "度";
        public string BalanceText = "";
        public double Balance;

        public string Label
        {
            get { return Campus + " " + Building + " " + Room; }
        }
    }

    internal class HistoryPoint
    {
        public DateTime T;
        public double V;
    }

    internal class QueryResult
    {
        public bool Ok;
        public string Error = "";
        public string Diag = "";
        public RoomInfo Room;
        public int TotalRooms;
    }

    // ========================================================================
    //  接口客户端
    // ========================================================================
    internal static class PowerApi
    {
        private const string UserAgent =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0.30";

        /// <summary>
        /// 拉取全校房间列表(含余额)。该接口免登录，一次请求即可拿到所有房间数据。
        /// </summary>
        public static QueryResult Query(Config cfg)
        {
            QueryResult qr = new QueryResult();

            // 还没配置房间时直接给出可操作的提示，不必白跑一次网络请求
            if (!cfg.HasRoom)
            {
                qr.Error = "还没有设置要监控的房间。\r\n请右键托盘图标 →「设置...」，" +
                           "从下拉列表中选择校区、楼栋和房间号。";
                return qr;
            }

            try
            {
                string url = cfg.BaseUrl + "/user/powerfee/getRoomInfo" +
                             "?from=wxminiprogram" +
                             "&implType=" + Uri.EscapeDataString(cfg.ImplType) +
                             "&buyMark=";

                string json = Post(url, "");

                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 32 * 1024 * 1024;
                Dictionary<string, object> root =
                    js.Deserialize<Dictionary<string, object>>(json);

                object retObj;
                bool ok = root.TryGetValue("ret", out retObj) && ToBool(retObj);
                if (!ok)
                {
                    object msgObj;
                    string msg = root.TryGetValue("msg", out msgObj) && msgObj != null
                        ? Convert.ToString(msgObj) : "未知错误";
                    qr.Error = "接口返回失败: " + msg;
                    return qr;
                }

                object arrObj;
                if (!root.TryGetValue("obj", out arrObj) || arrObj == null)
                {
                    qr.Error = "接口未返回房间数据";
                    qr.Diag = "ret=" + Convert.ToString(retObj) + " obj=null";
                    return qr;
                }

                // JavaScriptSerializer 对 JSON 数组可能给出 object[] 或 ArrayList，
                // 这里统一成 List<object>，避免依赖具体运行时类型。
                List<object> arr = AsList(arrObj);
                if (arr.Count == 0)
                {
                    qr.Error = "房间数据格式异常";
                    qr.Diag = "obj 运行时类型 = " + arrObj.GetType().FullName;
                    return qr;
                }

                List<RoomInfo> all = new List<RoomInfo>(arr.Count);
                foreach (object item in arr)
                {
                    Dictionary<string, object> d = item as Dictionary<string, object>;
                    if (d == null) continue;

                    RoomInfo r = new RoomInfo();
                    r.RoomNum = GetS(d, "roomNum");
                    r.Campus = GetS(d, "schoolArea");
                    r.Building = GetS(d, "building");
                    r.Room = GetS(d, "room");
                    r.SchoolAreaNo = GetS(d, "schoolAreaNo");
                    r.BuildingNo = GetS(d, "buildingNo");
                    string unit = GetS(d, "du");
                    if (unit.Length > 0) r.Unit = unit;
                    r.BalanceText = GetS(d, "formatPowerBalanceStr");

                    double bal;
                    string bs = GetS(d, "powerBalance");
                    if (double.TryParse(bs, NumberStyles.Float, CultureInfo.InvariantCulture, out bal))
                        r.Balance = bal;

                    all.Add(r);
                }

                qr.TotalRooms = all.Count;
                qr.Room = Find(all, cfg);
                if (qr.Room == null)
                {
                    qr.Error = "在 " + cfg.Campus + " " + cfg.Building + " " + cfg.Room +
                               " 中未找到房间，请检查房间号设置（共 " + all.Count + " 个房间）";
                    Log.Write("房间匹配失败: " + qr.Error);
                    return qr;
                }

                qr.Ok = true;
                return qr;
            }
            catch (WebException wex)
            {
                qr.Error = "网络错误: " + wex.Message;
                Log.Write("查询异常(WebException): " + wex.Message);
                return qr;
            }
            catch (Exception ex)
            {
                qr.Error = "查询异常: " + ex.Message;
                Log.Write("查询异常: " + ex.ToString());
                return qr;
            }
        }

        public static List<RoomInfo> FetchAll(Config cfg)
        {
            List<RoomInfo> list = new List<RoomInfo>();
            try
            {
                string url = cfg.BaseUrl + "/user/powerfee/getRoomInfo" +
                             "?from=wxminiprogram" +
                             "&implType=" + Uri.EscapeDataString(cfg.ImplType) +
                             "&buyMark=";
                string json = Post(url, "");
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = 32 * 1024 * 1024;
                Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(json);
                object arrObj;
                if (!root.TryGetValue("obj", out arrObj) || arrObj == null) return list;
                List<object> arr = AsList(arrObj);
                foreach (object item in arr)
                {
                    Dictionary<string, object> d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    RoomInfo r = new RoomInfo();
                    r.RoomNum = GetS(d, "roomNum");
                    r.Campus = GetS(d, "schoolArea");
                    r.Building = GetS(d, "building");
                    r.Room = GetS(d, "room");
                    r.SchoolAreaNo = GetS(d, "schoolAreaNo");
                    r.BuildingNo = GetS(d, "buildingNo");
                    string unit = GetS(d, "du");
                    if (unit.Length > 0) r.Unit = unit;
                    r.BalanceText = GetS(d, "formatPowerBalanceStr");
                    double bal;
                    if (double.TryParse(GetS(d, "powerBalance"), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out bal))
                        r.Balance = bal;
                    list.Add(r);
                }
            }
            catch (Exception ex)
            {
                Log.Write("FetchAll 失败: " + ex.Message);
            }
            return list;
        }

        private static RoomInfo Find(List<RoomInfo> all, Config cfg)
        {
            // 1) roomNum 精确匹配。但必须校验它和配置的房间名一致：
            //    用户改过房间号而 roomNum 是旧值时，若盲信编号会去监控错误的房间。
            if (!string.IsNullOrEmpty(cfg.RoomNum))
            {
                foreach (RoomInfo r in all)
                {
                    if (r.RoomNum != cfg.RoomNum) continue;
                    if (string.IsNullOrEmpty(cfg.Room) || Eq(r.Room, cfg.Room)) return r;
                    Log.Write("警告: 内部编号 " + cfg.RoomNum + " 实际对应 " + r.Label +
                              "，与配置的房间 " + cfg.Room + " 不一致，改为按名称匹配");
                    break;
                }
            }
            // 2) 校区 + 楼栋 + 房间
            foreach (RoomInfo r in all)
                if (Eq(r.Room, cfg.Room) && Eq(r.Building, cfg.Building) && Eq(r.Campus, cfg.Campus))
                    return r;
            // 3) 楼栋 + 房间
            foreach (RoomInfo r in all)
                if (Eq(r.Room, cfg.Room) && Eq(r.Building, cfg.Building)) return r;
            // 4) 仅房间号且唯一
            List<RoomInfo> hits = new List<RoomInfo>();
            foreach (RoomInfo r in all)
                if (Eq(r.Room, cfg.Room)) hits.Add(r);
            if (hits.Count == 1) return hits[0];

            return null;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetS(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        private static bool ToBool(object o)
        {
            if (o == null) return false;
            if (o is bool) return (bool)o;
            string s = Convert.ToString(o);
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        /// <summary>
        /// 把反序列化得到的数组统一成 List&lt;object&gt;。
        /// JavaScriptSerializer 视情况可能给出 object[] 或 ArrayList，不能硬转。
        /// </summary>
        private static List<object> AsList(object o)
        {
            List<object> list = new List<object>();
            if (o == null || o is string) return list;
            System.Collections.IEnumerable e = o as System.Collections.IEnumerable;
            if (e == null) return list;
            foreach (object item in e) list.Add(item);
            return list;
        }

        private static string Post(string url, string body)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.UserAgent = UserAgent;
            req.ContentType = "application/x-www-form-urlencoded";
            req.Accept = "application/json, text/javascript, */*; q=0.01";
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
            req.KeepAlive = false;
            req.Proxy = null;                 // 避免受系统代理拖慢
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;

            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream())
            {
                s.Write(data, 0, data.Length);
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }
    }

    // ========================================================================
    //  历史记录与用量估算
    // ========================================================================
    internal static class History
    {
        private const int MaxPoints = 4000;

        public static List<HistoryPoint> Load()
        {
            List<HistoryPoint> list = new List<HistoryPoint>();
            try
            {
                if (!File.Exists(Paths.History)) return list;
                foreach (string line in File.ReadAllLines(Paths.History, Encoding.UTF8))
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    string[] p = s.Split(',');
                    if (p.Length < 2) continue;
                    DateTime t;
                    double v;
                    if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out t)) continue;
                    if (!double.TryParse(p[1], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v)) continue;
                    HistoryPoint hp = new HistoryPoint();
                    hp.T = t; hp.V = v;
                    list.Add(hp);
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取历史失败: " + ex.Message);
            }
            return list;
        }

        public static void Append(double balance)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture) + "," +
                    balance.ToString(CultureInfo.InvariantCulture) + "\r\n";
                File.AppendAllText(Paths.History, line, new UTF8Encoding(true));

                // 简单裁剪，避免无限增长
                string[] all = File.ReadAllLines(Paths.History, Encoding.UTF8);
                if (all.Length > MaxPoints)
                {
                    int keep = MaxPoints / 2;
                    string[] tail = new string[keep];
                    Array.Copy(all, all.Length - keep, tail, 0, keep);
                    File.WriteAllLines(Paths.History, tail, new UTF8Encoding(true));
                }
            }
            catch (Exception ex)
            {
                Log.Write("写入历史失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 依据最近 72 小时的消耗速率估算还能用多少天；数据不足时返回 null。
        /// </summary>
        public static double? EstimateDaysLeft(List<HistoryPoint> hist, double current)
        {
            try
            {
                if (hist == null || hist.Count < 3) return null;
                DateTime now = DateTime.Now;

                int startIdx = -1;
                for (int i = 0; i < hist.Count; i++)
                {
                    if ((now - hist[i].T).TotalHours <= 72.0) { startIdx = i; break; }
                }
                if (startIdx < 0) return null;

                double hours = (now - hist[startIdx].T).TotalHours;
                if (hours < 6.0) return null;

                double drop = hist[startIdx].V - current;
                if (drop <= 0.05) return null;          // 没消耗，或刚充值

                double perHour = drop / hours;
                if (perHour <= 0.0001) return null;

                return current / (perHour * 24.0);
            }
            catch
            {
                return null;
            }
        }

        public static double? RecentDailyUsage(List<HistoryPoint> hist)
        {
            try
            {
                if (hist == null || hist.Count < 3) return null;
                DateTime now = DateTime.Now;
                int startIdx = -1;
                for (int i = 0; i < hist.Count; i++)
                {
                    if ((now - hist[i].T).TotalHours <= 72.0) { startIdx = i; break; }
                }
                if (startIdx < 0) return null;
                double hours = (now - hist[startIdx].T).TotalHours;
                if (hours < 6.0) return null;
                double drop = hist[startIdx].V - hist[hist.Count - 1].V;
                if (drop <= 0.05) return null;
                return drop / hours * 24.0;
            }
            catch { return null; }
        }
    }

    // ========================================================================
    //  图标生成(运行时绘制，无需外部资源文件)
    // ========================================================================
    internal static class Icons
    {
        public static readonly Color Green = Color.FromArgb(62, 142, 64);
        public static readonly Color Amber = Color.FromArgb(214, 137, 16);
        public static readonly Color Red = Color.FromArgb(197, 48, 48);
        public static readonly Color Gray = Color.FromArgb(120, 120, 120);

        private static Icon _ok, _warn, _low, _err;

        public static Icon Ok { get { if (_ok == null) _ok = Make(Green, "电"); return _ok; } }
        public static Icon Warn { get { if (_warn == null) _warn = Make(Amber, "电"); return _warn; } }
        public static Icon Low { get { if (_low == null) _low = Make(Red, "电"); return _low; } }
        public static Icon Err { get { if (_err == null) _err = Make(Gray, "?"); return _err; } }

        private static Icon Make(Color c, string glyph)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                using (Brush b = new SolidBrush(c))
                    g.FillEllipse(b, 1, 1, 30, 30);

                using (Brush tb = new SolidBrush(Color.White))
                using (Font f = new Font("Microsoft YaHei", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF sz = g.MeasureString(glyph, f);
                    g.DrawString(glyph, f, tb, (32f - sz.Width) / 2f, (32f - sz.Height) / 2f);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // ========================================================================
    //  低电量弹窗
    // ========================================================================
    internal class AlertForm : Form
    {
        private readonly System.Windows.Forms.Timer _autoClose = new System.Windows.Forms.Timer();
        private Label _lblCountdown = new Label();
        private int _secondsLeft = 90;

        public AlertForm(RoomInfo room, double threshold, double? daysLeft, double? dailyUsage,
                         bool sound)
        {
            Text = AppInfo.Title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            ClientSize = new Size(420, 208);
            Font = new Font("Microsoft YaHei", 9f, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;

            Panel header = new Panel();
            header.BackColor = Icons.Red;
            header.Bounds = new Rectangle(0, 0, 420, 46);
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Brush b = new SolidBrush(Color.White))
                using (Font f = new Font("Microsoft YaHei", 12f, FontStyle.Bold, GraphicsUnit.Point))
                {
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    e.Graphics.DrawString("⚠  宿舍电量不足", f, b, 16f, 12f);
                }
            };
            Controls.Add(header);

            Label lblRoom = new Label();
            lblRoom.AutoSize = false;
            lblRoom.Bounds = new Rectangle(18, 58, 384, 22);
            lblRoom.ForeColor = Color.FromArgb(90, 90, 90);
            lblRoom.Font = new Font("Microsoft YaHei", 9f);
            lblRoom.Text = room.Label + "   (编号 " + room.RoomNum + ")";
            Controls.Add(lblRoom);

            Label lblBalance = new Label();
            lblBalance.AutoSize = false;
            lblBalance.Bounds = new Rectangle(16, 82, 384, 44);
            lblBalance.ForeColor = Icons.Red;
            lblBalance.Font = new Font("Microsoft YaHei", 22f, FontStyle.Bold);
            lblBalance.Text = "剩余 " + room.BalanceText;
            Controls.Add(lblBalance);

            string sub = "已低于提醒阈值 " + threshold.ToString("0.##", CultureInfo.InvariantCulture) + " " + room.Unit;
            if (daysLeft.HasValue)
            {
                sub += "    按近期用量估算还可使用约 " + daysLeft.Value.ToString("0.0") + " 天";
            }
            else if (dailyUsage.HasValue)
            {
                sub += "    近期日均用量约 " + dailyUsage.Value.ToString("0.0") + " " + room.Unit + "/天";
            }

            Label lblSub = new Label();
            lblSub.AutoSize = false;
            lblSub.Bounds = new Rectangle(18, 128, 384, 20);
            lblSub.ForeColor = Color.FromArgb(70, 70, 70);
            lblSub.Text = sub;
            Controls.Add(lblSub);

            Button btnOk = new Button();
            btnOk.Text = "知道了";
            btnOk.Bounds = new Rectangle(310, 160, 96, 32);
            btnOk.FlatStyle = FlatStyle.System;
            btnOk.Click += delegate { Close(); };
            Controls.Add(btnOk);

            _lblCountdown.AutoSize = false;
            _lblCountdown.Bounds = new Rectangle(18, 168, 180, 18);
            _lblCountdown.ForeColor = Color.FromArgb(150, 150, 150);
            _lblCountdown.Font = new Font("Microsoft YaHei", 8f);
            _lblCountdown.Text = _secondsLeft + " 秒后自动关闭";
            Controls.Add(_lblCountdown);

            _autoClose.Interval = 1000;
            _autoClose.Tick += delegate
            {
                _secondsLeft--;
                if (_secondsLeft <= 0)
                {
                    _autoClose.Stop();
                    Close();
                }
                else
                {
                    _lblCountdown.Text = _secondsLeft + " 秒后自动关闭";
                }
            };

            if (sound)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); }
                catch { }
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }      // 置顶显示但不抢焦点，避免打断正在打字的用户
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            _autoClose.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoClose.Stop();
            _autoClose.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ========================================================================
    //  设置窗口
    // ========================================================================
    internal class SettingsForm : Form
    {
        private readonly Config _cfg;

        private ComboBox _cbCampus, _cbBuilding, _cbRoom;
        private ComboBox _cbThreshold, _cbInterval, _cbCooldown, _cbNotify;
        private CheckBox _chkSound, _chkAutoStart;
        private Label _lblResolved, _lblStatus;
        private Button _btnReload, _btnTest, _btnSave;

        private List<RoomInfo> _rooms = new List<RoomInfo>();
        private bool _loading;
        private bool _suppress;      // 抑制级联事件，避免刷新时递归触发
        private string _applyError = "";

        /// <summary>下拉项：显示房间号与当前余额，同时携带原始数据方便取编号。</summary>
        private class RoomItem
        {
            public RoomInfo Room;

            public override string ToString()
            {
                if (Room == null) return "";
                return Room.Room + "    (当前 " + Room.BalanceText + ")";
            }
        }

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            Text = AppInfo.Title + " - 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(486, 484);
            Font = new Font("Microsoft YaHei", 9f);

            int y = 18;

            AddLabel("校区", 20, y + 3);
            _cbCampus = AddCombo(120, y, 346, false);
            _cbCampus.SelectedIndexChanged += delegate { if (!_suppress) PopulateBuildings(); };
            y += 34;

            AddLabel("楼栋", 20, y + 3);
            _cbBuilding = AddCombo(120, y, 346, false);
            _cbBuilding.SelectedIndexChanged += delegate { if (!_suppress) PopulateRooms(); };
            y += 34;

            AddLabel("房间号", 20, y + 3);
            _cbRoom = AddCombo(120, y, 346, true);
            _cbRoom.SelectedIndexChanged += delegate { if (!_suppress) UpdateResolved(); };
            y += 32;

            _lblResolved = new Label();
            _lblResolved.AutoSize = false;
            _lblResolved.Bounds = new Rectangle(120, y, 346, 20);
            _lblResolved.ForeColor = Color.Gray;
            _lblResolved.Text = "内部编号将在选择房间后自动识别";
            Controls.Add(_lblResolved);
            y += 34;

            AddLabel("提醒阈值", 20, y + 3);
            _cbThreshold = AddCombo(120, y, 110, true);   // 可键入自定义数值
            AddLabel("度（可直接填写自定义值）", 240, y + 3);
            y += 32;

            AddLabel("查询间隔", 20, y + 3);
            _cbInterval = AddCombo(120, y, 110, false);
            AddLabel("分钟", 240, y + 3);
            y += 32;

            AddLabel("重复提醒", 20, y + 3);
            _cbCooldown = AddCombo(120, y, 110, false);
            AddLabel("分钟", 240, y + 3);
            y += 38;

            // 提醒方式做成单选下拉：两种提醒都锚在右下角，只能选一种，避免互相遮挡
            AddLabel("提醒方式", 20, y + 3);
            _cbNotify = AddCombo(120, y, 240, false);
            FillTextOptions(_cbNotify, NotifyModes(),
                            cfg.Popup ? 0 : (cfg.Balloon ? 1 : 2));
            y += 36;

            _chkSound = new CheckBox();
            _chkSound.Text = "声音提醒";
            _chkSound.Checked = cfg.Sound;
            _chkSound.Bounds = new Rectangle(120, y, 100, 22);
            Controls.Add(_chkSound);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动";
            _chkAutoStart.Checked = cfg.AutoStart;
            _chkAutoStart.Bounds = new Rectangle(240, y, 130, 22);
            Controls.Add(_chkAutoStart);
            y += 36;

            _btnReload = new Button();
            _btnReload.Text = "重新加载房间列表";
            _btnReload.Bounds = new Rectangle(20, y, 142, 30);
            _btnReload.Click += delegate { StartLoad(); };
            Controls.Add(_btnReload);

            _btnTest = new Button();
            _btnTest.Text = "测试查询";
            _btnTest.Bounds = new Rectangle(172, y, 100, 30);
            _btnTest.Click += OnTest;
            Controls.Add(_btnTest);
            y += 42;

            _lblStatus = new Label();
            _lblStatus.AutoSize = false;
            _lblStatus.Bounds = new Rectangle(20, y, 446, 58);
            _lblStatus.ForeColor = Color.FromArgb(80, 80, 80);
            _lblStatus.Text = "";
            Controls.Add(_lblStatus);
            y += 66;

            _btnSave = new Button();
            _btnSave.Text = "保存";
            _btnSave.Bounds = new Rectangle(266, y, 96, 30);
            _btnSave.Enabled = false;          // 房间列表加载成功后才允许保存
            _btnSave.Click += OnSave;
            Controls.Add(_btnSave);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Bounds = new Rectangle(370, y, 96, 30);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = _btnSave;
            CancelButton = btnCancel;

            // 预设选项：全部靠下拉选择，不需要手输任何内容
            FillOptions(_cbThreshold, PresetThresholds(),
                        cfg.Threshold.ToString("0.##", CultureInfo.InvariantCulture));
            FillOptions(_cbInterval, PresetIntervals(),
                        cfg.IntervalMinutes.ToString(CultureInfo.InvariantCulture));
            FillOptions(_cbCooldown, PresetCooldowns(),
                        cfg.CooldownMinutes.ToString(CultureInfo.InvariantCulture));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartLoad();
        }

        private void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            Controls.Add(l);
        }

        /// <summary>
        /// 创建下拉框。allowType=false 时是纯选择（只能从选项里挑，杜绝无效值）；
        /// allowType=true 时允许键入，配合自动补全在几百个房间里快速定位。
        ///
        /// 注意：WinForms 限制 DropDownList 样式下 AutoCompleteSource.ListItems
        /// 只能搭配 AutoCompleteMode.None，所以两种需求必须用不同样式实现。
        /// </summary>
        private ComboBox AddCombo(int x, int y, int w, bool allowType)
        {
            ComboBox cb = new ComboBox();
            cb.Bounds = new Rectangle(x, y, w, 24);
            cb.MaxDropDownItems = 16;
            if (allowType)
            {
                cb.DropDownStyle = ComboBoxStyle.DropDown;
                cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                cb.AutoCompleteSource = AutoCompleteSource.ListItems;
            }
            else
            {
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.AutoCompleteMode = AutoCompleteMode.None;
            }
            Controls.Add(cb);
            return cb;
        }

        private static double[] PresetThresholds()
        {
            return new double[] { 5, 10, 15, 20, 30, 50, 80, 100 };
        }

        /// <summary>提醒方式：两种提醒都锚在屏幕右下角，只能三选一。</summary>
        private static string[] NotifyModes()
        {
            return new string[]
            {
                "右下角弹窗（推荐）",
                "系统托盘通知",
                "不弹出，仅改托盘图标"
            };
        }

        private static void FillTextOptions(ComboBox cb, string[] items, int sel)
        {
            cb.Items.Clear();
            foreach (string s in items) cb.Items.Add(s);
            if (sel < 0 || sel >= items.Length) sel = 0;
            cb.SelectedIndex = sel;
        }

        private static double[] PresetIntervals()
        {
            return new double[] { 10, 15, 30, 60, 120, 180 };
        }

        private static double[] PresetCooldowns()
        {
            return new double[] { 60, 120, 180, 360, 720 };
        }

        /// <summary>填充数值选项；若当前值不在预设里则一并加入，避免丢失原有配置。</summary>
        private static void FillOptions(ComboBox cb, double[] presets, string current)
        {
            List<string> items = new List<string>();
            foreach (double v in presets) items.Add(v.ToString("0.##", CultureInfo.InvariantCulture));
            if (!items.Contains(current))
            {
                items.Add(current);
                items.Sort(delegate(string a, string b)
                {
                    double va, vb;
                    double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out va);
                    double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out vb);
                    return va.CompareTo(vb);
                });
            }

            cb.Items.Clear();
            foreach (string s in items) cb.Items.Add(s);
            int idx = cb.Items.IndexOf(current);
            cb.SelectedIndex = idx >= 0 ? idx : 0;
        }

        /// <summary>
        /// 取下拉框的当前值。
        /// 纯选择型(DropDownList)以选中项为准；
        /// 可编辑型(DropDown)必须以框内文字为准 —— 否则用户键入的自定义值
        /// 会被残留的 SelectedItem 覆盖（这个坑导致自定义阈值曾保存失效）。
        /// </summary>
        private static string SelText(ComboBox cb)
        {
            if (cb.DropDownStyle == ComboBoxStyle.DropDownList && cb.SelectedItem != null)
                return Convert.ToString(cb.SelectedItem);
            return cb.Text;
        }

        private static double SelNumber(ComboBox cb, double fallback)
        {
            double v;
            if (double.TryParse(SelText(cb).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsText(List<string> list, string s)
        {
            foreach (string x in list) if (Eq(x, s)) return true;
            return false;
        }

        /// <summary>在只读下拉框里按文本找下标（兼容普通字符串与 RoomItem）。</summary>
        private static int IndexOfItem(ComboBox.ObjectCollection items, string text)
        {
            for (int i = 0; i < items.Count; i++)
            {
                RoomItem ri = items[i] as RoomItem;
                string s = ri != null ? ri.Room.Room : Convert.ToString(items[i]);
                if (Eq(s, text)) return i;
            }
            return -1;
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.ForeColor = color;
            _lblStatus.Text = text;
        }

        /// <summary>从接口加载房间列表并填充三级下拉框。</summary>
        private void StartLoad()
        {
            if (_loading) return;
            _loading = true;
            _btnReload.Enabled = false;
            _btnSave.Enabled = false;
            SetStatus("正在从服务器加载房间列表，请稍候...", Color.FromArgb(80, 80, 80));

            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                e.Result = PowerApi.FetchAll(_cfg);
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                _loading = false;
                _btnReload.Enabled = true;

                List<RoomInfo> list = e.Result as List<RoomInfo>;
                if (list == null || list.Count == 0)
                {
                    SetStatus("房间列表加载失败（可能是网络问题）。\r\n请点「重新加载房间列表」重试。",
                              Icons.Red);
                    return;
                }

                _rooms = list;
                PopulateCampuses();
                _btnSave.Enabled = true;
                SetStatus("已加载 " + _rooms.Count + " 个房间。请依次选择校区、楼栋、房间号" +
                          "（房间号可直接键入数字跳转）；提醒阈值可从下拉选择或直接填写自定义值。",
                          Icons.Green);
            };
            bw.RunWorkerAsync();
        }

        private void PopulateCampuses()
        {
            _suppress = true;

            List<string> campuses = new List<string>();
            foreach (RoomInfo r in _rooms)
                if (!ContainsText(campuses, r.Campus)) campuses.Add(r.Campus);
            campuses.Sort(StringComparer.OrdinalIgnoreCase);

            _cbCampus.Items.Clear();
            foreach (string c in campuses) _cbCampus.Items.Add(c);
            int idx = IndexOfItem(_cbCampus.Items, _cfg.Campus);
            if (idx < 0 && _cbCampus.Items.Count > 0) idx = 0;
            if (idx >= 0) _cbCampus.SelectedIndex = idx;

            _suppress = false;
            PopulateBuildings();
        }

        private void PopulateBuildings()
        {
            string campus = SelText(_cbCampus).Trim();
            _suppress = true;

            List<string> blds = new List<string>();
            foreach (RoomInfo r in _rooms)
                if (Eq(r.Campus, campus) && !ContainsText(blds, r.Building)) blds.Add(r.Building);
            blds.Sort(StringComparer.OrdinalIgnoreCase);

            _cbBuilding.Items.Clear();
            foreach (string b in blds) _cbBuilding.Items.Add(b);
            int idx = IndexOfItem(_cbBuilding.Items, _cfg.Building);
            if (idx < 0 && _cbBuilding.Items.Count > 0) idx = 0;
            if (idx >= 0) _cbBuilding.SelectedIndex = idx;

            _suppress = false;
            PopulateRooms();
        }

        private void PopulateRooms()
        {
            string campus = SelText(_cbCampus).Trim();
            string building = SelText(_cbBuilding).Trim();
            _suppress = true;

            List<RoomInfo> rooms = new List<RoomInfo>();
            foreach (RoomInfo r in _rooms)
                if (Eq(r.Campus, campus) && Eq(r.Building, building)) rooms.Add(r);
            rooms.Sort(delegate(RoomInfo a, RoomInfo b)
            {
                return Natural.Compare(a.Room, b.Room);
            });

            _cbRoom.Items.Clear();
            foreach (RoomInfo r in rooms)
            {
                RoomItem it = new RoomItem();
                it.Room = r;
                _cbRoom.Items.Add(it);
            }
            int idx = IndexOfItem(_cbRoom.Items, _cfg.Room);
            if (idx < 0 && _cbRoom.Items.Count > 0) idx = 0;
            if (idx >= 0) _cbRoom.SelectedIndex = idx;

            _suppress = false;
            UpdateResolved();
        }

        /// <summary>当前选中的房间；未选中或输入无效时返回 null。</summary>
        private RoomInfo SelectedRoom()
        {
            RoomItem it = _cbRoom.SelectedItem as RoomItem;
            if (it != null) return it.Room;

            // 房间框可编辑：允许直接键入房间号，按名称宽松解析
            string typed = _cbRoom.Text.Trim();
            int p = typed.IndexOf("  (");
            if (p > 0) typed = typed.Substring(0, p).Trim();
            if (typed.Length == 0) return null;

            string campus = SelText(_cbCampus).Trim();
            string building = SelText(_cbBuilding).Trim();

            RoomInfo loose = null;
            int hits = 0;
            foreach (RoomInfo r in _rooms)
            {
                if (!Eq(r.Room, typed)) continue;
                if (Eq(r.Campus, campus) && Eq(r.Building, building)) return r;
                loose = r;
                hits++;
            }
            return hits == 1 ? loose : null;
        }

        private void UpdateResolved()
        {
            RoomInfo r = SelectedRoom();
            if (r != null)
            {
                _lblResolved.ForeColor = Icons.Green;
                _lblResolved.Text = "内部编号 " + r.RoomNum + "（已自动识别，无需填写）";
            }
            else
            {
                _lblResolved.ForeColor = Color.Gray;
                _lblResolved.Text = "内部编号将在选择房间后自动识别";
            }
        }

        // ---- 以下三个方法仅供 --dump-settings 无界面验证使用 ----

        internal bool IsLoaded { get { return _rooms.Count > 0; } }

        internal int CampusCount { get { return _cbCampus.Items.Count; } }

        /// <summary>诊断用：按文本切换三个下拉框（null 表示保持不变）。</summary>
        internal void DebugSet(string campus, string building, string room)
        {
            SetComboByText(_cbCampus, campus);
            SetComboByText(_cbBuilding, building);
            SetComboByText(_cbRoom, room);
        }

        // 诊断用的下标式切换：刻意不写死任何校区/楼栋名，
        // 否则这些名字会被编译进分发的 exe。
        internal void DebugSetCampusIndex(int i)
        {
            if (i >= 0 && i < _cbCampus.Items.Count) _cbCampus.SelectedIndex = i;
        }

        internal void DebugSetBuildingIndex(int i)
        {
            if (i >= 0 && i < _cbBuilding.Items.Count) _cbBuilding.SelectedIndex = i;
        }

        internal void DebugSetRoomIndex(int i)
        {
            if (i >= 0 && i < _cbRoom.Items.Count) _cbRoom.SelectedIndex = i;
        }

        private static void SetComboByText(ComboBox cb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int i = IndexOfItem(cb.Items, text);
            if (i >= 0) cb.SelectedIndex = i;
        }

        /// <summary>诊断用：导出当前下拉框与解析结果。</summary>
        internal string DumpState(string tag)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- " + tag + " ---");
            sb.AppendLine("  campus   items=" + _cbCampus.Items.Count +
                          "  sel=\"" + SelText(_cbCampus) + "\"");
            sb.AppendLine("  building items=" + _cbBuilding.Items.Count +
                          "  sel=\"" + SelText(_cbBuilding) + "\"");
            sb.AppendLine("  room     items=" + _cbRoom.Items.Count +
                          "  sel=\"" + SelText(_cbRoom) + "\"");
            sb.AppendLine("  resolved=\"" + _lblResolved.Text + "\"");
            sb.AppendLine("  threshold sel=\"" + SelText(_cbThreshold) +
                          "\" (" + _cbThreshold.Items.Count + " 项, style=" +
                          _cbThreshold.DropDownStyle + ")");
            sb.AppendLine("  interval  sel=\"" + SelText(_cbInterval) +
                          "\" (" + _cbInterval.Items.Count + " 项)");
            sb.AppendLine("  cooldown  sel=\"" + SelText(_cbCooldown) +
                          "\" (" + _cbCooldown.Items.Count + " 项)");
            sb.AppendLine("  notify    sel=\"" + SelText(_cbNotify) +
                          "\" (popup=" + _cfg.Popup + " balloon=" + _cfg.Balloon + ")");
            sb.AppendLine("  saveEnabled=" + _btnSave.Enabled +
                          "  comboStyle=" + _cbRoom.DropDownStyle);
            RoomInfo r = SelectedRoom();
            sb.AppendLine("  selectedRoom=" + (r == null
                ? "(null)"
                : r.Label + "  roomNum=" + r.RoomNum +
                  "  schoolAreaNo=" + r.SchoolAreaNo + "  buildingNo=" + r.BuildingNo));
            sb.AppendLine("  status=\"" + _lblStatus.Text.Replace("\r\n", " ") + "\"");
            return sb.ToString();
        }

        private void OnTest(object sender, EventArgs e)
        {
            RoomInfo sel = SelectedRoom();
            if (sel == null)
            {
                SetStatus("请先从下拉列表中选择一个房间。", Icons.Red);
                return;
            }

            Config tmp = new Config();
            tmp.BaseUrl = _cfg.BaseUrl;
            tmp.ImplType = _cfg.ImplType;
            tmp.Campus = sel.Campus;
            tmp.Building = sel.Building;
            tmp.Room = sel.Room;
            tmp.RoomNum = sel.RoomNum;

            _btnTest.Enabled = false;
            Cursor = Cursors.WaitCursor;
            SetStatus("正在查询 " + sel.Label + " ...", Color.FromArgb(80, 80, 80));

            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += delegate(object s2, DoWorkEventArgs e2)
            {
                e2.Result = PowerApi.Query(tmp);
            };
            bw.RunWorkerCompleted += delegate(object s2, RunWorkerCompletedEventArgs e2)
            {
                Cursor = Cursors.Default;
                _btnTest.Enabled = true;
                QueryResult qr = e2.Result as QueryResult;
                if (qr != null && qr.Ok)
                {
                    SetStatus("查询成功：" + qr.Room.Label + "  剩余 " + qr.Room.BalanceText +
                              "（内部编号 " + qr.Room.RoomNum + "）", Icons.Green);
                }
                else
                {
                    SetStatus(qr != null ? qr.Error : "查询失败", Icons.Red);
                }
            };
            bw.RunWorkerAsync();
        }

        /// <summary>
        /// 把界面上的选择写回配置。房间相关字段（含内部编号、校区/楼栋编号）
        /// 全部由选中的房间推导，因此不可能留下过期值。
        /// </summary>
        private bool ApplyToConfig()
        {
            _applyError = "";

            RoomInfo r = SelectedRoom();
            if (r == null)
            {
                _applyError = "请先从下拉列表中选择有效的校区、楼栋和房间号。";
                return false;
            }

            // 提醒阈值支持自定义数值（既可从下拉预设里选，也可直接键入）
            double thr;
            if (!double.TryParse(SelText(_cbThreshold).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out thr))
            {
                _applyError = "提醒阈值「" + SelText(_cbThreshold).Trim() +
                              "」不是有效数字。\r\n请从下拉列表选择，或直接填写 0.1 ~ 9999 之间的数字（单位：度）。";
                return false;
            }
            if (thr <= 0 || thr > 9999)
            {
                _applyError = "提醒阈值需要在 0.1 ~ 9999 度之间，当前填的是 " +
                              thr.ToString("0.##", CultureInfo.InvariantCulture) + "。";
                return false;
            }
            _cfg.Threshold = thr;

            _cfg.Campus = r.Campus;
            _cfg.Building = r.Building;
            _cfg.Room = r.Room;
            _cfg.RoomNum = r.RoomNum;
            _cfg.SchoolAreaNo = r.SchoolAreaNo;
            _cfg.BuildingNo = r.BuildingNo;

            double iv = SelNumber(_cbInterval, _cfg.IntervalMinutes);
            _cfg.IntervalMinutes = iv < 1 ? 1 : (int)Math.Round(iv);

            double cd = SelNumber(_cbCooldown, _cfg.CooldownMinutes);
            _cfg.CooldownMinutes = cd < 0 ? 0 : (int)Math.Round(cd);

            _cfg.Sound = _chkSound.Checked;
            _cfg.AutoStart = _chkAutoStart.Checked;

            // 提醒方式互斥：0=自绘弹窗，1=托盘通知，2=不弹
            int notify = _cbNotify.SelectedIndex;
            _cfg.Popup = (notify == 0);
            _cfg.Balloon = (notify == 1);

            _cfg.Save();
            return true;
        }

        internal bool DebugSave()
        {
            return ApplyToConfig();
        }

        internal void DebugSetThreshold(string text)
        {
            _cbThreshold.Text = text;
        }

        private void OnSave(object sender, EventArgs e)
        {
            if (!ApplyToConfig())
            {
                MessageBox.Show(_applyError, AppInfo.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Log.Write("设置已保存: " + _cfg.Campus + " " + _cfg.Building + " " + _cfg.Room +
                      " (编号 " + _cfg.RoomNum + ") 阈值=" + _cfg.Threshold +
                      " 间隔=" + _cfg.IntervalMinutes + "分钟");
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>
    /// 房间号的自然排序：把连续数字段按数值比较，
    /// 让 "101" &lt; "1A201" &lt; "1A1002" 这种顺序符合直觉。
    /// </summary>
    internal static class Natural
    {
        public static int Compare(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";
            int i = 0, j = 0;

            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }

    // ========================================================================
    //  托盘主程序
    // ========================================================================
    internal class TrayContext : ApplicationContext
    {
        private readonly Config _cfg;
        private readonly Form _marshal;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _tick;
        private readonly List<HistoryPoint> _history;

        private DateTime _nextRun = DateTime.MinValue;
        private DateTime _lastAlert = DateTime.MinValue;
        private DateTime _lastQuery = DateTime.MinValue;
        private bool _busy;
        private int _failCount;
        private RoomInfo _lastRoom;
        private double? _daysLeft;
        private double? _dailyUsage;

        private ToolStripMenuItem _miStatus;
        private ToolStripMenuItem _miQuery;
        private ToolStripMenuItem _miSound;
        private ToolStripMenuItem _miAutoStart;
        private AlertForm _alert;

        public TrayContext()
        {
            // 隐藏窗口，仅用于把工作投递回 UI 线程（见 Post 方法）
            _marshal = new Form();
            _marshal.ShowInTaskbar = false;
            _marshal.FormBorderStyle = FormBorderStyle.None;
            IntPtr forceHandle = _marshal.Handle;   // 强制创建句柄，BeginInvoke 才能用

            _cfg = Config.Load();
            _history = History.Load();

            ApplyAutoStart(_cfg.AutoStart);

            _tray = new NotifyIcon();
            _tray.Icon = Icons.Err;
            _tray.Text = AppInfo.Title;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { QueryNow(true); };
            _tray.ContextMenuStrip = BuildMenu();

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 60 * 1000;         // 每分钟检查一次是否到点
            _tick.Tick += OnTick;
            _tick.Start();

            _nextRun = DateTime.MinValue;       // 启动后立即查一次

            if (_cfg.HasRoom)
            {
                Log.Write("程序启动。监控 " + _cfg.Campus + " " + _cfg.Building + " " + _cfg.Room +
                          "，阈值 " + _cfg.Threshold + " 度，间隔 " + _cfg.IntervalMinutes + " 分钟");
                QueryNow(false);
            }
            else
            {
                // 首次运行：没有房间可监控，引导用户去设置里选一个
                Log.Write("程序启动。尚未配置房间，等待用户在设置中选择");
                _tray.Text = Trim(AppInfo.Title + " - 请先在设置里选择房间");
                if (_miStatus != null) _miStatus.Text = "尚未选择房间";

                int iv = Math.Max(1, _cfg.IntervalMinutes);
                _nextRun = DateTime.Now.AddMinutes(iv);

                Post(delegate
                {
                    MessageBox.Show(
                        "还没有选择要监控的房间。\r\n\r\n" +
                        "接下来会打开设置窗口，请从下拉列表中选择你的校区、楼栋和房间号" +
                        "（不需要知道任何内部编号，选中后会自动识别）。",
                        AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    OpenSettings();
                });
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei", 9f);

            _miStatus = new ToolStripMenuItem("正在查询...");
            _miStatus.Enabled = false;
            m.Items.Add(_miStatus);
            m.Items.Add(new ToolStripSeparator());

            _miQuery = new ToolStripMenuItem("立即查询");
            _miQuery.Font = new Font("Microsoft YaHei", 9f, FontStyle.Bold);
            _miQuery.Click += delegate { QueryNow(true); };
            m.Items.Add(_miQuery);

            ToolStripMenuItem miDetail = new ToolStripMenuItem("打开用电明细");
            miDetail.Click += delegate { OpenDetail(); };
            m.Items.Add(miDetail);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置...");
            miSettings.Click += delegate { OpenSettings(); };
            m.Items.Add(miSettings);

            _miSound = new ToolStripMenuItem("声音提醒");
            _miSound.CheckOnClick = true;
            _miSound.Checked = _cfg.Sound;
            _miSound.Click += delegate
            {
                _cfg.Sound = _miSound.Checked;
                _cfg.Save();
            };
            m.Items.Add(_miSound);

            _miAutoStart = new ToolStripMenuItem("开机自动启动");
            _miAutoStart.CheckOnClick = true;
            _miAutoStart.Checked = _cfg.AutoStart;
            _miAutoStart.Click += delegate
            {
                _cfg.AutoStart = _miAutoStart.Checked;
                ApplyAutoStart(_cfg.AutoStart);
                _cfg.Save();
            };
            m.Items.Add(_miAutoStart);

            ToolStripMenuItem miFolder = new ToolStripMenuItem("打开数据文件夹");
            miFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", Paths.DataDir); }
                catch (Exception ex) { Log.Write("打开目录失败: " + ex.Message); }
            };
            m.Items.Add(miFolder);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miAbout = new ToolStripMenuItem("关于");
            miAbout.Click += delegate
            {
                MessageBox.Show(
                    AppInfo.Title + "\r\n\r\n" +
                    "数据来源：广东警官学院 校园智能控电系统\r\n" +
                    "接口：POST /user/powerfee/getRoomInfo\r\n" +
                    "查询间隔：" + _cfg.IntervalMinutes + " 分钟\r\n" +
                    "提醒阈值：" + _cfg.Threshold + " 度\r\n\r\n" +
                    "配置文件：" + Paths.Config + "\r\n" +
                    "历史记录：" + Paths.History + "\r\n" +
                    "运行日志：" + Paths.Log,
                    AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            m.Items.Add(miAbout);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            m.Items.Add(miExit);

            return m;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (DateTime.Now >= _nextRun && !_busy)
            {
                QueryNow(false);
            }
        }

        private void QueryNow(bool manual)
        {
            if (_busy) return;
            _busy = true;
            if (manual && _miQuery != null) _miQuery.Text = "查询中...";

            bool m = manual;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                QueryResult qr;
                try
                {
                    qr = PowerApi.Query(_cfg);
                }
                catch (Exception ex)
                {
                    // 兜底：任何意外都只能变成一次查询失败，绝不能终结进程
                    qr = new QueryResult();
                    qr.Error = "查询线程异常: " + ex.Message;
                    Log.Write("查询线程异常: " + ex.ToString());
                }

                QueryResult result = qr;
                Post(delegate
                {
                    _busy = false;
                    if (_miQuery != null) _miQuery.Text = "立即查询";
                    try
                    {
                        HandleResult(result, m);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("处理查询结果异常: " + ex.ToString());
                    }
                });
            });
        }

        /// <summary>
        /// 把工作投递回 UI 线程执行。
        ///
        /// 注意：不能用 SynchronizationContext.Current —— 构造函数执行时
        /// Application.Run 还没安装 WinForms 同步上下文，那会拿到 null。
        /// 这里用一个永不显示的窗口作为回调宿主，最稳妥。
        /// </summary>
        private void Post(Action work)
        {
            try
            {
                if (_marshal != null && _marshal.IsHandleCreated)
                {
                    _marshal.BeginInvoke(work);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("投递 UI 回调失败: " + ex.Message);
            }

            try { work(); }
            catch (Exception ex)
            {
                Log.Write("执行 UI 回调失败: " + ex.Message);
            }
        }

        private void HandleResult(QueryResult qr, bool manual)
        {
            _lastQuery = DateTime.Now;

            if (!qr.Ok)
            {
                _failCount++;
                _tray.Icon = Icons.Err;
                _tray.Text = Trim(AppInfo.Title + " - 查询失败");
                if (_miStatus != null) _miStatus.Text = "查询失败：" + Shorten(qr.Error, 40);

                Log.Write("查询失败(" + _failCount + "): " + qr.Error);

                // 网络/接口异常：5 分钟后重试，不要等到下一个完整周期
                _nextRun = DateTime.Now.AddMinutes(5);

                if (manual)
                {
                    MessageBox.Show(qr.Error, AppInfo.Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            _failCount = 0;
            _lastRoom = qr.Room;
            _nextRun = DateTime.Now.AddMinutes(Math.Max(1, _cfg.IntervalMinutes));

            // roomNum 可能被自动匹配出来，回写以便下次精确匹配
            if (!string.IsNullOrEmpty(qr.Room.RoomNum) && qr.Room.RoomNum != _cfg.RoomNum)
            {
                _cfg.RoomNum = qr.Room.RoomNum;
                _cfg.Save();
            }

            // 校区/楼栋编号一并回写，保证「打开用电明细」的链接始终指向正确房间
            if (qr.Room.SchoolAreaNo.Length > 0 && qr.Room.SchoolAreaNo != _cfg.SchoolAreaNo)
            {
                _cfg.SchoolAreaNo = qr.Room.SchoolAreaNo;
                _cfg.BuildingNo = qr.Room.BuildingNo;
                _cfg.Save();
            }

            History.Append(qr.Room.Balance);
            HistoryPoint hp = new HistoryPoint();
            hp.T = DateTime.Now; hp.V = qr.Room.Balance;
            _history.Add(hp);

            _daysLeft = History.EstimateDaysLeft(_history, qr.Room.Balance);
            _dailyUsage = History.RecentDailyUsage(_history);

            bool low = qr.Room.Balance < _cfg.Threshold;
            bool warn = !low && qr.Room.Balance < _cfg.Threshold * _cfg.WarnRatio;

            if (low) _tray.Icon = Icons.Low;
            else if (warn) _tray.Icon = Icons.Warn;
            else _tray.Icon = Icons.Ok;

            UpdateUiText(qr.Room);
            Log.Write("查询成功: " + qr.Room.Label + " = " + qr.Room.BalanceText +
                      (low ? "  [低于阈值]" : "") + (manual ? "  (手动)" : ""));

            if (low)
            {
                bool cooldownOver = (DateTime.Now - _lastAlert).TotalMinutes >= _cfg.CooldownMinutes;
                if (cooldownOver || manual)
                {
                    _lastAlert = DateTime.Now;
                    NotifyLow(qr.Room);
                }
            }
        }

        private void UpdateUiText(RoomInfo r)
        {
            string line1 = r.Label.Replace("学生宿舍", "") + "  " + r.BalanceText;
            string line2 = "每 " + _cfg.IntervalMinutes + " 分钟自动刷新";
            if (_daysLeft.HasValue)
                line2 = "约可用 " + _daysLeft.Value.ToString("0.0") + " 天 · " + line2;
            else if (_dailyUsage.HasValue)
                line2 = "日均 " + _dailyUsage.Value.ToString("0.0") + " 度/天 · " + line2;

            if (_miStatus != null) _miStatus.Text = line1 + "   (" + line2 + ")";
            _tray.Text = Trim(AppInfo.Title + "  " + line1);
        }

        private void NotifyLow(RoomInfo r)
        {
            string msg = r.Label + " 剩余 " + r.BalanceText;
            if (_daysLeft.HasValue)
                msg += "，约可用 " + _daysLeft.Value.ToString("0.0") + " 天";

            bool shown = false;

            // 自绘弹窗和托盘气泡都锚定屏幕右下角，同时触发会互相遮挡，
            // 因此这里只启用一种（互斥，不是叠加）：
            //   优先自绘弹窗 —— 置顶且不受系统「专注助手」静音影响；
            //   关掉弹窗后才退回托盘气泡 —— 好处是通知会留在 Windows 通知中心可回查。
            if (_cfg.Popup)
            {
                try
                {
                    if (_alert != null)
                    {
                        _alert.Close();
                        _alert = null;
                    }
                    _alert = new AlertForm(r, _cfg.Threshold, _daysLeft, _dailyUsage,
                                           _cfg.Sound);
                    _alert.FormClosed += delegate { _alert = null; };
                    _alert.Show();
                    shown = true;
                    Log.Write("提醒方式: 自绘弹窗（已避免与系统通知重叠）");
                }
                catch (Exception ex)
                {
                    Log.Write("弹窗失败，改用托盘气泡: " + ex.Message);
                }
            }

            if (!shown && _cfg.Balloon)
            {
                try
                {
                    _tray.BalloonTipTitle = "宿舍电量不足";
                    _tray.BalloonTipText = msg;
                    _tray.BalloonTipIcon = ToolTipIcon.Warning;
                    _tray.ShowBalloonTip(15000);
                    shown = true;
                    Log.Write("提醒方式: 系统托盘通知");
                }
                catch (Exception ex)
                {
                    Log.Write("气泡提醒失败: " + ex.Message);
                }
            }

            if (!shown && _cfg.Sound)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); }
                catch { }
            }
        }

        private void OpenDetail()
        {
            RoomInfo r = _lastRoom;
            if (r == null)
            {
                MessageBox.Show("还没有成功查询过，暂时拿不到房间信息。\r\n请先用「立即查询」查一次。",
                    AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string url = _cfg.BaseUrl + "/user/powerfee/toDailyDetails?implType=" +
                         Uri.EscapeDataString(_cfg.ImplType) + "&type=2" +
                         "&schoolAreaNo=" + Uri.EscapeDataString(_cfg.SchoolAreaNo) +
                         "&buildingNo=" + Uri.EscapeDataString(_cfg.BuildingNo) +
                         "&roomNum=" + Uri.EscapeDataString(r.RoomNum) +
                         "&from=wxminiprogram";

            try { Process.Start(url); }
            catch (Exception ex) { Log.Write("打开明细失败: " + ex.Message); }
        }

        private void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm(_cfg))
            {
                DialogResult dr = f.ShowDialog();
                if (dr == DialogResult.OK)
                {
                    _miSound.Checked = _cfg.Sound;
                    _miAutoStart.Checked = _cfg.AutoStart;
                    Log.Write("设置变更，立即重新查询");
                    _nextRun = DateTime.MinValue;
                    QueryNow(false);
                }
            }
        }

        private static void ApplyAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = Application.ExecutablePath;
                        k.SetValue(AppInfo.RunKey, "\"" + exe + "\"");
                    }
                    else
                    {
                        if (k.GetValue(AppInfo.RunKey) != null) k.DeleteValue(AppInfo.RunKey, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("设置开机自启失败: " + ex.Message);
            }
        }

        internal static void DisableAutoStart()
        {
            ApplyAutoStart(false);
        }

        private void ExitApp()
        {
            try
            {
                _tick.Stop();
                if (_alert != null) { _alert.Close(); _alert = null; }
                _tray.Visible = false;
                _tray.Dispose();
                if (_marshal != null) { _marshal.Dispose(); }
            }
            catch { }
            Log.Write("程序退出");
            ExitThread();
        }

        private static string Trim(string s)
        {
            // NotifyIcon.Text 有长度上限，超长会抛异常
            if (s == null) return "";
            if (s.Length <= 120) return s;
            return s.Substring(0, 117) + "...";
        }

        private static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "...";
        }
    }

    // ========================================================================
    //  入口
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 所有模式都必须先启用 TLS 1.2，否则 HTTPS 会报
            // "未能创建 SSL/TLS 安全通道"。放在最前面以免遗漏某个入口。
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (args != null && args.Length > 0)
            {
                string a = args[0].ToLowerInvariant();
                if (a == "--selftest")
                {
                    SelfTest();
                    return;
                }
                if (a == "--uninstall-autostart")
                {
                    TrayContext.DisableAutoStart();
                    return;
                }
                if (a == "--check")
                {
                    CheckMode();
                    return;
                }
                if (a == "--test-alert")
                {
                    TestAlert();
                    return;
                }
                if (a == "--dump-ui")
                {
                    DumpUi();
                    return;
                }
                if (a == "--dump-settings")
                {
                    DumpSettings();
                    return;
                }
            }

            bool createdNew;
            using (Mutex mtx = new Mutex(true, AppInfo.MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("程序已经在运行了，请看右下角托盘区的「电」图标。",
                        AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("未处理异常: " + e.Exception.ToString());
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    Log.Write("致命异常: " + (ex != null ? ex.ToString() : "unknown"));
                };

                Application.Run(new TrayContext());
            }
        }

        /// <summary>
        /// 无界面自检：查询一次并把结果写入 check.txt，便于脚本化验证而不会弹窗阻塞。
        /// </summary>
        private static void CheckMode()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Config cfg = Config.Load();
            QueryResult qr = PowerApi.Query(cfg);

            StringBuilder sb = new StringBuilder();
            if (qr.Ok)
            {
                sb.AppendLine("OK");
                sb.AppendLine("label=" + qr.Room.Label);
                sb.AppendLine("roomNum=" + qr.Room.RoomNum);
                sb.AppendLine("balance=" + qr.Room.Balance.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("balanceText=" + qr.Room.BalanceText);
                sb.AppendLine("totalRooms=" + qr.TotalRooms);
            }
            else
            {
                sb.AppendLine("FAIL");
                sb.AppendLine("error=" + qr.Error);
                if (qr.Diag.Length > 0) sb.AppendLine("diag=" + qr.Diag);
            }
            sb.AppendLine("config=" + Paths.Config);
            File.WriteAllText(Path.Combine(Paths.DataDir, "check.txt"), sb.ToString(),
                new UTF8Encoding(false));
        }

        /// <summary>
        /// 无界面验证设置窗口：等待房间列表加载，然后切换各级下拉框，
        /// 把每一步的状态导出到 settings_dump.txt，用来确认级联逻辑正确。
        /// </summary>
        private static void DumpSettings()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config cfg = Config.Load();

            SettingsForm f = new SettingsForm(cfg);
            f.Show();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("配置: " + cfg.Campus + " / " + cfg.Building + " / " + cfg.Room +
                          "  roomNum=" + cfg.RoomNum);
            sb.AppendLine();

            // 等异步加载完成（最多 25 秒）
            int waited = 0;
            while (!f.IsLoaded && waited < 25000)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(100);
                waited += 100;
            }
            Application.DoEvents();
            sb.AppendLine("加载耗时 " + waited + " ms，已加载: " + f.IsLoaded);
            sb.AppendLine();

            sb.AppendLine(f.DumpState("1. 初始状态"));

            // 用下标导航，不写死任何校区/楼栋名（那些名字会被编译进 exe）
            int secondCampus = f.CampusCount > 1 ? 1 : 0;
            f.DebugSetCampusIndex(secondCampus);
            Application.DoEvents();
            sb.AppendLine(f.DumpState("2. 切到另一个校区（楼栋/房间应随之刷新）"));

            f.DebugSetBuildingIndex(0);
            Application.DoEvents();
            sb.AppendLine(f.DumpState("3. 该校区第 1 个楼栋"));

            f.DebugSetRoomIndex(0);
            Application.DoEvents();
            sb.AppendLine(f.DumpState("4. 该楼栋第 1 个房间"));

            // 保存往返测试：切到别的房间保存，验证写盘内容，再还原
            string oCampus = cfg.Campus, oBuilding = cfg.Building, oRoom = cfg.Room;
            string oThr = cfg.Threshold.ToString("0.##", CultureInfo.InvariantCulture);
            sb.AppendLine();
            sb.AppendLine("--- 5. 保存往返测试（临时切到上面选中的房间）---");
            bool saved = f.DebugSave();
            sb.AppendLine("  DebugSave 返回: " + saved);
            sb.AppendLine("  内存配置: " + cfg.Campus + " / " + cfg.Building + " / " + cfg.Room +
                          "  roomNum=" + cfg.RoomNum +
                          "  areaNo=" + cfg.SchoolAreaNo + "  bldgNo=" + cfg.BuildingNo);
            sb.AppendLine("  写盘内容(房间相关行):");
            foreach (string line in File.ReadAllLines(Paths.Config, Encoding.UTF8))
            {
                string t = line.Trim();
                if (t.StartsWith("campus") || t.StartsWith("building") ||
                    t.StartsWith("room") || t.StartsWith("schoolAreaNo"))
                    sb.AppendLine("    " + t);
            }

            f.DebugSet(oCampus, oBuilding, oRoom);
            Application.DoEvents();
            f.DebugSave();
            sb.AppendLine("  已还原: " + cfg.Campus + " / " + cfg.Building + " / " + cfg.Room +
                          "  roomNum=" + cfg.RoomNum +
                          "  areaNo=" + cfg.SchoolAreaNo + "  bldgNo=" + cfg.BuildingNo);

            // 自定义阈值测试：可选可填，非法值必须被拒绝
            sb.AppendLine();
            sb.AppendLine("--- 6. 自定义提醒阈值 ---");
            f.DebugSetThreshold("37.5");
            Application.DoEvents();
            sb.AppendLine(f.DumpState("   键入 37.5 后的状态"));
            bool okCustom = f.DebugSave();
            sb.AppendLine("   保存返回: " + okCustom + "   cfg.Threshold=" +
                          cfg.Threshold.ToString("0.##", CultureInfo.InvariantCulture) +
                          "   (应为 True / 37.5)");

            f.DebugSetThreshold("abc");
            Application.DoEvents();
            bool okBad = f.DebugSave();
            sb.AppendLine("   键入 \"abc\" 保存返回: " + okBad +
                          "   cfg.Threshold 未变=" +
                          (cfg.Threshold == 37.5 ? "是" : "否") + "   (应 False / 是)");

            f.DebugSetThreshold("99999");
            Application.DoEvents();
            bool okRange = f.DebugSave();
            sb.AppendLine("   键入 \"99999\"(超范围) 保存返回: " + okRange + "   (应为 False)");

            f.DebugSetThreshold(oThr);
            Application.DoEvents();
            f.DebugSave();
            sb.AppendLine("   已还原阈值: " +
                          cfg.Threshold.ToString("0.##", CultureInfo.InvariantCulture));

            f.Close();
            Application.DoEvents();

            File.WriteAllText(Path.Combine(Paths.DataDir, "settings_dump.txt"),
                              sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>预览低电量弹窗（示例数据），用于确认提醒样式是否符合预期。</summary>
        private static void TestAlert()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config cfg = Config.Load();

            RoomInfo r = new RoomInfo();
            r.RoomNum = cfg.RoomNum;
            r.Campus = cfg.Campus;
            r.Building = cfg.Building;
            r.Room = cfg.Room;
            r.Balance = 8.6;
            r.BalanceText = "8.60度";

            AlertForm f = new AlertForm(r, cfg.Threshold, 1.4, 6.1, cfg.Sound);
            Application.Run(f);
        }

        /// <summary>
        /// 无视觉验证：构建弹窗、渲染到位图、统计像素并导出所有控件的文本与坐标。
        /// 用于在没有图形界面的环境下确认 UI 布局与中文渲染是否正常。
        /// </summary>
        private static void DumpUi()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config cfg = Config.Load();

            RoomInfo r = new RoomInfo();
            r.RoomNum = cfg.RoomNum;
            r.Campus = cfg.Campus;
            r.Building = cfg.Building;
            r.Room = cfg.Room;
            r.Balance = 8.6;
            r.BalanceText = "8.60度";

            AlertForm f = new AlertForm(r, cfg.Threshold, 1.4, 6.1, false);
            f.Show();
            Pump();
            System.Threading.Thread.Sleep(500);
            Pump();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("visible=" + f.Visible);
            sb.AppendLine("handle=" + f.Handle.ToInt64());
            sb.AppendLine("bounds=" + f.Bounds.X + "," + f.Bounds.Y + "," +
                          f.Width + "x" + f.Height);
            sb.AppendLine("topmost=" + f.TopMost);
            sb.AppendLine("controls=" + f.Controls.Count);
            DumpControls(f, sb, "  ");

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            sb.AppendLine("expectX=" + (wa.Right - f.Width - 16) + " actualX=" + f.Location.X);
            sb.AppendLine("expectY=" + (wa.Bottom - f.Height - 16) + " actualY=" + f.Location.Y);

            Bitmap bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(Path.Combine(Paths.DataDir, "ui_preview.png"),
                     System.Drawing.Imaging.ImageFormat.Png);

            int red = 0, dark = 0, white = 0, other = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 150 && c.G < 100 && c.B < 100) red++;
                    else if (c.R < 120 && c.G < 120 && c.B < 120) dark++;
                    else if (c.R > 235 && c.G > 235 && c.B > 235) white++;
                    else other++;
                }
            }
            sb.AppendLine("pixels: accent=" + red + " darkText=" + dark +
                          " white=" + white + " other=" + other);
            sb.AppendLine("ui_preview.png=" + Path.Combine(Paths.DataDir, "ui_preview.png"));
            bmp.Dispose();

            f.Close();
            Pump();

            File.WriteAllText(Path.Combine(Paths.DataDir, "ui_dump.txt"),
                              sb.ToString(), new UTF8Encoding(false));
        }

        private static void Pump()
        {
            Application.DoEvents();
        }

        private static void DumpControls(Control parent, StringBuilder sb, string indent)
        {
            foreach (Control c in parent.Controls)
            {
                string text = "";
                try { text = c.Text; }
                catch { }
                sb.AppendLine(indent + c.GetType().Name +
                              " [" + c.Bounds.X + "," + c.Bounds.Y + " " +
                              c.Width + "x" + c.Height + "] " +
                              "visible=" + c.Visible + " text=\"" + text + "\"");
                if (c.Controls.Count > 0) DumpControls(c, sb, indent + "  ");
            }
        }

        /// <summary>命令行自检：查询一次并弹窗显示结果，用于快速验证接口是否可用。</summary>
        private static void SelfTest()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Config cfg = Config.Load();
            QueryResult qr = PowerApi.Query(cfg);
            if (qr.Ok)
            {
                MessageBox.Show(
                    "接口自检成功。\r\n\r\n" +
                    "房间：" + qr.Room.Label + "\r\n" +
                    "内部编号：" + qr.Room.RoomNum + "\r\n" +
                    "剩余电量：" + qr.Room.BalanceText + "\r\n" +
                    "本次共获取 " + qr.TotalRooms + " 个房间的数据。",
                    AppInfo.Title + " - 自检", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("接口自检失败：\r\n\r\n" + qr.Error,
                    AppInfo.Title + " - 自检", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
