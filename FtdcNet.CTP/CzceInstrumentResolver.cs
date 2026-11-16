/////////////////////////////////////////////////////////////////////////
//// 上期技术 Ftdc C++ => .Net Framework Adapter
//// Author : shawn666.liu@hotmail.com
/////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace CTP
{
    /// <summary>
    /// 郑商所（CZCE）合约代码解析器。
    ///
    /// 郑商所老品种在 CTP 前置中通常使用 3 位年月（如 SA705），
    /// 而部分行情/策略配置使用 4 位年月（如 SA2705）；新品种（如 SO2707）
    /// 则可能本身就是 4 位。本类通过查询前置的合约列表，建立
    /// “配置代码 → 前置实际 InstrumentID”的映射。
    ///
    /// 典型用法：
    /// <code>
    /// var resolver = new CzceInstrumentResolver(td);
    /// resolver.LoadAll(++requestId);                  // 一次性加载全部 CZCE 合约
    /// var id = resolver.Resolve("SA2705");            // → "SA705"
    /// // 或按需解析（只查询用到的合约）：
    /// var id2 = resolver.ResolveOnDemand("SA2705", () => ++requestId);
    /// </code>
    ///
    /// 注意：这些方法会阻塞等待 CTP 查询回调，不要在 CTP 回调线程或 UI 线程调用。
    /// LoadAll 会查询全部 CZCE 合约，耗时取决于前置（实测 SimNow 2315 条约 0.5 秒，
    /// 前置繁忙时可能更久）。只需要少量合约时优先使用 ResolveOnDemand。
    /// </summary>
    public sealed class CzceInstrumentResolver
    {
        private readonly FtdcTdAdapter _td;
        private readonly object _sync = new object();
        private readonly object _queryGate = new object();
        private readonly Dictionary<string, string> _canonical =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byKey =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private volatile bool _loaded;

        public CzceInstrumentResolver(FtdcTdAdapter td)
        {
            _td = td ?? throw new ArgumentNullException(nameof(td));
        }

        /// <summary>是否已通过 LoadAll 加载过完整合约列表。</summary>
        public bool IsLoaded => _loaded;

        /// <summary>已知的 CZCE 合约数量（含期权等非期货合约）。</summary>
        public int Count
        {
            get { lock (_sync) return _canonical.Count; }
        }

        /// <summary>别名（4 位/3 位等非标准写法）到前置实际合约代码的映射副本。</summary>
        public IDictionary<string, string> Aliases
        {
            get
            {
                lock (_sync)
                    return new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 加载全部 CZCE 合约并建立映射。请求编号由调用方保证唯一。
        /// 阻塞等待查询完成。
        /// </summary>
        public int LoadAll(int requestId, TimeSpan? timeout = null)
        {
            lock (_queryGate)
            {
                var rows = Query(string.Empty, requestId, timeout ?? TimeSpan.FromMinutes(3));
                lock (_sync)
                {
                    foreach (var row in rows)
                        AddInstrument(row);
                    _loaded = true;
                    return _canonical.Count;
                }
            }
        }

        /// <summary>
        /// 按需查询并解析一个合约。会先尝试原代码，再尝试 3 位/4 位互换形式。
        /// requestIdFactory 每次调用都必须返回一个未被占用的请求编号。
        /// 阻塞等待查询完成。
        /// </summary>
        public string ResolveOnDemand(
            string instrumentId,
            Func<int> requestIdFactory,
            TimeSpan? timeout = null)
        {
            if (requestIdFactory == null)
                throw new ArgumentNullException(nameof(requestIdFactory));

            var resolved = Resolve(instrumentId);
            if (resolved != null)
                return resolved;
            if (_loaded)
                return null;

            var normalized = Normalize(instrumentId);
            if (string.IsNullOrEmpty(normalized))
                return null;

            var candidates = BuildCandidates(normalized);
            lock (_queryGate)
            {
                foreach (var candidate in candidates)
                {
                    var cached = Resolve(candidate);
                    if (cached != null)
                        return cached;

                    var rows = Query(candidate, requestIdFactory(), timeout ?? TimeSpan.FromSeconds(30));
                    foreach (var row in rows)
                    {
                        if (!IsCzce(row) ||
                            !string.Equals(row.InstrumentID, candidate, StringComparison.OrdinalIgnoreCase))
                            continue;

                        lock (_sync)
                        {
                            AddInstrument(row);
                            _aliases[normalized] = row.InstrumentID;
                        }
                        return row.InstrumentID;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// 从已加载/已解析的缓存中同步解析。未命中返回 null。
        /// </summary>
        public string Resolve(string instrumentId)
        {
            var normalized = Normalize(instrumentId);
            if (string.IsNullOrEmpty(normalized))
                return null;

            lock (_sync)
            {
                if (_canonical.ContainsKey(normalized))
                    return normalized;

                if (_aliases.TryGetValue(normalized, out var alias))
                    return alias;

                var key = BuildKey(normalized);
                if (key != null && _byKey.TryGetValue(key, out var canonical))
                    return canonical;
            }
            return null;
        }

        /// <summary>未命中时返回原始代码，适合直接传给订阅/查询接口。</summary>
        public string ResolveOrOriginal(string instrumentId)
        {
            return Resolve(instrumentId) ?? instrumentId;
        }

        public bool TryResolve(string instrumentId, out string canonical)
        {
            canonical = Resolve(instrumentId);
            return canonical != null;
        }

        // ------------------------------------------------------------------

        private List<ThostFtdcInstrumentField> Query(
            string instrumentId,
            int requestId,
            TimeSpan timeout)
        {
            var rows = new List<ThostFtdcInstrumentField>();
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                void Handler(object sender, OnRspEventArgs e)
                {
                    if (e.EventType != EnumOnRspType.OnRspQryInstrument || e.RequestID != requestId)
                        return;

                    if (e.RspInfo != null && e.RspInfo.ErrorID != 0)
                    {
                        error = new InvalidOperationException(
                            $"CTP instrument query failed: {e.RspInfo.ErrorID}:{e.RspInfo.ErrorMsg}");
                        done.Set();
                        return;
                    }

                    if (e.Param != IntPtr.Zero)
                        rows.Add(Conv.P2S<ThostFtdcInstrumentField>(e.Param));

                    if (e.IsLast)
                        done.Set();
                }

                _td.OnRspEvent += Handler;
                try
                {
                    var ret = _td.ReqQryInstrument(new ThostFtdcQryInstrumentField
                    {
                        InstrumentID = instrumentId ?? string.Empty,
                        ExchangeID = "CZCE",
                    }, requestId);
                    if (ret != 0)
                        throw new InvalidOperationException($"ReqQryInstrument returned {ret}");

                    if (!done.Wait(timeout))
                        throw new TimeoutException(
                            $"CTP instrument query timed out after {timeout.TotalSeconds:F0}s");
                    if (error != null)
                        throw error;
                    return rows;
                }
                finally
                {
                    _td.OnRspEvent -= Handler;
                }
            }
        }

        private void AddInstrument(ThostFtdcInstrumentField instrument)
        {
            if (!IsCzce(instrument) || string.IsNullOrEmpty(instrument.InstrumentID))
                return;

            // 精确代码始终可解析
            _canonical[instrument.InstrumentID] = instrument.InstrumentID;

            if (instrument.ProductClass != EnumProductClassType.Futures)
                return;

            if (!TryParse(instrument.InstrumentID, out var product, out var suffix))
                return;

            var key = BuildKey(product, suffix, out var alias);
            if (key == null)
                return;

            if (!_byKey.ContainsKey(key))
                _byKey[key] = instrument.InstrumentID;

            if (!string.IsNullOrEmpty(alias) && !_aliases.ContainsKey(alias))
                _aliases[alias] = instrument.InstrumentID;
        }

        private static bool IsCzce(ThostFtdcInstrumentField instrument)
        {
            return instrument != null &&
                   string.Equals(instrument.ExchangeID, "CZCE", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string instrumentId)
        {
            return string.IsNullOrWhiteSpace(instrumentId)
                ? null
                : instrumentId.Trim().ToUpperInvariant();
        }

        private static List<string> BuildCandidates(string normalized)
        {
            var result = new List<string> { normalized };

            if (TryParse(normalized, out var product, out var suffix))
            {
                if (suffix.Length == 4)
                {
                    var alt = product + suffix.Substring(1);
                    if (!result.Contains(alt))
                        result.Add(alt);
                }
                else if (suffix.Length == 3)
                {
                    var key = BuildKey(product, suffix, out _);
                    if (key != null && !result.Contains(key))
                        result.Add(key);
                }
            }
            return result;
        }

        private static string BuildKey(string instrumentId)
        {
            return TryParse(instrumentId, out var product, out var suffix)
                ? BuildKey(product, suffix, out _)
                : null;
        }

        private static string BuildKey(string product, string suffix, out string alternate)
        {
            alternate = null;
            if (suffix.Length == 4)
            {
                // 前置 4 位：SO2707；别名是去掉世纪位的 3 位 SO707
                alternate = product + suffix.Substring(1);
                return product + suffix;
            }

            if (suffix.Length != 3)
                return null;

            var yearDigit = suffix[0] - '0';
            if (!int.TryParse(suffix.Substring(1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var month) ||
                month < 1 || month > 12)
                return null;

            var year = ExpandYear(yearDigit);
            var full = product + (year % 100).ToString("D2", CultureInfo.InvariantCulture) +
                       month.ToString("D2", CultureInfo.InvariantCulture);
            alternate = full;
            return full;
        }

        private static int ExpandYear(int yearDigit)
        {
            // 取不早于当前年份、且末位数字匹配的年份；例如 2026 年时：
            // 7 -> 2027, 6 -> 2026, 0 -> 2030。
            var year = DateTime.Now.Year;
            while (year % 10 != yearDigit)
                year++;
            return year;
        }

        private static bool TryParse(string instrumentId, out string product, out string suffix)
        {
            product = null;
            suffix = null;
            if (string.IsNullOrEmpty(instrumentId))
                return false;

            var i = 0;
            while (i < instrumentId.Length && char.IsLetter(instrumentId[i]))
                i++;
            if (i == 0 || i == instrumentId.Length)
                return false;

            for (var j = i; j < instrumentId.Length; j++)
            {
                if (!char.IsDigit(instrumentId[j]))
                    return false;
            }

            var length = instrumentId.Length - i;
            if (length != 3 && length != 4)
                return false;

            product = instrumentId.Substring(0, i).ToUpperInvariant();
            suffix = instrumentId.Substring(i);
            return true;
        }
    }
}
